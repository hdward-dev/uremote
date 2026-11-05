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
    public static async Task RunAsync(UuSignalClient signal, string ffmpeg, IReadOnlyList<uint> outputs, CancellationToken ct, bool enableInput = false, Action<string>? report = null, bool enableAudio = false, bool enableClipboard = false, HostTerminalManager? terminalManager = null, Func<string,string,CancellationToken,Task>? assistance = null, Func<CancellationToken>? assistancePermission = null, Action? assistanceConnected = null, Action<HostControlConnection?>? connectionChanged = null, Func<string,CancellationToken,Task>? assistanceMode = null, Func<string>? encoderPath = null, Func<string, bool>? recyclePortal = null, Action<Action<string>>? bindCaptureRefresh = null,
        Func<CancellationToken, Task<PrivacyScreenImage?>>? loadScreenSaver = null)
    {
        report ??= Console.WriteLine;
        if (OperatingSystem.IsMacOS() && enableAudio) { enableAudio = false; report("audio-unavailable"); }
        await using var ownedTerminals = terminalManager is null ? new HostTerminalManager() : null;
        terminalManager ??= ownedTerminals!;
        // A missing first buffer used to wait 10s per screen and then throw, which
        // retried the whole host. Probe quickly, publish with a black placeholder,
        // and keep rebuilding only that stream until a real frame arrives.
        var tracker = new FirstFrameTracker(outputs.Count);
        var probed = await Task.WhenAll(outputs.Select(output => ProbeOutputAsync(output, ct)));
        var dimensions = new (int Width, int Height)[probed.Length];
        var waiting = new bool[probed.Length];
        for (var index = 0; index < probed.Length; index++)
        {
            tracker.Observe(index, probed[index].Pending, DesktopBackend.PortalGeneration, report);
            dimensions[index] = ((int)probed[index].Width, (int)probed[index].Height);
            waiting[index] = probed[index].Pending;
            Array.Clear(probed[index].Pixels);
        }
        var frameRateLimits = await ReadFrameRateLimitsAsync(outputs, report, ct);
        ApplyBoundModes(outputs, dimensions);
        var profiles = new HostVideoSettings(dimensions, frameRateLimits: frameRateLimits);
        BindCeilings(outputs, profiles);
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
        var captureHeld = 0;
        var sender = SendCandidatesAsync();
        var receiver = ReceiveAsync();
        var firstFrames = WatchFirstFramesAsync();
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
            try { await firstFrames; } catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            await ClosePeerAsync();
        }

        async Task WatchFirstFramesAsync()
        {
            var since = new long[outputs.Count];
            var generation = DesktopBackend.PortalGeneration;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var index = 0; index < waiting.Length; index++)
                if (waiting[index]) since[index] = now;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var current = DesktopBackend.PortalGeneration;
                    if (current != generation)
                    {
                        generation = current;
                        Array.Fill(waiting, true);
                        Array.Clear(since);
                    }
                    for (var index = 0; index < outputs.Count; index++)
                    {
                        if (!waiting[index]) continue;
                        CapturedScreen frame;
                        try { frame = await ProbeOutputAsync(outputs[index], token); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                        catch (Exception)
                        {
                            var size = DesktopBackend.OutputSize(outputs[index]);
                            frame = CapturePlaceholder.Create(outputs[index], size.Width >= 2 ? size.Width : 1280, size.Height >= 2 ? size.Height : 720);
                        }
                        try
                        {
                            tracker.Observe(index, frame.Pending, DesktopBackend.PortalGeneration, report);
                            if (!frame.Pending)
                            {
                                waiting[index] = false;
                                since[index] = 0;
                                var width = (int)frame.Width;
                                var height = (int)frame.Height;
                                if (!profiles.RejectsSourceSize(index, width, height))
                                {
                                    profiles.AdoptSourceSize(index, width, height);
                                    var adopted = profiles.Get(index);
                                    dimensions[index] = (adopted.Width, adopted.Height);
                                }
                                continue;
                            }
                            if (SessionLock.IsLocked == true)
                            {
                                since[index] = 0;
                                continue;
                            }
                            now = System.Diagnostics.Stopwatch.GetTimestamp();
                            if (since[index] == 0) since[index] = now;
                            else if (FirstFrameRetry.ShouldRebuild(System.Diagnostics.Stopwatch.GetElapsedTime(since[index]), SessionLock.IsLocked))
                            {
                                since[index] = now;
                                try
                                {
                                    if (await DesktopBackend.RebuildCaptureAsync(outputs[index], token))
                                        report($"capture-stream-rebuilt;screen={index + 1};reason=no-frame");
                                }
                                catch (Exception rebuild) when (rebuild is IOException or InvalidOperationException or TimeoutException
                                    or DesktopSessionClosedException or UnauthorizedAccessException or FormatException
                                    or NotSupportedException or ObjectDisposedException)
                                { }
                            }
                        }
                        finally { Array.Clear(frame.Pixels); }
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
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
                        DesktopBackend.ReportPortalOutputs(report);
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
                    // A dead media peer must not keep the previous app_control_id. Drop that key and
                    // read the packet again; a still-live viewer is left untouched.
                    if (!TryRecoverStaleControl(frame, e, out update))
                    {
                        var field = e.Data["field"] as string;
                        var eventName = frame.Packet?.Data?[0]?.GetValue<string>();
                        report("rejected-event=" + (eventName is "soac" or "released" or "be-controlled" ? eventName : "other"));
                        report("signal-packet-rejected; field=" +
                            (field is "client_id" or "ice_id" or "app_control_id" or "type" or "sdp" ? field : "other"));
                        continue;
                    }
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
                        var sessionEncoder = encoderPath?.Invoke() ?? ffmpeg;
                        var encoderImplementation = dataOnly ? -1 : await StreamingH264Encoder.ImplementationAsync(sessionEncoder, token);
                        var trueColor = !dataOnly && await StreamingH264Encoder.SupportsTrueColorAsync(sessionEncoder, token);
                        if (!dataOnly && outputs.Count > 0)
                        {
                            // Open pipelines now, in parallel with ICE and SDP. The log is synchronous.
                            var arming = DesktopBackend.ArmForViewerAsync(outputs, dataOnly, report, token);
                            Interlocked.Exchange(ref captureHeld, 1);
                            _ = ReportArmAsync(arming);
                        }
                        report("session-capture-type=" + update.Peer.Options.CaptureType);
                        report("session-type-value=" + update.Peer.Options.TypeValue);
                        var platform = request["controller_platform"]?.ToJsonString();
                        report("controller-platform=" + (int.TryParse(platform, out var platformCode) ? platformCode : -1));
                        // Android tablets can use a desktop-like ClientType but still render only
                        // the primary video track, just like iOS. Route screen selection onto it.
                        var mobileSingleStream = !dataOnly && (update.Peer.Options.ClientType == 1 || platformCode is 2 or 3);
                        frameRateLimits = await ReadFrameRateLimitsAsync(outputs, report, token);
                        ApplyBoundModes(outputs, dimensions);
                        profiles = new HostVideoSettings(((int Width, int Height)[])dimensions.Clone(), mobileSingleStream, frameRateLimits,
                            supportsTrueColor: trueColor, decoderCapabilities: update.Peer.Options.DecoderCapabilities);
                        BindCeilings(outputs, profiles);
                        if (trueColor && update.Peer.Options.Capture?.ChromaFormat == 3)
                            profiles.Apply(new(-1, 0, 0, 0, 0, 0, 0, null, ChromaFormat: 3));
                        foreach (var decoderLimit in update.Peer.Options.DecoderCapabilities.Where(c => c.CodecType == 1 && c.Width > 0 && c.Height > 0).Take(16))
                            report($"decoder-size-limit;codec=1;chroma={decoderLimit.ChromaFormat};width={decoderLimit.Width};height={decoderLimit.Height}");
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
                        video = StreamAsync(media, sessionEncoder, dataOnly ? Array.Empty<uint>() : outputs, peerToken, enableInput, async () =>
                        {
                            await signal.SendEventAsync("ice_finished_log", new JsonObject
                            { ["app_control_id"] = id.AppControlId, ["ice_id"] = id.IceId }, ct: peerToken);
                            report("ice-completion-notified");
                        }, report, profiles, recyclePortal, tracker);
                        input = terminal ? HostTerminalPeer.RunAsync(media, terminalManager, enableInput && !OperatingSystem.IsMacOS(), peerStop.Token, report)
                            : HostInput.RunAsync(media, outputs, peerStop.Token, report, enableInput && !fileOnly, enableClipboard && !fileOnly, profiles, inputApplied: () =>
                            {
                                lock (connectionGate)
                                {
                                    if (active != id || mediaLifetime.IsCancellationRequested || connectionInfo is null) return;
                                    connectionInfo = connectionInfo with { HasInputActivity = true };
                                    connectionChanged?.Invoke(connectionInfo);
                                }
                            }, loadScreenSaver: loadScreenSaver);
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
                            // Drop this control id now, before the receive loop reads the retry.
                            // RemoveIfMatch leaves a newer be-controlled that already replaced it.
                            sessions.RemoveIfMatch(id);
                            // Same path as a local disconnect: clear_out drops the server's control
                            // session, then ClosePeerAsync forgets whatever peer is still current.
                            if (!token.IsCancellationRequested && Interlocked.Exchange(ref disconnectRequested, 1) == 0)
                                disconnects.Writer.TryWrite(mediaLifetime);
                            try { mediaLifetime.Cancel(); } catch (ObjectDisposedException) { }
                            activeMedia.Dispose();
                            report("peer-pipeline-ended; publisher-kept-online");
                            report("viewer-released");
                        }
                        _ = input.ContinueWith(PeerFailed, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        _ = video.ContinueWith(PeerFailed, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        report($"encoder-codec-capability;codec=1;size=3840x2160;implementation={encoderImplementation};true-color={trueColor}");
                        await signal.SendEventAsync("forward_setting", Capability(id, dimensions, frameRateLimits, encoderImplementation, trueColor), ct: token);
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

            bool ControlMediaEnded() => active is null
                || peerStop?.IsCancellationRequested == true
                || media?.State is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected;

            bool TryRecoverStaleControl(UuSignalFrame signalFrame, FormatException error, out HostSignalUpdate recovered)
            {
                recovered = new(HostSignalAction.Ignored);
                var stale = error.Data["field"] as string == "app_control_id" || error.Message == "Conflicting peer identity.";
                if (!stale || !ControlMediaEnded() || !TryControlKey(signalFrame, out var client, out var ice)) return false;
                sessions.Remove(new HostPeerId(client, ice, "stale"));
                try { recovered = sessions.Accept(signalFrame); return true; }
                catch (FormatException) { return false; }
            }

            static bool TryControlKey(UuSignalFrame signalFrame, out string client, out string ice)
            {
                client = "";
                ice = "";
                if (signalFrame.Packet?.Data is not JsonArray values || values.Count < 2 || values[1] is not JsonObject payload) return false;
                if (payload["client_id"] is not JsonValue clientValue || !clientValue.TryGetValue<string>(out client!) || string.IsNullOrEmpty(client)) return false;
                var data = payload["data"] as JsonObject ?? payload;
                return data["ice_id"] is JsonValue iceValue && iceValue.TryGetValue<string>(out ice!) && !string.IsNullOrEmpty(ice);
            }
        }

        async Task ReportArmAsync(Task arming)
        {
            try { await arming.ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception e) when (e is IOException or InvalidOperationException or DesktopSessionClosedException
                or UnauthorizedAccessException or TimeoutException or NotSupportedException or FormatException or ObjectDisposedException)
            { report("capture-arm-failed;type=" + e.GetType().Name); }
        }

        async Task ClosePeerAsync()
        {
            if (Interlocked.Exchange(ref captureHeld, 0) == 1)
                DesktopBackend.ReleaseCapture();
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

    private static async Task StreamAsync(HostMediaPeer media, string ffmpeg, IReadOnlyList<uint> outputs, CancellationToken ct, bool enableInput, Func<Task> onConnected, Action<string> report, HostVideoSettings profiles, Func<string, bool>? recyclePortal, FirstFrameTracker tracker)
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
                await StreamOutputAsync(media, ffmpeg, outputs[selected], selected, report, ct, profiles, recyclePortal, tracker, 0);
            }
            return;
        }
        using var streamsStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = outputs.Select((output, index) => StreamOutputAsync(media, ffmpeg, output, index, report, streamsStop.Token, profiles, recyclePortal, tracker)).ToList();
        // One screen's capture stall is recovered inside StreamOutputAsync. It must not cancel the others.
        try { await Task.WhenAll(tasks); }
        finally
        {
            streamsStop.Cancel();
            try { await Task.WhenAll(tasks); }
            catch (Exception) { }
        }
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
        Action<string> report, CancellationToken ct, HostVideoSettings profiles, Func<string, bool>? recyclePortal, FirstFrameTracker tracker, int? videoTrack = null)
    {
        var rebuilds = 0;
        var foreign = 0;
        var gaveUp = false;
        while (!ct.IsCancellationRequested)
        {
        if (profiles.SingleVideoStream && profiles.SelectedScreen != index) return;
        var seenGeneration = DesktopBackend.PortalGeneration;
        try
        {
        var profile = profiles.Get(index);
        // X11 paces inside capture. Wayland pacing stays in this loop. Both read the same request.
        DesktopBackend.NoteRequestedFrameRate(output, profiles.RequestedFps(index));
        CapturedScreen first;
        while (true)
        {
            try { first = await ProbeOutputAsync(output, ct); break; }
            catch (Exception e) when (RefreshInterrupt(e, seenGeneration))
            {
                report($"portal-capture-refresh-retry;screen={index + 1}");
                await DesktopBackend.WaitCaptureReadyAsync(ct);
                seenGeneration = DesktopBackend.PortalGeneration;
            }
        }
        tracker.Observe(index, first.Pending, DesktopBackend.PortalGeneration, report);
        if (!first.Pending && profiles.RejectsSourceSize(index, (int)first.Width, (int)first.Height))
        {
            var foreignWidth = (int)first.Width;
            var foreignHeight = (int)first.Height;
            Array.Clear(first.Pixels);
            if (++foreign <= 2)
            {
                report($"capture-stream-rebuilt;screen={index + 1};reason=foreign-size;frame={foreignWidth}x{foreignHeight}");
                try { await DesktopBackend.RebuildCaptureAsync(output, ct); }
                catch (Exception refresh) when (refresh is IOException or InvalidOperationException or TimeoutException
                    or DesktopSessionClosedException or UnauthorizedAccessException or FormatException
                    or NotSupportedException or ObjectDisposedException)
                { }
                continue;
            }
            report($"capture-size-clamped;screen={index + 1};frame={foreignWidth}x{foreignHeight};display={profiles.Get(index).Width}x{profiles.Get(index).Height}");
            first = await ProbeOutputAsync(output, ct);
            tracker.Observe(index, first.Pending, DesktopBackend.PortalGeneration, report);
        }
        else foreign = 0;
        if (!first.Pending) profiles.AdoptSourceSize(index, (int)first.Width, (int)first.Height);
        profile = profiles.Get(index);
        await using var encoder = await StreamingH264Encoder.CreateAsync(ffmpeg, (int)first.Width, (int)first.Height, first.YInverted, first.ShmFormat, profile.Fps, profile.Width, profile.Height, profile.Bitrate, profile.Crf, stopToken: ct, chromaFormat: profile.ChromaFormat);
        report($"video-encoder-backend;screen={index + 1};codec={encoder.Codec};hardware={encoder.Hardware};inflight-limit={encoder.MaxInFlightFrames};input-pipe-bytes={encoder.InputPipeBytes}");
        report($"video-encoder-config;screen={index + 1};width={profile.Width};height={profile.Height};fps={profile.Fps};bitrate={profile.Bitrate};crf={profile.Crf};chroma={encoder.ChromaFormat};depth=8;hdr=false");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var darkness = new StrongBox<int>(-1);
        using var frames = new LatestCaptureFrames();
        var capturePump = Task.Run(async () =>
        {
            CapturedScreen? pending = first;
            Exception? failure = null;
            var pace = new CapturePace(profile.Fps, System.Diagnostics.Stopwatch.Frequency, System.Diagnostics.Stopwatch.GetTimestamp());
            try
            {
                while (true)
                {
                    var accepted = frames.Push(pending!);
                    pending = null;
                    if (!accepted) return;
                    var delay = pace.AfterFrame(System.Diagnostics.Stopwatch.GetTimestamp());
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, stop.Token);
                    pending = await ProbeOutputAsync(output, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception e) { failure = e; }
            finally
            {
                if (pending is not null) Array.Clear(pending.Pixels);
                frames.Complete(failure);
            }
        });
        var producer = Task.Run(async () =>
        {
            var blackFrames = 0;
            var blackStart = 0L;
            try
            {
                try
                {
                await using var latest = frames.ReadAsync(stop.Token).GetAsyncEnumerator();
                while (true)
                {
                    await encoder.WaitForCapacityAsync(stop.Token);
                    if (!await latest.MoveNextAsync()) break;
                    var frame = latest.Current;
                    try
                    {
                        tracker.Observe(index, frame.Pending, DesktopBackend.PortalGeneration, report);
                        var geometryChanged = frame.Width != first.Width || frame.Height != first.Height || frame.ShmFormat != first.ShmFormat || frame.YInverted != first.YInverted;
                        // The placeholder kept the host online. A real buffer with the monitor's
                        // actual geometry restarts this encoder without failing the session.
                        if (geometryChanged && first.Pending && !frame.Pending)
                        {
                            if (profiles.RejectsSourceSize(index, (int)frame.Width, (int)frame.Height))
                                throw new IOException("Capture size does not match this display.");
                            profiles.AdoptSourceSize(index, (int)frame.Width, (int)frame.Height);
                            break;
                        }
                        if (geometryChanged)
                            throw new IOException("Display format changed; reconnect required.");
                        if (frame.Pending)
                        {
                            Volatile.Write(ref darkness.Value, 0);
                            blackFrames = 0;
                        }
                        else
                        {
                        profiles.AdoptSourceSize(index, (int)frame.Width, (int)frame.Height);
                        var dark = PortalCaptureHealth.NearBlack(frame.Pixels);
                        Volatile.Write(ref darkness.Value, dark ? 1 : 0);
                        if (dark)
                        {
                            if (blackFrames == 0) blackStart = System.Diagnostics.Stopwatch.GetTimestamp();
                            blackFrames++;
                            if (PortalCaptureHealth.BlackStreak(blackFrames, System.Diagnostics.Stopwatch.GetElapsedTime(blackStart), SessionLock.IsLocked))
                            {
                                blackFrames = 0;
                                report($"portal-capture-stale;reason=low-luma;screen={index + 1}");
                                if (recyclePortal?.Invoke("low-luma") == true) break;
                            }
                        }
                        else blackFrames = 0;
                        }
                        await encoder.WriteAsync(frame.Pixels, (int)frame.Stride, stop.Token);
                    }
                    finally { Array.Clear(frame.Pixels); }
                    if (profiles.Get(index) != profile || (profiles.SingleVideoStream && profiles.SelectedScreen != index)
                        || media.ConsumeKeyFrameRequest(videoTrack ?? index)) break;
                }
                }
                catch (Exception e) when (RefreshInterrupt(e, seenGeneration))
                {
                    report($"portal-capture-refresh-retry;screen={index + 1}");
                    await DesktopBackend.WaitCaptureReadyAsync(stop.Token);
                }
            }
            finally { frames.Complete(); encoder.CompleteInput(); }
        });
        var consumer = Task.Run(async () =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var count = 0; long sentBytes = 0;
            var windowBytes = 0L; var windowFrames = 0; var lowWindows = 0;
            var windowStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var fpsWindowStart = windowStart; var fpsWindowFrames = 0;
            double maxEncoderDelay = 0, maxSendDelay = 0;
            try
            {
            await foreach (var encoded in encoder.ReadAsync(stop.Token))
            {
                try
                {
                    // Nominal duration describes the selected rate; the transport clocks
                    // RTP against actual elapsed time so slow frames cannot build playback lag.
                    var rtpDuration = (uint)(90000 / profile.Fps);
                    maxEncoderDelay = Math.Max(maxEncoderDelay, encoder.FrameDelayMilliseconds);
                    var sendStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    await media.SendH264PacedAsync(encoded, rtpDuration, videoTrack ?? index, profile.Bitrate, stop.Token);
                    maxSendDelay = Math.Max(maxSendDelay, System.Diagnostics.Stopwatch.GetElapsedTime(sendStarted).TotalMilliseconds);
                    sentBytes += encoded.Length;
                    windowBytes += encoded.Length;
                    count++;
                    windowFrames++;
                    fpsWindowFrames++;
                    var fpsElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(fpsWindowStart);
                    if (fpsElapsed >= TimeSpan.FromSeconds(2) && fpsWindowFrames > 0)
                    {
                        var current = profiles.Get(index);
                        var actual = fpsWindowFrames / fpsElapsed.TotalSeconds;
                        report($"video-fps;screen={index + 1};display-hz={profiles.DisplayFps(index)};requested={profiles.RequestedFps(index)};target={current.Fps};actual={actual.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
                        report($"video-pipeline-delay;screen={index + 1};chroma={profile.ChromaFormat};encode-queue-max-ms={maxEncoderDelay.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)};send-max-ms={maxSendDelay.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}");
                        maxEncoderDelay = 0; maxSendDelay = 0;
                        fpsWindowStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        fpsWindowFrames = 0;
                    }
                    if (count == 1 || count % 150 == 0)
                        report($"video-frames-sent={count};screen={index + 1};fps={count / watch.Elapsed.TotalSeconds:F1};encoded-mbps={sentBytes * 8 / watch.Elapsed.TotalSeconds / 1000000:F2}");
                    var windowElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(windowStart);
                    if (PortalCaptureHealth.WindowIsStale(windowFrames, windowElapsed, windowBytes, SessionLock.IsLocked)) lowWindows++;
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
                if (SessionLock.IsLocked != true && Volatile.Read(ref darkness.Value) != 0)
                    recyclePortal?.Invoke("low-bitrate");
            }
        });
        try
        {
            var completed = await Task.WhenAny(producer, consumer);
            if (completed.IsFaulted || completed.IsCanceled || completed == consumer) stop.Cancel();
            try { await Task.WhenAll(producer, consumer, capturePump); }
            catch
            {
                // Preserve an encoder/transport failure instead of replacing it
                // with the sibling task's cancellation during cleanup.
                if (completed.IsFaulted) await completed;
                throw;
            }
        }
        finally { stop.Cancel(); await capturePump; Array.Clear(first.Pixels); }
        rebuilds = 0;
        gaveUp = false;
        }
        catch (Exception raw) when (!ct.IsCancellationRequested)
        {
            var failure = Root(raw);
            if (failure is OperationCanceledException) throw;
            if (failure is VideoEncoderException encoderFailure)
            {
                report($"video-encoder-failed;screen={index + 1};codec={encoderFailure.Codec}");
                if (encoderFailure.ChromaFormat == 3)
                {
                    // A failed 4:4:4 session must not disable working 4:2:0 NVENC
                    // or tear down the portal. Resume compatible SDR immediately.
                    profiles.DisableTrueColor();
                    media.SendControl(profiles.Sync(0));
                    report("video-true-color-fallback;chroma=1;reason=encoder-failed");
                }
                await Task.Delay(100, ct);
                continue;
            }
            if (DesktopBackend.CaptureRefreshActive || RefreshInterrupt(failure, seenGeneration))
            {
                report($"portal-capture-refresh-retry;screen={index + 1}");
                await DesktopBackend.WaitCaptureReadyAsync(ct);
                continue;
            }
            if (failure is not (TimeoutException or IOException or ObjectDisposedException)) throw;
            if (SessionLock.IsLocked == true)
            {
                // The locker is not a dead stream. Keep the placeholder and the existing pipewiresrc.
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }
            // A dead PipeWire stream is rebuilt for this screen only. The other screen and the peer stay up.
            // After repeated failures, wait and try again instead of cancelling the session.
            if (++rebuilds > 3)
            {
                if (!gaveUp)
                {
                    gaveUp = true;
                    report($"capture-stream-rebuilt;screen={index + 1};reason=gave-up");
                }
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                rebuilds = 0;
            }
            var reason = failure is TimeoutException ? "no-frame" : "pipeline-error";
            try
            {
                if (rebuilds >= 3 && DesktopBackend.HasPortalRestoreToken())
                {
                    await DesktopBackend.RefreshPortalAsync(DesktopBackend.PortalInputEnabled, ct);
                    reason = "portal-restore";
                }
                else await DesktopBackend.RebuildCaptureAsync(output, ct);
            }
            catch (Exception refresh) when (refresh is IOException or InvalidOperationException or TimeoutException
                or DesktopSessionClosedException or UnauthorizedAccessException or FormatException or NotSupportedException or ObjectDisposedException)
            {
                if (!gaveUp)
                {
                    gaveUp = true;
                    report($"capture-stream-rebuilt;screen={index + 1};reason=gave-up");
                }
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                rebuilds = 0;
                continue;
            }
            report($"capture-stream-rebuilt;screen={index + 1};reason={reason}");
        }
        }
    }

    private static Task<CapturedScreen> ProbeOutputAsync(uint output, CancellationToken ct)
        => DesktopBackend.CaptureOrPendingAsync(output, ct);

    private static Exception Root(Exception error)
    {
        while (error is AggregateException aggregate && aggregate.InnerException is not null) error = aggregate.InnerException;
        return error;
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
            if (milliHz <= 0) milliHz = rates.GetValueOrDefault(DesktopBackend.ResolveOutput(output));
            if (milliHz <= 0) milliHz = DesktopBackend.StoredRefreshMilliHz(output);
            var limit = HostDisplayInfo.FrameRateLimit(milliHz);
            var mode = DesktopBackend.BoundMode(output);
            report($"display-frame-rate;screen={index + 1};refresh-millihz={milliHz};max-fps={limit}");
            if (mode.Width >= 2 && mode.Height >= 2)
                report($"display-mode;screen={index + 1};mode={mode.Width}x{mode.Height};refresh-millihz={milliHz}");
            return limit;
        }).ToArray();
    }

    // Replace a poisoned or logical-size placeholder with the matched hardware mode before
    // the display list is sent. The controller echoes that list back as the encode size.
    private static void ApplyBoundModes(IReadOnlyList<uint> outputs, (int Width, int Height)[] dimensions)
    {
        for (var index = 0; index < outputs.Count && index < dimensions.Length; index++)
        {
            var mode = DesktopBackend.BoundMode(outputs[index]);
            if (mode.Width >= 2 && mode.Height >= 2)
                dimensions[index] = (mode.Width & ~1, mode.Height & ~1);
        }
    }

    private static void BindCeilings(IReadOnlyList<uint> outputs, HostVideoSettings profiles)
    {
        for (var index = 0; index < outputs.Count; index++)
        {
            var mode = DesktopBackend.BoundMode(outputs[index]);
            if (mode.Width >= 2 && mode.Height >= 2)
                profiles.BindCeiling(index, mode.Width, mode.Height);
        }
    }

    private static JsonObject Capability(HostPeerId id, IReadOnlyList<(int Width, int Height)> dimensions, IReadOnlyList<int> frameRateLimits, int encoderImplementation, bool trueColor) => new()
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
                // Each chroma format is probed independently. 4:4:4 is SDR true
                // color; the 8-bit capture path does not promise HDR or HEVC.
                ["video_codec_capability"] = new JsonArray((trueColor ? new[] { 1, 3 } : new[] { 1 }).Select(chroma => (JsonNode)new JsonObject
                    { ["video_codec"] = 1, ["width"] = 3840, ["height"] = 2160, ["chroma_sampling"] = chroma,
                        ["bit_depth"] = 8, ["codec_impl"] = encoderImplementation }).ToArray())
            }
        }
    };
}
