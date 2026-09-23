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
    private bool answerApplied;
    private BundledIceCandidates? bundledIce;
    private readonly ConcurrentDictionary<string,int> iceDiagnostics = new();
    private readonly List<JsonObject> pendingCandidates = [];
    private int disposed;
    private readonly bool enableClipboard;
    public event Action<JsonObject>? LocalCandidate;
    public event Action<string>? Status;
    public event Action<int, byte[]>? Video;
    public event Action? ControlReady;
    public bool CanControl => control?.readyState == RTCDataChannelState.open && disposed == 0;
    public ControllerMediaPeer(List<RTCIceServer>? servers = null, bool relay = false, bool dataOnly = false, bool enableClipboard = false)
    {
        this.enableClipboard = enableClipboard;
        var direct = DirectMediaNetwork.FromEnvironment();
        peer = new RTCPeerConnection(new RTCConfiguration { iceServers = servers ?? [], X_BindAddress = direct?.Address, X_UseRtpFeedbackProfile = true,
            iceTransportPolicy = relay ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all });
        try { direct?.Bind(peer.GetRtpChannel().RtpSocket); } catch { peer.Dispose(); throw; }
        // candidate is the SDP attribute body, not the WebRTC signaling representation.
        // toJSON supplies the required candidate: prefix and a non-null media identifier.
        peer.onicecandidate += c => { DescribeCandidate("local", c.candidate); LocalCandidate?.Invoke(JsonNode.Parse(c.toJSON())!.AsObject()); };
        var ice = (RtpIceChannel)peer.GetRtpChannel();
        void Count(string key) => iceDiagnostics.AddOrUpdate(key,1,(_, n) => n+1);
        ice.OnStunMessageSent += (message, _, relayed) => Count("sent-"+message.Header.MessageType+(relayed ? "-relay" : "-direct"));
        ice.OnStunMessageReceived += (message, _, relayed) => {
            Count("received-"+message.Header.MessageType+(relayed ? "-relay" : "-direct"));
            if (message.Header.MessageType == STUNMessageTypesEnum.BindingRequest)
                Count(message.CheckIntegrity(System.Text.Encoding.UTF8.GetBytes(ice.LocalIcePassword)) ? "incoming-integrity-ok" : "incoming-integrity-failed");
        };
        ice.OnIceCandidateError += (_, reason) => {
            var category = reason.Contains("sdpMLineIndex") ? "media-index" : reason.Contains("component") ? "component" : reason.Contains("transport") ? "transport" : "other";
            Count("candidate-rejected-"+category);
        };
        peer.oniceconnectionstatechange += state => {
            Status?.Invoke("ice-" + state);
            if (state is RTCIceConnectionState.failed or RTCIceConnectionState.connected)
            {
                Status?.Invoke("ice-role-controlling="+ice.IsController+";remote-credentials="+(!string.IsNullOrEmpty(ice.RemoteIceUser)&&!string.IsNullOrEmpty(ice.RemoteIcePassword)));
                foreach (var count in iceDiagnostics.OrderBy(x=>x.Key)) Status?.Invoke("ice-stat-"+count.Key+"="+count.Value);
            }
        };
        peer.onicegatheringstatechange += s => { Status?.Invoke("gathering-" + s); if (s == RTCIceGatheringState.complete) gathered.TrySetResult(); };
        peer.onconnectionstatechange += s => Status?.Invoke("peer-" + s);
        peer.OnVideoFrameReceivedByIndex += (index, endpoint, stamp, bytes, format) => { if (bytes.Length <= 16_000_000 && disposed == 0) Video?.Invoke(index, bytes); };
        if (!dataOnly)
        {
        for (int i = 0; i < 5; i++) peer.addTrack(new MediaStreamTrack(new List<VideoFormat> {
            new(VideoCodecsEnum.H264, 98, 90000, "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e033") }, MediaStreamStatusEnum.RecvOnly));
        peer.addTrack(new MediaStreamTrack(new List<AudioFormat> { new(AudioCodecsEnum.OPUS, 111, 48000, 2) }, MediaStreamStatusEnum.RecvOnly));
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
            try { if (HostControlEcho.Reply(bytes, Next(), (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), true, enableClipboard) is { } reply) Send(reply); }
            catch (FormatException) { }
        };
        channel.onopen += () => Status?.Invoke("data-ready-" + channel.label);
        if (channel.label == "CONTROL_DATA_CHANNEL") channel.onopen += () => { ControlReady?.Invoke(); _ = HeartbeatAsync(); };
    }
    private async Task HeartbeatAsync()
    {
        try { while (!lifetime.IsCancellationRequested) { Send(ControllerProtocol.Echo(Next(), enableClipboard)); await Task.Delay(100, lifetime.Token); } }
        catch (OperationCanceledException) { }
        catch { Status?.Invoke("control-failed"); }
    }
    public async Task<string> OfferAsync(CancellationToken ct = default)
    {
        if (DirectMediaNetwork.FromEnvironment() is { } direct) Status?.Invoke("direct-interface=" + direct.Interface);
        foreach (var label in new[] { "CONTROL_DATA_CHANNEL", "TEXT_DATA_CHANNEL", "STREAMER_DATA_CHANNEL", "FILE_DATA_CHANNEL", "BINARY_DATA_CHANNEL" })
            Attach(await peer.createDataChannel(label, new RTCDataChannelInit()));
        var offer = peer.createOffer(); await peer.setLocalDescription(offer);
        await Task.WhenAny(gathered.Task, Task.Delay(1500, ct)); ct.ThrowIfCancellationRequested();
        var sdp = peer.localDescription.sdp.ToString();
        DescribeSdp("offer", sdp);
        return sdp;
    }
    public void ApplyAnswer(string sdp)
    {
        if (sdp.Length > 1_000_000) throw new InvalidDataException("SDP exceeds limit.");
        DescribeSdp("answer", sdp);
        foreach (var line in sdp.Split('\n').Select(x => x.Trim()).Where(x => x.StartsWith("a=candidate:"))) DescribeCandidate("sdp-remote", line[2..]);
        var result = peer.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = sdp });
        if (result != SetDescriptionResultEnum.OK) throw new InvalidDataException("Remote media negotiation rejected.");
        bundledIce = new BundledIceCandidates(sdp);
        answerApplied = true;
        foreach (var candidate in pendingCandidates) ApplyCandidate(candidate);
        pendingCandidates.Clear();
    }
    private void DescribeSdp(string direction, string sdp)
    {
        var lines = sdp.Split('\n').Select(x => x.Trim()).ToArray();
        var media = lines.Where(x => x.StartsWith("m=")).Select(x => x.Split(' ')).ToArray();
        Status?.Invoke($"sdp-{direction};audio={media.Count(x => x[0] == "m=audio")};video={media.Count(x => x[0] == "m=video")};rejected={media.Count(x => x.Length > 1 && x[1] == "0")};inactive={lines.Count(x => x == "a=inactive")};bundle={lines.Any(x => x.StartsWith("a=group:BUNDLE"))};ice-user-groups={lines.Where(x => x.StartsWith("a=ice-ufrag:")).Distinct().Count()};ice-password-groups={lines.Where(x => x.StartsWith("a=ice-pwd:")).Distinct().Count()}");
    }
    public void AddCandidate(JsonObject c)
    {
        DescribeCandidate("remote", c["candidate"]?.GetValue<string>() ?? "");
        if (!answerApplied) { if (pendingCandidates.Count >= 256) throw new InvalidDataException("Too many early candidates."); pendingCandidates.Add((JsonObject)c.DeepClone()); return; }
        ApplyCandidate(c);
    }
    private void DescribeCandidate(string direction, string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.IndexOf(parts, "typ");
        var kind = index >= 0 && index + 1 < parts.Length ? parts[index + 1] : "unknown";
        if (kind is not ("host" or "srflx" or "relay" or "prflx")) kind = "unknown";
        var protocol = parts.Length > 2 && parts[2].Equals("udp", StringComparison.OrdinalIgnoreCase) ? "udp" : "other";
        Status?.Invoke($"candidate-{direction};type={kind};transport={protocol}");
    }
    private void ApplyCandidate(JsonObject c)
    {
        var originalIndex = c["sdpMLineIndex"]?.GetValue<int>() ?? 0;
        c = bundledIce?.Normalize(c) ?? c;
        if ((c["sdpMLineIndex"]?.GetValue<int>() ?? 0) != originalIndex) iceDiagnostics.AddOrUpdate("bundle-remapped",1,(_,n)=>n+1);
        peer.addIceCandidate(new RTCIceCandidateInit {
        candidate = c["candidate"]?.GetValue<string>() ?? "", sdpMid = c["sdpMid"]?.GetValue<string>(), sdpMLineIndex = (ushort)(c["sdpMLineIndex"]?.GetValue<int>() ?? 0) });
    }
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
