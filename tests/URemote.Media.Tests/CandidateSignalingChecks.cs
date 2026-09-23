using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using SIPSorcery.Net;
using URemote.Media;

// Exercise the actual candidate events using a local STUN responder. SDP-only and
// SIPSorcery-to-SIPSorcery tests hide this bug: its parser accepts an unprefixed body.
internal static class CandidateSignalingChecks
{
    public static async Task RunAsync()
    {
        await CheckAsync(controller: true);
        await CheckAsync(controller: false);
        Console.WriteLine("PASS: controller and host trickle candidates have WebRTC prefix and bundle media id");
    }

    private static async Task CheckAsync(bool controller)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        var servers = new List<RTCIceServer> { new() { urls = $"stun:127.0.0.1:{endpoint.Port}" } };
        var candidate = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCandidate(JsonObject value)
        {
            if (value["candidate"]?.GetValue<string>().Contains(" typ srflx ") == true)
                candidate.TrySetResult(value);
        }
        using IDisposable peer = controller ? new ControllerMediaPeer(servers, dataOnly: true)
            : new HostMediaPeer(servers, activeVideoStreams: 0);
        if (peer is ControllerMediaPeer c) c.LocalCandidate += OnCandidate;
        else ((HostMediaPeer)peer).LocalCandidate += OnCandidate;

        // Don't answer until subscriptions are installed, even if gathering starts in the constructor.
        var packet = await server.ReceiveAsync(timeout.Token);
        var request = STUNMessage.ParseSTUNMessage(packet.Buffer, packet.Buffer.Length);
        if (request.Header.MessageType != STUNMessageTypesEnum.BindingRequest)
            throw new Exception("Expected a STUN binding request.");
        var response = new STUNMessage(STUNMessageTypesEnum.BindingSuccessResponse);
        response.Header.TransactionId = request.Header.TransactionId;
        response.AddXORMappedAddressAttribute(IPAddress.Parse("192.0.2.123"), 45678);
        await server.SendAsync(response.ToByteBuffer(null, true), packet.RemoteEndPoint, timeout.Token);
        var json = await candidate.Task.WaitAsync(timeout.Token);
        var text = json["candidate"]!.GetValue<string>();
        if (!text.StartsWith("candidate:", StringComparison.Ordinal) || text.StartsWith("candidate:candidate:", StringComparison.Ordinal))
            throw new Exception("Trickle candidate is not a WebRTC candidate attribute.");
        if (json["sdpMid"]?.GetValue<string>() != "0" || json["sdpMLineIndex"]?.GetValue<int>() != 0)
            throw new Exception("Trickle candidate must identify the bundled transport.");
        var parsed = new RTCIceCandidate(new RTCIceCandidateInit { candidate = text, sdpMid = "0", sdpMLineIndex = 0 });
        if (parsed.address != "192.0.2.123" || parsed.port != 45678 || parsed.type != RTCIceCandidateType.srflx)
            throw new Exception("Candidate endpoint changed during serialization.");
    }
}
