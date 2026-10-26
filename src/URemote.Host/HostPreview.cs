using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SIPSorcery.Net;
using URemote.Core;
using URemote.Linux;
using URemote.Media;

// Foreground single-viewer experiment; input is enabled only by the explicit control CLI mode.
public static class HostPreview
{
    public static async Task RunAsync(UuSignalClient signal, string ffmpeg, IReadOnlyList<uint> outputs, CancellationToken ct, bool enableInput = false, Action<string>? report = null, bool enableAudio = false, bool enableClipboard = false, HostTerminalManager? terminalManager = null, Func<string,string,CancellationToken,Task>? assistance = null, Func<CancellationToken>? assistancePermission = null, Action? assistanceConnected = null, Action<HostControlConnection?>? connectionChanged = null, Func<string,CancellationToken,Task>? assistanceMode = null, Func<string>? encoderPath = null, Func<string, bool>? recyclePortal = null, Action<Action<string>>? bindCaptureRefresh = null)
    {
        report ??= Console.WriteLine;
        if (OperatingSystem.IsMacOS() && enableAudio) { enableAudio = false; report("audio-unavailable"); }
        await using var ownedTerminals = terminalManager is null ? new HostTerminalManager() : null;
        terminalManager ??= ownedTerminals!;
        var dimensions = new List<(int Width, int Height)>();
        foreach (var output in outputs)
        {
            var f = await DesktopBackend.CaptureAsync(output, ct: ct);
            dimensions.Add(((int)f.Width, (int)f.Height)); Array.Clear(f.Pixels);
        }
        var frameRateLimits = await ReadFrameRateLimitsAsync(outputs, report, ct);
        var profiles = new HostVideoSettings(dimensions.ToArray(), frameRateLimits: frameRateLimits);
        var sessions = new HostSignalSessions();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = stop.Token;
        var refreshRequests = Channel.CreateBounded<string>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        bindCaptureRefresh?.Invoke(reason => refreshRequests.Writer.TryWrite(reason));
        var refreshPump = bindCaptureRefresh is null ? Task.CompletedTask : RefreshPumpAsync();
        var candidates = Channel.CreateBounded<(HostPeerId Id, JsonObject Value)>(256);
        var disconnects = Channel.CreateUnbounded<CancellationTokenSource>(new() { SingleReader = true });
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
            refreshRequests.Writer.TryComplete();
            try { await refreshPump; } catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            await ClosePeerAsync();
        }

