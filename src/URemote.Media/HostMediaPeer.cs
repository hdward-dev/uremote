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
                    { new(VideoCodecsEnum.H264, 98, 90000, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e033") },
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
        data.Writer.TryComplete();
        peer.Close("local session closed");
        peer.Dispose();
    }
}
