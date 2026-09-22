using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using URemote.Core;
namespace URemote.Media;

public sealed class ControllerMediaPeer : IDisposable
{
    private readonly RTCPeerConnection peer;
    private RTCDataChannel? control;
    private readonly ConcurrentDictionary<string, RTCDataChannel> channels = new();
    public event Action<string, byte[]>? DataReceived;
    private readonly object sendGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource gathered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long sequence;
    private int disposed;
    public event Action<JsonObject>? LocalCandidate;
    public event Action<string>? Status;
    public event Action<int, byte[]>? Video;
    public event Action? ControlReady;
    public bool CanControl => control?.readyState == RTCDataChannelState.open && disposed == 0;
    public ControllerMediaPeer(List<RTCIceServer>? servers = null, bool relay = false, bool dataOnly = false)
    {
        peer = new RTCPeerConnection(new RTCConfiguration { iceServers = servers ?? [], X_UseRtpFeedbackProfile = true,
            iceTransportPolicy = relay ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all });
        peer.onicecandidate += c => LocalCandidate?.Invoke(new JsonObject { ["candidate"] = c.candidate, ["sdpMid"] = c.sdpMid, ["sdpMLineIndex"] = (int)c.sdpMLineIndex });
        peer.onicegatheringstatechange += s => { if (s == RTCIceGatheringState.complete) gathered.TrySetResult(); };
        peer.onconnectionstatechange += s => Status?.Invoke("peer-" + s);
        peer.OnVideoFrameReceivedByIndex += (index, endpoint, stamp, bytes, format) => { if (bytes.Length <= 16_000_000 && disposed == 0) Video?.Invoke(index, bytes); };
        if (!dataOnly)
        {
        for (int i = 0; i < 5; i++) peer.addTrack(new MediaStreamTrack(new List<VideoFormat> {
            new(VideoCodecsEnum.H264, 98, 90000, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e033") }, MediaStreamStatusEnum.RecvOnly));
        peer.addTrack(new MediaStreamTrack(new List<AudioFormat> { new(AudioCodecsEnum.OPUS, 111, 48000, 2) }, MediaStreamStatusEnum.Inactive));
        }
        peer.ondatachannel += Attach;
    }
    private ulong Next() => (ulong)Interlocked.Increment(ref sequence);
    private void Attach(RTCDataChannel channel)
    {
        channels[channel.label] = channel;
        if (channel.label == "CONTROL_DATA_CHANNEL" && control is null) control = channel;
        channel.onmessage += (_, protocol, bytes) =>
        {
            if (disposed != 0 || bytes.Length > 524288) return;
            try { if (URemote.Core.FileTransferProtocol.Decode(bytes) is { }) Status?.Invoke("ftp-payload-protocol="+protocol); } catch(FormatException) { }
            DataReceived?.Invoke(channel.label, bytes);
            if (bytes.Length > 8192) return;
            try { if (HostControlEcho.Reply(bytes, Next(), (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true) is { } reply) Send(reply); }
            catch (FormatException) { }
        };
        channel.onopen += () => Status?.Invoke("data-ready-" + channel.label);
        if (channel.label == "CONTROL_DATA_CHANNEL") channel.onopen += () => { ControlReady?.Invoke(); _ = HeartbeatAsync(); };
    }
    private async Task HeartbeatAsync()
    {
        try { while (!lifetime.IsCancellationRequested) { Send(ControllerProtocol.Echo(Next())); await Task.Delay(100, lifetime.Token); } }
        catch (OperationCanceledException) { }
        catch { Status?.Invoke("control-failed"); }
    }
    public async Task<string> OfferAsync(CancellationToken ct = default)
    {
        foreach (var label in new[] { "CONTROL_DATA_CHANNEL", "TEXT_DATA_CHANNEL", "STREAMER_DATA_CHANNEL", "FILE_DATA_CHANNEL", "BINARY_DATA_CHANNEL" })
            Attach(await peer.createDataChannel(label, new RTCDataChannelInit()));
        var offer = peer.createOffer(); await peer.setLocalDescription(offer);
        await Task.WhenAny(gathered.Task, Task.Delay(1500, ct)); ct.ThrowIfCancellationRequested();
        return peer.localDescription.sdp.ToString();
    }
    public void ApplyAnswer(string sdp)
    {
        if (sdp.Length > 1_000_000) throw new InvalidDataException("SDP exceeds limit.");
        var result = peer.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
        if (result != SetDescriptionResultEnum.OK) throw new InvalidDataException("Remote media negotiation rejected.");
    }
    public void AddCandidate(JsonObject c) => peer.addIceCandidate(new RTCIceCandidateInit {
        candidate = c["candidate"]?.GetValue<string>() ?? "", sdpMid = c["sdpMid"]?.GetValue<string>(), sdpMLineIndex = (ushort)(c["sdpMLineIndex"]?.GetValue<int>() ?? 0) });
    private bool Send(byte[] bytes)
    {
        lock (sendGate) { if (!CanControl) return false; try { control!.send(bytes); return true; } catch { return false; } }
    }
    public bool SendData(string label, byte[] bytes)
    {
        lock (sendGate) {
            if (disposed != 0 || bytes.Length > 131072 || !channels.TryGetValue(label, out var channel) || channel.readyState != RTCDataChannelState.open) return false;
            try {
                if (label == "TEXT_DATA_CHANNEL") peer.sctp.RTCSctpAssociation.SendData(channel.id.GetValueOrDefault(), (uint)DataChannelPayloadProtocols.WebRTC_String, bytes);
                else channel.send(bytes);
                return true;
            } catch { return false; }
        }
    }
    public bool SendInput(string json, int display)
    {
        // Official desktop hosts consume raw JSON with the WebRTC string PPID.
        // The SendToRom protobuf envelope is used by mobile/legacy hosts.
        lock (sendGate)
        {
            if (!CanControl || System.Text.Encoding.UTF8.GetByteCount(json) > 4096) return false;
            try { control!.send(json); return true; } catch { return false; }
        }
    }
    public void SelectDisplay(int display) => Send(ControllerProtocol.Capture(Next(), display));
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); lock (sendGate) { peer.Close("controller closed"); peer.Dispose(); }
    }
}
