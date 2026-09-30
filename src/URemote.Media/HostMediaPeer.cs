using System.Net;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace URemote.Media;

public sealed record PeerDataMessage(string ChannelLabel, bool IsText, byte[] Bytes)
{
    public override string ToString() => "PeerDataMessage(contents redacted)";
}

/// <summary>Native .NET WebRTC answer side. Caller must authorize the UU session before creating it.</summary>
public sealed class HostMediaPeer : IDisposable
{
    private readonly RTCPeerConnection peer;
    private readonly Channel<PeerDataMessage> data = Channel.CreateBounded<PeerDataMessage>(256);
    private readonly ConcurrentDictionary<string, RTCDataChannel> channels = new();
    private readonly int activeVideoStreams;
    private bool negotiated;
    private readonly ProtectedVideoCache recoveryCache = new();
    private readonly Channel<VideoFeedback> recoveryRequests = Channel.CreateBounded<VideoFeedback>(512);
    private readonly CancellationTokenSource recoveryStop = new();
    private readonly object recoveryGate = new();
    private bool recoveryInstalled;
    private readonly ConcurrentDictionary<(uint, ushort), byte> queuedRepairs = new();
    private Task? recoveryTask;
    private readonly int[] pendingKeyFrames = new int[5];
    private readonly long[] lastKeyFrame = new long[5];
    private readonly int[] streamBitrates = new int[5];
    private long recoveredPackets, repairMisses, repairExpired, repairThrottled, repairLimit, repairSendErrors, repairQueueFull, lastRepairAge;
    private long lastReceiverLog;
    private int nackCount, keyFrameRequests;
    private readonly bool[] pathReported = new bool[5];
    private readonly uint?[] pacedTimestamps = new uint?[5];
    private readonly TaskCompletionSource gatheringComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool enableAudio;
    private int disposed;
    public ChannelReader<PeerDataMessage> DataMessages => data.Reader;
    public event Action<JsonObject>? LocalCandidate;
    public event Action<RTCPeerConnectionState>? StateChanged;
    public event Action<string>? Diagnostic;
    public RTCPeerConnectionState State => peer.connectionState;
    public int VideoStreamCount => peer.VideoStreamList.Count;