        async Task RefreshPumpAsync()
        {
            try
            {
                await foreach (var reason in refreshRequests.Reader.ReadAllAsync(token))
                {
                    try
                    {
                        await DesktopBackend.RefreshPortalAsync(enableInput, token);
                        report("portal-capture-refreshed;reason=" + reason);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                    catch (Exception e) when (e is IOException or InvalidOperationException or DesktopSessionClosedException or UnauthorizedAccessException or TimeoutException or NotSupportedException or FormatException)
                    {
                        report("portal-capture-refresh-failed;type=" + e.GetType().Name);
                        recyclePortal?.Invoke("refresh-failed");
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
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
            var signalReady = signal.Events.WaitToReadAsync(token).AsTask();
            var disconnectReady = disconnects.Reader.WaitToReadAsync(token).AsTask();
            while (true)
            {
                await Task.WhenAny(signalReady, disconnectReady);
                token.ThrowIfCancellationRequested();
                if (disconnectReady.IsCompleted)
                {
                    await disconnectReady;
                    while (disconnects.Reader.TryRead(out var requestedLifetime))
                    {
                        // A stale card must never disconnect a replacement session, even if its peer ID is reused.
                        if (!ReferenceEquals(requestedLifetime, peerStop)) continue;
                        try
                        {
                            using var sendDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                            sendDeadline.CancelAfter(TimeSpan.FromSeconds(3));
                            await signal.ClearControlRoomAsync(sendDeadline.Token);
                            report("control-room-clear-sent");
                        }
                        finally
                        {
                            // Always revoke local input/capture, including when signaling is unavailable.
                            // A signaling failure is handled by the outer host reconnect loop.
                            await ClosePeerAsync();
                            report("viewer-released");
                        }
                        report("viewer-disconnected-locally; publisher-kept-online");
                    }
                    disconnectReady = disconnects.Reader.WaitToReadAsync(token).AsTask();
                    continue;
                }
                if (!await signalReady) break;
                var hasFrame = signal.Events.TryRead(out var frame);
                signalReady = signal.Events.WaitToReadAsync(token).AsTask();
                if (!hasFrame || frame is null) continue;
                if (assistanceMode is not null && HostAssistance.ControlModeRequest(frame.Packet?.Data) is { } modeControl)
                {
                    try { await assistanceMode(modeControl, token); report("assistance-mode-answered;mode=by_password"); }
                    catch (Exception e) when (e is HttpRequestException or InvalidDataException or TimeoutException)
                    { report("assistance-mode-failed;type=" + e.GetType().Name); }
                    continue;
                }
                if (assistance is not null && frame.Packet?.Data is JsonArray push
                    && push.Count > 0 && push[0] is JsonValue pushName && pushName.TryGetValue<string>(out var pushEvent)
                    && pushEvent is "push" or "bmsg_push" && HostAssistance.Challenge(push) is { } challenge)
                {
                    report("assistance-challenge-received;allowed=" + !(assistancePermission?.Invoke().IsCancellationRequested ?? false));
                    try { await assistance(challenge.ControlId, challenge.Salt, token); report("assistance-challenge-answered"); }
                    catch (Exception e) when (e is HttpRequestException or InvalidDataException or TimeoutException)
                    { report("assistance-challenge-failed;type=" + e.GetType().Name); }
                    continue;
                }
                if (frame.Packet?.Data is JsonArray settingPacket && settingPacket.Count > 1 && settingPacket[1] is JsonObject settingObject)
                {
                    JsonObject? remoteCapability = (settingObject["data"] as JsonObject)?["device_capability"] as JsonObject;
                    if (settingObject["streamer_data"] is JsonValue streamerValue && streamerValue.TryGetValue<string>(out var streamerJson) && streamerJson.Length <= 65536)
                        try { remoteCapability = (JsonNode.Parse(streamerJson) as JsonObject)?["device_capability"] as JsonObject; } catch (System.Text.Json.JsonException) { }
                    if (remoteCapability?["video_codec_capability"] is JsonArray remoteCodecs)
                        foreach (var codec in remoteCodecs.Take(32).OfType<JsonObject>())
                        {
                            string Number(string key) => codec[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
                            report($"controller-codec-capability codec={Number("video_codec")} size={Number("width")}x{Number("height")} chroma={Number("chroma_sampling")} depth={Number("bit_depth")} implementation={Number("codec_impl")}");
                        }
                }
                HostSignalUpdate update;
                try { update = sessions.Accept(frame); }
                catch (FormatException e)
                {
                    // Reject this packet without allowing a malformed/stale notification to terminate another peer.
                    var field = e.Data["field"] as string;
                    var eventName = frame.Packet?.Data?[0]?.GetValue<string>();
                    report("rejected-event=" + (eventName is "soac" or "released" or "be-controlled" ? eventName : "other"));
                    report("signal-packet-rejected; field=" +
                        (field is "client_id" or "ice_id" or "app_control_id" or "type" or "sdp" ? field : "other"));
                    continue;
                }
                switch (update.Action)
                {
                    case HostSignalAction.Requested:
                        var quiet = DesktopBackend.PipelineQuietRemaining;
                        if (quiet > TimeSpan.Zero)
                        {
                            report($"peer-pipeline-quiet;milliseconds={(int)quiet.TotalMilliseconds}");
                            await Task.Delay(quiet, token);
                        }
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
                        report("session-type-value=" + update.Peer.Options.TypeValue);
                        var platform = request["controller_platform"]?.ToJsonString();
                        report("controller-platform=" + (int.TryParse(platform, out var platformCode) ? platformCode : -1));
                        // Android tablets can use a desktop-like ClientType but still render only
                        // the primary video track, just like iOS. Route screen selection onto it.
                        var mobileSingleStream = !dataOnly && (update.Peer.Options.ClientType == 1 || platformCode is 2 or 3);
                        frameRateLimits = await ReadFrameRateLimitsAsync(outputs, report, token);
                        profiles = new HostVideoSettings(dimensions.ToArray(), mobileSingleStream, frameRateLimits);
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
                        var notifiedConnection = 0;
                        var disconnectRequested = 0;
                        var connectionOptions = update.Peer.Options;
                        HostControlConnection? connectionInfo = null;
                        var connectionGate = new object();
                        _ = mediaLifetime.Token.Register(() => { if (active == id) connectionChanged?.Invoke(null); });
                        var isAssistance = update.Peer.Options.ControlConnectType == 2;
                        media.StateChanged += state =>
                        {
                            report("media-state=" + state);
                            if (active == id)
                            {
                                if (state == RTCPeerConnectionState.connected && !mediaLifetime.IsCancellationRequested
                                    && Interlocked.Exchange(ref notifiedConnection, 1) == 0)
                                {
                                    lock (connectionGate)
                                    {
                                        connectionInfo = HostControlConnection.FromRequest(request, connectionOptions) with
                                        {
                                            ViewOnly = !enableInput,
                                            RequestDisconnect = () =>
                                            {
                                                if (!token.IsCancellationRequested && Interlocked.Exchange(ref disconnectRequested, 1) == 0)
                                                    disconnects.Writer.TryWrite(mediaLifetime);
                                            }
                                        };
                                        connectionChanged?.Invoke(connectionInfo);
                                    }
                                }
                                else if (state is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected)
                                    connectionChanged?.Invoke(null);
                            }
                            if (isAssistance && state == RTCPeerConnectionState.connected && Interlocked.Exchange(ref notifiedAssistance, 1) == 0) assistanceConnected?.Invoke();
                            if (state is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected)
                                try { mediaLifetime.Cancel(); } catch (ObjectDisposedException) { }
                        };
                        var peerToken = peerStop.Token;
                        video = StreamAsync(media, encoderPath?.Invoke() ?? ffmpeg, dataOnly ? Array.Empty<uint>() : outputs, peerToken, enableInput, async () =>
                        {
                            await signal.SendEventAsync("ice_finished_log", new JsonObject
                            { ["app_control_id"] = id.AppControlId, ["ice_id"] = id.IceId }, ct: peerToken);
                            report("ice-completion-notified");
                        }, report, profiles, recyclePortal);
                        input = terminal ? HostTerminalPeer.RunAsync(media, terminalManager, enableInput && !OperatingSystem.IsMacOS(), peerStop.Token, report)
                            : HostInput.RunAsync(media, outputs, peerStop.Token, report, enableInput && !fileOnly, enableClipboard && !fileOnly, profiles, inputApplied: () =>
                            {
                                lock (connectionGate)
                                {
                                    if (active != id || mediaLifetime.IsCancellationRequested || connectionInfo is null) return;
                                    connectionInfo = connectionInfo with { HasInputActivity = true };
                                    connectionChanged?.Invoke(connectionInfo);
                                }
                            });
                        audio = !dataOnly && enableAudio ? StreamAudioAsync(media, report, peerStop.Token) : Task.CompletedTask;
                        var activeMedia = media;
                        void PeerFailed(Task task)
                        {
                            var error = task.Exception?.GetBaseException();
                            var site = Site(error);
                            // A planned portal recycle cancels this peer on purpose. InvalidOperationException
                            // from a stale async enumerator used to reconnect in a tight loop; reset once instead.
                            var reset = error is InvalidOperationException || (DesktopBackend.PipelineResetActive && error is OperationCanceledException or DesktopSessionClosedException or IOException or ObjectDisposedException);
                            if (reset)
                            {
                                report("peer-pipeline-reset;type=" + error?.GetType().Name + ";site=" + site);
                                // Capture health requests the portal recycle. This only breaks the reconnect storm.
                                if (error is InvalidOperationException) DesktopBackend.ArmPipelineQuiet(TimeSpan.FromSeconds(2));
                                try { mediaLifetime.Cancel(); } catch (ObjectDisposedException) { }
                                activeMedia.Dispose();
                                report("peer-pipeline-ended; publisher-kept-online");
                                report("viewer-released");
                                return;
                            }
                            report("peer-pipeline-failure;type=" + error?.GetType().Name + ";site=" + site);
                            try { mediaLifetime.Cancel(); } catch (ObjectDisposedException) { }
                            activeMedia.Dispose();
                            report("peer-pipeline-ended; publisher-kept-online");
                            report("viewer-released");
                        }
                        _ = input.ContinueWith(PeerFailed, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        _ = video.ContinueWith(PeerFailed, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        await signal.SendEventAsync("forward_setting", Capability(id, dimensions, frameRateLimits), ct: token);
                        await signal.SendEventAsync("forward_setting", new JsonObject
                        {
                            ["client_id"] = id.ClientId,
                            ["data"] = new JsonObject
                            {
                                ["type"] = "signal_app_data",
                                ["signal_app_data"] = new JsonObject { ["ice_id"] = id.IceId,
                                    ["binary_data"] = new JsonObject { ["_placeholder"] = true, ["num"] = 0 } }
                            }
                        }, [HostDisplayInfo.Encode(1, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), outputs.Count, dimensions, mobileSingleStream, frameRateLimits)], ct: token);
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
            connectionChanged?.Invoke(null);
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

    private static async Task StreamAsync(HostMediaPeer media, string ffmpeg, IReadOnlyList<uint> outputs, CancellationToken ct, bool enableInput, Func<Task> onConnected, Action<string> report, HostVideoSettings profiles, Func<string, bool>? recyclePortal)
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
                await StreamOutputAsync(media, ffmpeg, outputs[selected], selected, report, ct, profiles, recyclePortal, 0);
            }
            return;
        }
        using var streamsStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = outputs.Select((output, index) => StreamOutputAsync(media, ffmpeg, output, index, report, streamsStop.Token, profiles, recyclePortal)).ToList();
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
        Action<string> report, CancellationToken ct, HostVideoSettings profiles, Func<string, bool>? recyclePortal, int? videoTrack = null)
    {
        while (!ct.IsCancellationRequested)
        {
        if (profiles.SingleVideoStream && profiles.SelectedScreen != index) return;
        var profile = profiles.Get(index);
        var seenGeneration = DesktopBackend.PortalGeneration;
        CapturedScreen first;
        while (true)
        {
            try { first = await DesktopBackend.CaptureAsync(output, ct: ct); break; }
            catch (Exception e) when (RefreshInterrupt(e, seenGeneration))
            {
                report($"portal-capture-refresh-retry;screen={index + 1}");
                await DesktopBackend.WaitCaptureReadyAsync(ct);
                seenGeneration = DesktopBackend.PortalGeneration;
            }
        }
        await using var encoder = new StreamingH264Encoder(ffmpeg, (int)first.Width, (int)first.Height, first.YInverted, first.ShmFormat, profile.Fps, profile.Width, profile.Height, profile.Bitrate, profile.Crf);
        report($"video-encoder-config;screen={index + 1};width={profile.Width};height={profile.Height};fps={profile.Fps};bitrate={profile.Bitrate};crf={profile.Crf}");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var darkness = new StrongBox<int>(-1);
        var producer = Task.Run(async () =>
        {
            var frame = first;
            var blackFrames = 0;
            var blackStart = 0L;
            try
            {
                try
                {
                var cadence = System.Diagnostics.Stopwatch.StartNew();
                var period = (double)System.Diagnostics.Stopwatch.Frequency / profile.Fps;
                double nextFrame = 0;
                while (true)
                {
                    try
                    {
                        if (frame.Width != first.Width || frame.Height != first.Height || frame.ShmFormat != first.ShmFormat || frame.YInverted != first.YInverted)
                            throw new IOException("Display format changed; reconnect required.");
                        var dark = PortalCaptureHealth.NearBlack(frame.Pixels);
                        Volatile.Write(ref darkness.Value, dark ? 1 : 0);
                        if (dark)
                        {
                            if (blackFrames == 0) blackStart = System.Diagnostics.Stopwatch.GetTimestamp();
                            blackFrames++;
                            if (PortalCaptureHealth.BlackStreak(blackFrames, System.Diagnostics.Stopwatch.GetElapsedTime(blackStart)))
                            {
                                blackFrames = 0;
                                report($"portal-capture-stale;reason=low-luma;screen={index + 1}");
                                if (recyclePortal?.Invoke("low-luma") == true) break;
                            }
                        }
                        else blackFrames = 0;
                        await encoder.WriteAsync(frame.Pixels, (int)frame.Stride, stop.Token);
                    }
                    finally { Array.Clear(frame.Pixels); }
                    if (profiles.Get(index) != profile || (profiles.SingleVideoStream && profiles.SelectedScreen != index)
                        || media.ConsumeKeyFrameRequest(videoTrack ?? index)) break;
                    nextFrame += period;
                    var remaining = (nextFrame - cadence.ElapsedTicks) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                    if (remaining > 0)
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(remaining)), stop.Token);
                    else nextFrame = cadence.ElapsedTicks; // Skip missed deadlines instead of bursting to catch up.
                    try { frame = await DesktopBackend.CaptureAsync(output, ct: stop.Token); }
                    catch (Exception e) when (RefreshInterrupt(e, seenGeneration))
                    {
                        report($"portal-capture-refresh-retry;screen={index + 1}");
                        await DesktopBackend.WaitCaptureReadyAsync(stop.Token);
                        break;
                    }
                }
                }
                catch (Exception e) when (RefreshInterrupt(e, seenGeneration))
                {
                    report($"portal-capture-refresh-retry;screen={index + 1}");
                    await DesktopBackend.WaitCaptureReadyAsync(stop.Token);
                }
            }
            finally { Array.Clear(frame.Pixels); encoder.CompleteInput(); }
        });
        var consumer = Task.Run(async () =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var count = 0; long sentBytes = 0;
            var windowBytes = 0L; var windowFrames = 0; var lowWindows = 0;
            var windowStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
            await foreach (var encoded in encoder.ReadAsync(stop.Token))
            {
                try
                {
                    // The encoder produces a fixed-rate stream; pipe scheduling jitter is not media time.
                    var rtpDuration = (uint)(90000 / profile.Fps);
                    await media.SendH264PacedAsync(encoded, rtpDuration, videoTrack ?? index, profile.Bitrate, stop.Token);
                    sentBytes += encoded.Length;
                    windowBytes += encoded.Length;
                    count++;
                    windowFrames++;
                    if (count == 1 || count % 150 == 0)
                        report($"video-frames-sent={count};screen={index + 1};fps={count / watch.Elapsed.TotalSeconds:F1};encoded-mbps={sentBytes * 8 / watch.Elapsed.TotalSeconds / 1000000:F2}");
                    var windowElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(windowStart);
                    if (PortalCaptureHealth.WindowIsStale(windowFrames, windowElapsed, windowBytes)) lowWindows++;
                    else if (windowFrames >= 120 && windowElapsed >= TimeSpan.FromSeconds(2)) lowWindows = 0;
                    if (windowFrames >= 120 && windowElapsed >= TimeSpan.FromSeconds(2))
                    {
                        if (PortalCaptureHealth.StreakTriggers(lowWindows) && Volatile.Read(ref darkness.Value) != 0)
                        {
                            var fps = windowFrames / windowElapsed.TotalSeconds;
                            var mbps = windowBytes * 8d / windowElapsed.TotalSeconds / 1_000_000d;
                            report($"portal-capture-stale;reason=low-bitrate;screen={index + 1};fps={fps:F1};encoded-mbps={mbps:F2}");
                            lowWindows = 0;
                            if (recyclePortal?.Invoke("low-bitrate") == true) break;
                        }
                        windowBytes = 0; windowFrames = 0; windowStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                }
                finally { Array.Clear(encoded); }
            }
            }
            catch (InvalidOperationException)
            {
                report($"peer-video-transport-reset;screen={index + 1}");
                DesktopBackend.ArmPipelineQuiet(TimeSpan.FromSeconds(2));
                if (Volatile.Read(ref darkness.Value) != 0 || lowWindows > 0)
                    recyclePortal?.Invoke("low-bitrate");
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

    private static bool RefreshInterrupt(Exception error, int seenGeneration) =>
        error is DesktopSessionClosedException or ObjectDisposedException or IOException
        && (DesktopBackend.CaptureRefreshActive || DesktopBackend.PortalGeneration != seenGeneration);

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

    private static string Site(Exception? error)
    {
        var name = error?.TargetSite?.Name;
        if (string.IsNullOrEmpty(name) || name.Length > 64) return "unknown";
        foreach (var character in name)
            if (!char.IsLetterOrDigit(character) && character is not ('_' or '.')) return "unknown";
        return name;
    }

    private static async Task<int[]> ReadFrameRateLimitsAsync(IReadOnlyList<uint> outputs, Action<string> report, CancellationToken ct)
    {
        IReadOnlyDictionary<uint, int> rates;
        try { rates = await DesktopBackend.ReadRefreshRatesAsync(ct); }
        catch (Exception e) when (!ct.IsCancellationRequested && e is IOException or System.Net.Sockets.SocketException or OperationCanceledException or FormatException)
        { rates = new Dictionary<uint, int>(); report("display-refresh-unavailable;fallback-fps=60"); }
        return outputs.Select((output, index) =>
        {
            var milliHz = rates.GetValueOrDefault(output);
            var limit = HostDisplayInfo.FrameRateLimit(milliHz);
            report($"display-frame-rate;screen={index + 1};refresh-millihz={milliHz};max-fps={limit}");
            return limit;
        }).ToArray();
    }

    private static JsonObject Capability(HostPeerId id, IReadOnlyList<(int Width, int Height)> dimensions, IReadOnlyList<int> frameRateLimits) => new()
    {
        ["client_id"] = id.ClientId,
        ["data"] = new JsonObject
        {
            ["type"] = "device_capability",
            ["device_capability"] = new JsonObject
            {
                ["ice_id"] = id.IceId,
                ["display_info"] = new JsonArray(Enumerable.Range(0, dimensions.Count).Select(index => (JsonNode)new JsonObject
                    { ["id"] = index, ["fps"] = frameRateLimits[index], ["type"] = 0, ["hdr"] = -1 }).ToArray()),
                // This is the encoder's supported size, not the currently attached screen size.
                // Native clients intersect it with their decoder capability to enable quality tiers.
                // libx264 supports 4K H264 4:2:0 8-bit; no hardware, H265 or HDR is advertised.
                ["video_codec_capability"] = new JsonArray(new JsonObject { ["video_codec"] = 1,
                    ["width"] = 3840, ["height"] = 2160, ["chroma_sampling"] = 1, ["bit_depth"] = 8, ["codec_impl"] = -1 })
            }
        }
    };
}
