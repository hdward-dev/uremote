using System.Text.Json.Nodes;
using System.Threading.Channels;
using SIPSorcery.Net;
using URemote.Core;
using URemote.Linux;
using URemote.Media;

// Foreground single-viewer experiment; input is enabled only by the explicit control CLI mode.
public static class HostPreview
{
    public static async Task RunAsync(UuSignalClient signal, string ffmpeg, IReadOnlyList<uint> outputs, CancellationToken ct, bool enableInput = false, Action<string>? report = null, bool enableAudio = false, bool enableClipboard = false, HostTerminalManager? terminalManager = null, Func<string,string,CancellationToken,Task>? assistance = null, Func<CancellationToken>? assistancePermission = null, Action? assistanceConnected = null)
    {
        report ??= Console.WriteLine;
        await using var ownedTerminals = terminalManager is null ? new HostTerminalManager() : null;
        terminalManager ??= ownedTerminals!;
        var dimensions = new List<(int Width, int Height)>();
        foreach (var output in outputs)
        {
            var f = await WaylandScreenCapture.CaptureAsync(output, ct: ct);
            dimensions.Add(((int)f.Width, (int)f.Height)); Array.Clear(f.Pixels);
        }
        var profiles = new HostVideoSettings(dimensions.ToArray());
        var sessions = new HostSignalSessions();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = stop.Token;
        var candidates = Channel.CreateBounded<(HostPeerId Id, JsonObject Value)>(256);
        HostMediaPeer? media = null;
        HostPeerId? active = null;
        var answered = false;
        CancellationTokenSource? peerStop = null;
        Task video = Task.CompletedTask;
        Task input = Task.CompletedTask;
        Task audio = Task.CompletedTask;
        var sender = SendCandidatesAsync();
        var receiver = ReceiveAsync();
        try
        {
            await Task.WhenAny(sender, receiver);
            stop.Cancel();
            await Task.WhenAll(sender, receiver);
        }
        finally
        {
            stop.Cancel();
            await ClosePeerAsync();
        }

        async Task SendCandidatesAsync()
        {
            await foreach (var item in candidates.Reader.ReadAllAsync(token))
            {
                while (item.Id == active && !Volatile.Read(ref answered)) await Task.Delay(10, token);
                if (item.Id != active) continue;
                await signal.SendEventAsync("soac", new JsonObject
                {
                    ["client_id"] = item.Id.ClientId,
                    ["data"] = new JsonObject { ["type"] = "candidate", ["ice_id"] = item.Id.IceId,
                        ["app_control_id"] = item.Id.AppControlId, ["candidate"] = item.Value }
                }, ct: token);
            }
        }

        async Task ReceiveAsync()
        {
            await foreach (var frame in signal.Events.ReadAllAsync(token))
            {
                if (assistance is not null && frame.Packet?.Data is JsonArray push
                    && push[0]?.GetValue<string>() == "push" && HostAssistance.Challenge(push) is { } challenge)
                {
                    try { await assistance(challenge.ControlId, challenge.Salt, token); report("assistance-challenge-answered"); }
                    catch (Exception e) when (e is HttpRequestException or InvalidDataException or TimeoutException) { report("assistance-challenge-failed"); }
                    continue;
                }
                HostSignalUpdate update;
                try { update = sessions.Accept(frame); }
                catch (FormatException e)
                {
                    // Fail closed for the current peer, but keep the foreground listener available for a retry.
                    var field = e.Data["field"] as string;
                    var eventName = frame.Packet?.Data?[0]?.GetValue<string>();
                    report("rejected-event=" + (eventName is "soac" or "released" or "be-controlled" ? eventName : "other"));
                    report("signal-packet-rejected; field=" +
                        (field is "client_id" or "ice_id" or "app_control_id" or "type" or "sdp" ? field : "other"));
                    await ClosePeerAsync();
                    sessions.Reset();
                    report("viewer-released");
                    continue;
                }
                switch (update.Action)
                {
                    case HostSignalAction.Requested:
                        var assistanceToken = update.Peer!.Options.ControlConnectType == 2
                            ? assistancePermission?.Invoke() ?? CancellationToken.None : CancellationToken.None;
                        if(assistanceToken.IsCancellationRequested)
                        { sessions.Remove(update.Peer.Id); report("assistance-disabled-request-rejected"); break; }
                        if (active is not null && (peerStop?.IsCancellationRequested == true || active.ClientId == update.Peer!.Id.ClientId || media?.State is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected))
                            await ClosePeerAsync();
                        if (active is not null) { sessions.Remove(update.Peer!.Id); report("viewer-busy"); break; }
                        var request = (JsonObject)((JsonArray)frame.Packet!.Data!)[1]!;
                        var id = update.Peer!.Id;
                        var terminal = update.Peer.Options.CaptureType == 8;
                        var fileOnly = update.Peer.Options.CaptureType == 5;
                        var dataOnly = terminal || fileOnly;
                        report("session-capture-type=" + update.Peer.Options.CaptureType);
                        var platform = request["controller_platform"]?.ToJsonString();
                        report("controller-platform=" + (int.TryParse(platform, out var platformCode) ? platformCode : -1));
                        var mobileSingleStream = !terminal && (update.Peer.Options.ClientType == 1 || platformCode == 3);
                        profiles = new HostVideoSettings(dimensions.ToArray(), mobileSingleStream);
                        report("video-routing=" + (mobileSingleStream ? "selected-screen-on-primary-track" : "independent-screen-tracks"));
                        Volatile.Write(ref answered, false);
                        active = id;
                        media = new HostMediaPeer(ParseIceServers(request), forceRelay: request["force_relay"]?.GetValue<bool>() ?? false, activeVideoStreams: dataOnly ? 0 : mobileSingleStream ? 1 : outputs.Count, enableAudio: !dataOnly && enableAudio);
                        media.Diagnostic += report;
                        report("ice-policy-relay=" + (request["force_relay"]?.GetValue<bool>() ?? false));
                        media.LocalCandidate += value =>
                        {
                            media?.DescribeCandidate("local", value["candidate"]?.GetValue<string>() ?? "", value["sdpMLineIndex"]?.GetValue<int>() ?? -1);
                            if (!candidates.Writer.TryWrite((id, value))) stop.Cancel();
                        };
                        peerStop = CancellationTokenSource.CreateLinkedTokenSource(token, assistanceToken);
                        var mediaLifetime = peerStop;
                        var notifiedAssistance = 0;
                        var isAssistance = update.Peer.Options.ControlConnectType == 2;
                        media.StateChanged += state =>
                        {
                            report("media-state=" + state);
                            if (isAssistance && state == RTCPeerConnectionState.connected && Interlocked.Exchange(ref notifiedAssistance, 1) == 0) assistanceConnected?.Invoke();
                            if (state is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected)
                                try { mediaLifetime.Cancel(); } catch (ObjectDisposedException) { }
                        };
                        var peerToken = peerStop.Token;
                        video = StreamAsync(media, ffmpeg, dataOnly ? Array.Empty<uint>() : outputs, peerToken, enableInput, async () =>
                        {
                            await signal.SendEventAsync("ice_finished_log", new JsonObject
                            { ["app_control_id"] = id.AppControlId, ["ice_id"] = id.IceId }, ct: peerToken);
                            report("ice-completion-notified");
                        }, report, profiles);
                        input = terminal ? HostTerminalPeer.RunAsync(media, terminalManager, enableInput, peerStop.Token, report)
                            : HostInput.RunAsync(media, outputs, peerStop.Token, report, enableInput && !fileOnly, enableClipboard && !fileOnly, profiles);
                        audio = !dataOnly && enableAudio ? StreamAudioAsync(media, report, peerStop.Token) : Task.CompletedTask;
                        var activeMedia = media;
                        void PeerFailed(Task task)
                        {
                            _ = task.Exception;
                            try { mediaLifetime.Cancel(); } catch (ObjectDisposedException) { }
                            activeMedia.Dispose();
                            report("peer-pipeline-ended; publisher-kept-online");
                            report("viewer-released");
                        }
                        _ = input.ContinueWith(PeerFailed, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        _ = video.ContinueWith(PeerFailed, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        await signal.SendEventAsync("forward_setting", Capability(id, dimensions), ct: token);
                        await signal.SendEventAsync("forward_setting", new JsonObject
                        {
                            ["client_id"] = id.ClientId,
                            ["data"] = new JsonObject
                            {
                                ["type"] = "signal_app_data",
                                ["signal_app_data"] = new JsonObject { ["ice_id"] = id.IceId,
                                    ["binary_data"] = new JsonObject { ["_placeholder"] = true, ["num"] = 0 } }
                            }
                        }, [HostDisplayInfo.Encode(1, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), outputs.Count, dimensions, mobileSingleStream)], ct: token);
                        report("display-list-sent");
                        report(enableInput ? "viewer-requested; keyboard-mouse-enabled" : "viewer-requested; input-disabled");
                        break;
                    case HostSignalAction.Offer when update.Peer!.Id == active:
                        report("remote-offer-received");
                        var answer = await media!.AnswerAsync(update.Sdp!);
                        await sessions.SendAnswerAsync(signal, active!, answer, token);
                        Volatile.Write(ref answered, true);
                        report("native-answer-sent");
                        break;
                    case HostSignalAction.Candidate when update.Peer!.Id == active:
                        media!.AddRemoteCandidate(update.Candidate!);
                        break;
                    case HostSignalAction.Released when update.Peer!.Id == active:
                        await ClosePeerAsync();
                        report("viewer-released");
                        break;
                }
            }
        }

        async Task ClosePeerAsync()
        {
            if (active is { } oldPeer) sessions.Remove(oldPeer);
            active = null;
            Volatile.Write(ref answered, false);
            if (peerStop is not null) await peerStop.CancelAsync();
            try { await Task.WhenAll(video, input, audio); }
            catch (OperationCanceledException) when (peerStop?.IsCancellationRequested == true) { }
            catch { report("peer-cleanup-completed-after-pipeline-failure"); }
            finally
            {
                media?.Dispose(); media = null;
                peerStop?.Dispose(); peerStop = null;
            }
        }
    }

    private static async Task StreamAsync(HostMediaPeer media, string ffmpeg, IReadOnlyList<uint> outputs, CancellationToken ct, bool enableInput, Func<Task> onConnected, Action<string> report, HostVideoSettings profiles)
    {
        using var connectionDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectionDeadline.CancelAfter(TimeSpan.FromSeconds(45));
        while (media.State != RTCPeerConnectionState.connected)
        {
            if (media.State is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
                throw new IOException("Media connection failed.");
            try { await Task.Delay(100, connectionDeadline.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException("Media handshake timed out."); }
        }
        await onConnected();
        if (outputs.Count == 0) { await Task.Delay(Timeout.Infinite, ct); return; }
        if (profiles.SingleVideoStream)
        {
            while (!ct.IsCancellationRequested)
            {
                var selected = profiles.SelectedScreen;
                report("video-source-selected;screen=" + selected + ";track=0");
                await StreamOutputAsync(media, ffmpeg, outputs[selected], selected, report, ct, profiles, 0);
            }
            return;
        }
        using var streamsStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = outputs.Select((output, index) => StreamOutputAsync(media, ffmpeg, output, index, report, streamsStop.Token, profiles)).ToList();
        await Task.WhenAny(tasks);
        streamsStop.Cancel();
        await Task.WhenAll(tasks);
    }

    private static async Task StreamAudioAsync(HostMediaPeer media, Action<string> report, CancellationToken ct)
    {
        try
        {
            while (media.State != RTCPeerConnectionState.connected) await Task.Delay(100, ct);
            var pwCat = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(p => Path.Combine(p, "pw-cat")).FirstOrDefault(File.Exists) ?? throw new FileNotFoundException();
            await SystemAudioStream.RunAsync(media, pwCat, report, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { if (!ct.IsCancellationRequested) report("audio-unavailable"); }
    }

    private static async Task StreamOutputAsync(HostMediaPeer media, string ffmpeg, uint output, int index,
        Action<string> report, CancellationToken ct, HostVideoSettings profiles, int? videoTrack = null)
    {
        while (!ct.IsCancellationRequested)
        {
        if (profiles.SingleVideoStream && profiles.SelectedScreen != index) return;
        var profile = profiles.Get(index);
        var first = await WaylandScreenCapture.CaptureAsync(output, ct: ct);
        await using var encoder = new StreamingH264Encoder(ffmpeg, (int)first.Width, (int)first.Height, first.YInverted, first.ShmFormat, profile.Fps, profile.Width, profile.Height, profile.Bitrate, profile.Crf);
        report($"video-encoder-config;screen={index + 1};width={profile.Width};height={profile.Height};fps={profile.Fps};bitrate={profile.Bitrate};crf={profile.Crf}");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = Task.Run(async () =>
        {
            var frame = first;
            try
            {
                using var cadence = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / profile.Fps));
                while (true)
                {
                    try
                    {
                        if (frame.Width != first.Width || frame.Height != first.Height || frame.ShmFormat != first.ShmFormat || frame.YInverted != first.YInverted)
                            throw new IOException("Display format changed; reconnect required.");
                        await encoder.WriteAsync(frame.Pixels, (int)frame.Stride, stop.Token);
                    }
                    finally { Array.Clear(frame.Pixels); }
                    if (profiles.Get(index) != profile || (profiles.SingleVideoStream && profiles.SelectedScreen != index)) break;
                    await cadence.WaitForNextTickAsync(stop.Token);
                    frame = await WaylandScreenCapture.CaptureAsync(output, ct: stop.Token);
                }
            }
            finally { Array.Clear(frame.Pixels); encoder.CompleteInput(); }
        });
        var consumer = Task.Run(async () =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long lastTicks = 0; var count = 0;
            await foreach (var encoded in encoder.ReadAsync(stop.Token))
            {
                try
                {
                    var ticks = watch.ElapsedTicks;
                    var rtpDuration = lastTicks == 0 ? (uint)(90000 / profile.Fps) : (uint)Math.Clamp((ticks - lastTicks) * 90000 / System.Diagnostics.Stopwatch.Frequency, 1, 90000);
                    lastTicks = ticks;
                    media.SendH264(encoded, rtpDuration, videoTrack ?? index);
                    count++;
                    if (count == 1 || count % 150 == 0)
                        report($"video-frames-sent={count};screen={index + 1};fps={count / watch.Elapsed.TotalSeconds:F1}");
                }
                finally { Array.Clear(encoded); }
            }
        });
        try
        {
            var completed = await Task.WhenAny(producer, consumer);
            if (completed.IsFaulted || completed.IsCanceled || completed == consumer) stop.Cancel();
            await Task.WhenAll(producer, consumer);
        }
        finally { stop.Cancel(); Array.Clear(first.Pixels); }
        }
    }

    private static List<RTCIceServer> ParseIceServers(JsonObject request)
    {
        if (request["iceServers"] is not JsonArray servers || servers.Count > 32)
            throw new FormatException("Invalid ICE server list.");
        var result = new List<RTCIceServer>();
        foreach (var server in servers)
        {
            var urls = server?["urls"] is JsonArray array ? array.ToArray() : new[] { server?["urls"] };
            if (urls.Length > 16) throw new FormatException("Too many ICE URLs.");
            foreach (var url in urls)
            {
                var value = url?.GetValue<string>() ?? throw new FormatException("Missing ICE URL.");
                if (value.Length > 2048 || !(value.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("turn:", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("turns:", StringComparison.OrdinalIgnoreCase)))
                    throw new FormatException("Unsupported ICE URL.");
                result.Add(new RTCIceServer { urls = value, username = server?["username"]?.GetValue<string>(),
                    credential = server?["credential"]?.GetValue<string>() });
            }
        }
        return result;
    }

    private static JsonObject Capability(HostPeerId id, IReadOnlyList<(int Width, int Height)> dimensions) => new()
    {
        ["client_id"] = id.ClientId,
        ["data"] = new JsonObject
        {
            ["type"] = "device_capability",
            ["device_capability"] = new JsonObject
            {
                ["ice_id"] = id.IceId,
                ["display_info"] = new JsonArray(Enumerable.Range(0, dimensions.Count).Select(index => (JsonNode)new JsonObject
                    { ["id"] = index, ["fps"] = HostDisplayInfo.MaxSupportedFps, ["type"] = 0, ["hdr"] = -1 }).ToArray()),
                ["video_codec_capability"] = new JsonArray(new JsonObject { ["video_codec"] = 1,
                    ["width"] = dimensions.Max(x => x.Width), ["height"] = dimensions.Max(x => x.Height), ["chroma_sampling"] = 1, ["bit_depth"] = 8, ["codec_impl"] = -1 })
            }
        }
    };
}