    public HostMediaPeer(List<RTCIceServer>? iceServers = null, IPAddress? bindAddress = null, bool forceRelay = false, int activeVideoStreams = 1, bool enableAudio = false)
    {
        if (activeVideoStreams is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(activeVideoStreams));
        this.activeVideoStreams = activeVideoStreams; this.enableAudio = enableAudio;
        var direct = DirectMediaNetwork.FromEnvironment();
        peer = new RTCPeerConnection(new RTCConfiguration
        {
            iceServers = iceServers ?? [], X_BindAddress = direct?.Address ?? bindAddress,
            X_UseRtpFeedbackProfile = true,
            iceTransportPolicy = forceRelay ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all
        });
        try { direct?.Bind(peer.GetRtpChannel().RtpSocket); } catch { peer.Dispose(); throw; }
        // Use WebRTC JSON, not the unprefixed SDP attribute body (candidate.candidate).
        peer.onicecandidate += candidate => LocalCandidate?.Invoke(JsonNode.Parse(candidate.toJSON())!.AsObject());
        peer.onconnectionstatechange += state => StateChanged?.Invoke(state);
        peer.oniceconnectionstatechange += state => Diagnostic?.Invoke("ice-state=" + state);
        peer.OnReceiveReportByIndex += (index, _, kind, packet) =>
        {
            if (kind != SDPMediaTypesEnum.video) return;
            if (packet.ReceiverReport?.ReceptionReports is { } reports
                && Environment.TickCount64 - Interlocked.Read(ref lastReceiverLog) >= 5000)
            {
                Interlocked.Exchange(ref lastReceiverLog, Environment.TickCount64);
                var ntpSeconds = (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalSeconds + 2208988800.0;
                var compactNow = unchecked((uint)(ulong)(ntpSeconds * 65536.0));
                foreach (var report in reports.Take(5))
                {
                    var rtt = FindVideo(report.SSRC) >= 0
                        ? VideoFeedbackParser.RoundTripMilliseconds(compactNow, report.LastSenderReportTimestamp, report.DelaySinceLastSenderReport) : null;
                    Diagnostic?.Invoke($"rtcp-receiver;stream={index};loss256={report.FractionLost};lost={report.PacketsLost};jitter90k={report.Jitter};rtt-ms={rtt?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}");
                }
                Diagnostic?.Invoke($"video-repair-stats;sent={Interlocked.Read(ref recoveredPackets)};cache-miss={Interlocked.Read(ref repairMisses)};expired={Interlocked.Read(ref repairExpired)};throttled={Interlocked.Read(ref repairThrottled)};retry-limit={Interlocked.Read(ref repairLimit)};send-errors={Interlocked.Read(ref repairSendErrors)};queue-full={Interlocked.Read(ref repairQueueFull)};last-cache-age-ms={Interlocked.Read(ref lastRepairAge)}");
            }
            if (packet.Feedback is { } feedback)
            {
                if (feedback.Header.PacketType == RTCPReportTypesEnum.RTPFB
                    && feedback.Header.FeedbackMessageType == RTCPFeedbackTypesEnum.NACK)
                {
                    var count = Interlocked.Increment(ref nackCount);
                    if (count == 1 || count % 100 == 0) Diagnostic?.Invoke($"rtcp-nack;stream={index};requests={count}");
                }
                else if (feedback.Header.PacketType == RTCPReportTypesEnum.PSFB
                    && feedback.Header.PayloadFeedbackMessageType is PSFBFeedbackTypesEnum.PLI or PSFBFeedbackTypesEnum.FIR)
                {
                    var count = Interlocked.Increment(ref keyFrameRequests);
                    if (count == 1 || count % 20 == 0) Diagnostic?.Invoke($"rtcp-keyframe-request;stream={index};requests={count}");
                }
            }
        };
        peer.onicegatheringstatechange += state =>
        {
            Diagnostic?.Invoke("ice-gathering=" + state);
            if (state == RTCIceGatheringState.complete) gatheringComplete.TrySetResult();
        };
        peer.ondatachannel += channel =>
        {
            Diagnostic?.Invoke("data-channel-opened=" + (channel.label is "CONTROL_DATA_CHANNEL" or "BINARY_DATA_CHANNEL" or "TEXT_DATA_CHANNEL" ? channel.label : "other"));
            channels[channel.label] = channel;
            channel.onmessage += (_, protocol, bytes) =>
        {
            if (bytes.Length > 524288 || !data.Writer.TryWrite(new(channel.label,
                protocol == DataChannelPayloadProtocols.WebRTC_String, bytes.ToArray())))
                Dispose();
            };
        };
    }

    public bool SendControl(byte[] bytes)
    {
        if (disposed != 0 || bytes.Length > 8192 || !channels.TryGetValue("CONTROL_DATA_CHANNEL", out var channel)
            || channel.readyState != RTCDataChannelState.open) return false;
        channel.send(bytes);
        return true;
    }

    public bool SendData(string label, byte[] bytes)
    {
        if (disposed != 0 || bytes.Length > 131072 || !channels.TryGetValue(label, out var channel)
            || channel.readyState != RTCDataChannelState.open) return false;
        if (label == "TEXT_DATA_CHANNEL")
        {
            // UU carries protobuf bytes with PPID 51, including non-UTF8 payloads.
            // Send raw bytes: string conversion would corrupt compressed lists and file data.
            lock(channel) peer.sctp.RTCSctpAssociation.SendData(channel.id.GetValueOrDefault(),
                (uint)DataChannelPayloadProtocols.WebRTC_String, bytes);
        }
        else channel.send(bytes);
        return true;
    }
    public void SendOpus(byte[] packet)
    {
        if (!enableAudio || !negotiated || State != RTCPeerConnectionState.connected) throw new InvalidOperationException("Audio transport unavailable.");
        if (packet.Length is 0 or > 4000) throw new ArgumentException("Invalid Opus packet.");
        peer.SendAudio(960, packet);
    }

    public async Task<string> AnswerAsync(string offer)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (negotiated) throw new InvalidOperationException("Renegotiation is not implemented for this peer instance.");
        if (offer.Length > 1_000_000) throw new ArgumentException("SDP exceeds limit.");
        DescribeSdp("offer", offer);
        foreach (var line in offer.Split('\n').Select(x => x.Trim()).Where(x =>
            x.StartsWith("m=video ") || x.StartsWith("a=rtpmap:") || x.StartsWith("a=fmtp:")
            || x is "a=recvonly" or "a=sendrecv" or "a=inactive"))
            Diagnostic?.Invoke("sdp-format=" + line);
        var parsed = SDP.ParseSDPDescription(offer);
        var videos = parsed.Media.Count(m => m.Media == SDPMediaTypesEnum.video);
        var audios = parsed.Media.Count(m => m.Media == SDPMediaTypesEnum.audio);
        if (videos < activeVideoStreams || videos > 8 || audios > 2) throw new NotSupportedException("Unsupported remote media layout.");
        // Preserve UU's multi-video layout. Only the first display is active until display routing is implemented.
        foreach (var announcement in parsed.Media)
        {
            if (announcement.Media == SDPMediaTypesEnum.audio)
                peer.addTrack(new MediaStreamTrack(new List<AudioFormat>
                    { new(AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=10;useinbandfec=1") }, enableAudio ? MediaStreamStatusEnum.SendOnly : MediaStreamStatusEnum.Inactive));
            else if (announcement.Media == SDPMediaTypesEnum.video)
                peer.addTrack(new MediaStreamTrack(new List<VideoFormat>
                    { new(VideoCodecsEnum.H264, 98, 90000, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e034") },
                    peer.VideoStreamList.Count < activeVideoStreams ? MediaStreamStatusEnum.SendOnly : MediaStreamStatusEnum.Inactive));
        }
        var result = peer.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offer });
        if (result != SetDescriptionResultEnum.OK) throw new InvalidDataException("WebRTC rejected remote SDP: " + result);
        // Include gathered routes in the initial answer for official clients that drop early trickle candidates.
        await Task.WhenAny(gatheringComplete.Task, Task.Delay(1500));
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        var answer = peer.createAnswer();
        await peer.setLocalDescription(answer);
        negotiated = true;
        var text = peer.localDescription.sdp.ToString();
        DescribeSdp("answer", text);
        return text;
    }

    public void AddRemoteCandidate(JsonObject candidate)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        var text = candidate["candidate"]?.GetValue<string>() ?? throw new FormatException("Missing ICE candidate.");
        var index = candidate["sdpMLineIndex"]?.GetValue<int>() ?? 0;
        if (text.Length > 4096 || index is < 0 or > ushort.MaxValue) throw new FormatException("Invalid ICE candidate.");
        DescribeCandidate("remote", text, index);
        peer.addIceCandidate(new RTCIceCandidateInit
        {
            candidate = text, sdpMid = candidate["sdpMid"]?.GetValue<string>(), sdpMLineIndex = (ushort)index
        });
    }

    public void DescribeCandidate(string direction, string candidate, int index)
    {
        var parts = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var typ = Array.IndexOf(parts, "typ");
        var kind = typ >= 0 && typ + 1 < parts.Length ? parts[typ + 1] : "unknown";
        if (kind is not ("host" or "srflx" or "relay" or "prflx")) kind = "other";
        var protocol = parts.Length > 2 ? parts[2].ToLowerInvariant() : "unknown";
        if (protocol is not ("udp" or "tcp")) protocol = "other";
        Diagnostic?.Invoke($"ice-candidate={direction};kind={kind};transport={protocol};index={index}");
    }
    private void DescribeSdp(string direction, string text)
    {
        var lines = text.Split('\n').Select(x => x.Trim()).ToArray();
        var media = lines.Where(x => x.StartsWith("m=")).Select(x => x.Split(' ')).ToArray();
        Diagnostic?.Invoke($"sdp-{direction};audio={media.Count(x => x[0] == "m=audio")};video={media.Count(x => x[0] == "m=video")};data={media.Count(x => x[0] == "m=application")};rejected={media.Count(x => x.Length > 1 && x[1] == "0")};candidates={lines.Count(x => x.StartsWith("a=candidate:"))};bundle={lines.Any(x => x.StartsWith("a=group:BUNDLE"))};setup-active={lines.Count(x => x == "a=setup:active")};setup-passive={lines.Count(x => x == "a=setup:passive")}");
    }

    private void InstallRecovery()
    {
        lock (recoveryGate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (recoveryInstalled) return;
            var streams = peer.AudioStreamList.Cast<MediaStream>().Concat(peer.VideoStreamList).ToArray();
            if (streams.Any(s => s.GetSecurityContext() is null)) throw new InvalidOperationException("SRTP context unavailable.");
            foreach (var stream in streams)
            {
                var context = stream.GetSecurityContext();
                var video = stream is VideoStream;
                stream.SetSecurityContext(
                    (byte[] buffer, int length, out int output) =>
                    {
                        var result = context.ProtectRtpPacket(buffer, length, out output);
                        if (video && result == 0) recoveryCache.Store(buffer.AsSpan(0, output), Environment.TickCount64);
                        return result;
                    }, context.UnprotectRtpPacket, context.ProtectRtcpPacket,
                    (byte[] buffer, int length, out int output) =>
                    {
                        var result = context.UnprotectRtcpPacket(buffer, length, out output);
                        if (result == 0 && Volatile.Read(ref disposed) == 0)
                            foreach (var feedback in VideoFeedbackParser.Parse(buffer.AsSpan(0, output)))
                            {
                                if (feedback.KeyFrame) RequestKeyFrame(feedback.Ssrc);
                                else if (queuedRepairs.TryAdd((feedback.Ssrc, feedback.Sequence), 0)
                                    && !recoveryRequests.Writer.TryWrite(feedback))
                                {
                                    queuedRepairs.TryRemove((feedback.Ssrc, feedback.Sequence), out _);
                                    Interlocked.Increment(ref repairQueueFull);
                                }
                            }
                        return result;
                    });
            }
            recoveryInstalled = true;
            recoveryTask = RecoverAsync(recoveryStop.Token);
        }
    }
    private int FindVideo(uint ssrc) => peer.VideoStreamList.FindIndex(s => s.LocalTrack?.Ssrc == ssrc);
    private void RequestKeyFrame(uint ssrc)
    {
        var index = FindVideo(ssrc);
        if (index >= 0 && index < activeVideoStreams && Environment.TickCount64 - Interlocked.Read(ref lastKeyFrame[index]) >= 500)
            Interlocked.Exchange(ref pendingKeyFrames[index], 1);
    }
    public bool ConsumeKeyFrameRequest(int streamIndex)
    {
        if (streamIndex < 0 || streamIndex >= activeVideoStreams) return false;
        if (Volatile.Read(ref pendingKeyFrames[streamIndex]) == 0) return false;
        // IDRs are requested on demand to avoid desktop quality pumping. Coalesce
        // PLI/FIR requests for two seconds after each IDR
        // so repeated feedback cannot continuously restart the encoder.
        if (Environment.TickCount64 - Interlocked.Read(ref lastKeyFrame[streamIndex]) < 2000) return false;
        if (Interlocked.Exchange(ref pendingKeyFrames[streamIndex], 0) == 0) return false;
        Interlocked.Exchange(ref lastKeyFrame[streamIndex], Environment.TickCount64);
        Diagnostic?.Invoke("video-keyframe-refresh;stream=" + streamIndex);
        return true;
    }
    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var request in recoveryRequests.Reader.ReadAllAsync(ct))
            {
                queuedRepairs.TryRemove((request.Ssrc, request.Sequence), out _);
                var index = FindVideo(request.Ssrc);
                if (index < 0 || index >= activeVideoStreams || State != RTCPeerConnectionState.connected) continue;
                var packet = recoveryCache.Take(request.Ssrc, request.Sequence, Environment.TickCount64, out var result, out var age);
                if (age >= 0) Interlocked.Exchange(ref lastRepairAge, age);
                if (packet is null)
                {
                    switch (result)
                    {
                        case VideoRepairResult.Missing: Interlocked.Increment(ref repairMisses); break;
                        case VideoRepairResult.Expired: Interlocked.Increment(ref repairExpired); break;
                        case VideoRepairResult.Throttled: Interlocked.Increment(ref repairThrottled); break;
                        case VideoRepairResult.RetryLimit: Interlocked.Increment(ref repairLimit); break;
                    }
                    continue;
                }
                try
                {
                    var stream = peer.VideoStreamList[index];
                    var channel = peer.GetRtpChannel();
                    var error = stream.IsUsingRelayEndPoint
                        ? channel.SendRelay(RTPChannelSocketsEnum.RTP, stream.DestinationEndPoint, packet, stream.RtpRelayEndPoint.RelayServerEndPoint)
                        : channel.Send(RTPChannelSocketsEnum.RTP, stream.DestinationEndPoint, packet);
                    if (error != System.Net.Sockets.SocketError.Success)
                    { Interlocked.Increment(ref repairSendErrors); continue; }
                    var count = Interlocked.Increment(ref recoveredPackets);
                    if (count == 1 || count % 100 == 0) Diagnostic?.Invoke("video-packets-retransmitted=" + count);
                    // Bound recovery traffic to roughly 25% of the current video budget, shared across streams.
                    var budget = Math.Max(1000000, streamBitrates.Sum());
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, (packet.Length + 64) * 8.0 * 1000 / (budget * 0.25))), ct);
                }
                finally { Array.Clear(packet); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) when (e is ObjectDisposedException or System.Net.Sockets.SocketException or InvalidOperationException)
        { if (!ct.IsCancellationRequested) Diagnostic?.Invoke("video-recovery-stopped;type=" + e.GetType().Name); }
    }

    public async Task SendH264PacedAsync(byte[] accessUnit, uint durationRtpUnits, int streamIndex, int bitrate, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (State != RTCPeerConnectionState.connected || !negotiated) throw new InvalidOperationException("Video transport unavailable.");
        if (streamIndex < 0 || streamIndex >= activeVideoStreams || durationRtpUnits is 0 or > 90000
            || bitrate is < 1000000 or > 40000000) throw new ArgumentOutOfRangeException(nameof(streamIndex));
        InstallRecovery();
        Volatile.Write(ref streamBitrates[streamIndex], bitrate);
        if (H264Packetiser.ParseNals(accessUnit).Any(n => n.NAL.Length > 0 && (n.NAL[0] & 31) == 5))
        { Interlocked.Exchange(ref lastKeyFrame[streamIndex], Environment.TickCount64); Interlocked.Exchange(ref pendingKeyFrames[streamIndex], 0); }
        var stream = peer.VideoStreamList[streamIndex];
        if (!pathReported[streamIndex])
        {
            pathReported[streamIndex] = true;
            var destination = stream.IsUsingRelayEndPoint ? stream.RtpRelayEndPoint.RelayServerEndPoint : stream.DestinationEndPoint;
            Diagnostic?.Invoke($"media-path;stream={streamIndex};relay={stream.IsUsingRelayEndPoint};destination={destination}");
        }
        var format = stream.GetSendingFormat().ToVideoFormat();
        if (format.Codec != VideoCodecsEnum.H264) throw new InvalidOperationException("Expected negotiated H264.");
        var timestamp = pacedTimestamps[streamIndex] ?? stream.LocalTrack.Timestamp;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long sentBytes = 0;
        foreach (var packet in H264RtpPayloads.Create(accessUnit))
        {
            try
            {
                // Reserve 30% above the encoder limit for packet overhead; do not burst an entire IDR.
                var due = TimeSpan.FromSeconds(sentBytes * 8.0 / (bitrate * 1.3));
                var wait = due - watch.Elapsed;
                if (wait.TotalMilliseconds >= 1) await Task.Delay(wait, ct);
                ct.ThrowIfCancellationRequested();
                stream.SetRtpHeaderExtensionValue(TransportWideCCExtension.RTP_HEADER_EXTENSION_URI, null!);
                stream.SendRtpRaw(packet.Bytes, timestamp, packet.Last ? 1 : 0, format.FormatID);
                sentBytes += packet.Bytes.Length + 64;
            }
            finally { Array.Clear(packet.Bytes); }
        }
        pacedTimestamps[streamIndex] = unchecked(timestamp + durationRtpUnits);
    }

    public void SendH264(byte[] accessUnit, uint durationRtpUnits, int streamIndex = 0)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (peer.connectionState != RTCPeerConnectionState.connected || !negotiated)
            throw new InvalidOperationException("Media transport is not connected.");
        if (durationRtpUnits is 0 or > 90000 || accessUnit.Length is 0 or > 8_000_000)
            throw new ArgumentException("Invalid H264 access unit.");
        if (streamIndex < 0 || streamIndex >= activeVideoStreams) throw new ArgumentOutOfRangeException(nameof(streamIndex));
        peer.VideoStreamList[streamIndex].SendVideo(durationRtpUnits, accessUnit);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        recoveryStop.Cancel(); recoveryRequests.Writer.TryComplete(); recoveryCache.Dispose();
        data.Writer.TryComplete();
        peer.Close("local session closed");
        peer.Dispose();
        if (recoveryTask is { } task) _ = task.ContinueWith(_ => recoveryStop.Dispose(), TaskScheduler.Default);
        else recoveryStop.Dispose();
    }
}
