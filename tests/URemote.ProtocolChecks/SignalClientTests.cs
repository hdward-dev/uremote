using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using URemote.Core;

static class SignalClientTests
{
    public static async Task Run(Action<bool, string> check)
    {
        using var transport = new TestWebSocket();
        var starting = UuSignalClient.StartConnectedAsync(transport, TimeSpan.FromSeconds(2));
        transport.FeedText("0{\"pingInterval\":15000,", false);
        transport.FeedText("\"pingTimeout\":18000}");
        check((await transport.NextSent()).Text == "40", "fragmented Engine.IO open triggers namespace connect");
        transport.FeedText("40{\"sid\":\"fixture\"}");
        await using var client = await starting;
        check(client.IsConnected, "namespace confirmation completes handshake");
        var room = client.GetRoomInfoAsync();
        var roomPacket = SocketIoCodec.Parse((await transport.NextSent()).Text[1..]);
        check(roomPacket.Id is not null && roomPacket.Data?[0]?.GetValue<string>() == "room_info", "room_info requests an ACK");
        transport.FeedText($"43{roomPacket.Id}[{{\"publisher\":{{\"role\":\"publisher\"}}}}]");
        check((await room).Packet?.Data?[0]?["publisher"]?["role"]?.GetValue<string>() == "publisher", "ACK routed to matching request");
        transport.FeedText("2probe");
        check((await transport.NextSent()).Text == "3probe", "Engine.IO ping payload echoed in pong");

        var (payload, binary) = HostSignalSessions.BuildAnswer(new("fixture-client", "fixture-ice", "fixture-control"), "v=0\r\ns=fixture\r\n");
        var first = client.SendEventAsync("soac", payload, [binary]);
        var second = client.SendEventAsync("forward_setting", new JsonObject { ["fixture"] = true });
        await Task.WhenAll(first, second);
        var sentHeader = await transport.NextSent();
        var sentBinary = await transport.NextSent();
        var sentSecond = await transport.NextSent();
        check(sentHeader.Text.StartsWith("451-", StringComparison.Ordinal) && sentBinary.Type == WebSocketMessageType.Binary
            && sentBinary.Bytes[0] == 4 && sentSecond.Text.StartsWith("42", StringComparison.Ordinal), "binary packet and attachment are sent atomically");
        var peerReader = new UuSignalReader();
        peerReader.ReadText(sentHeader.Text);
        check(UuSignalReader.DecodeGzipSdp(peerReader.ReadBinary(sentBinary.Bytes)!) == "v=0\r\ns=fixture\r\n", "outbound answer is decodable by UU framing");
        transport.FeedText(sentHeader.Text);
        transport.Feed(sentBinary.Bytes, WebSocketMessageType.Binary);
        using var eventTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        check(UuSignalReader.DecodeGzipSdp(await client.Events.ReadAsync(eventTimeout.Token)) == "v=0\r\ns=fixture\r\n", "inbound binary event reaches consumer intact");

        var timedOut = client.EmitWithAckAsync("room_info", null, TimeSpan.FromMilliseconds(30));
        var latePacket = SocketIoCodec.Parse((await transport.NextSent()).Text[1..]);
        try { await timedOut; check(false, "ACK deadline enforced"); }
        catch (TimeoutException) { check(true, "ACK deadline enforced"); }
        transport.FeedText($"43{latePacket.Id}[\"late\"]");
        var pending = client.GetRoomInfoAsync();
        await transport.NextSent();
        transport.Feed([], WebSocketMessageType.Close);
        try { await pending; check(false, "disconnect rejects pending ACK"); }
        catch (IOException) { check(true, "disconnect rejects pending ACK"); }
        await client.Completion;
        check(!client.IsConnected, "disconnected transport no longer reports online");

        using var heartbeatTransport = new TestWebSocket();
        var heartbeatStarting = UuSignalClient.StartConnectedAsync(heartbeatTransport, TimeSpan.FromSeconds(2));
        heartbeatTransport.FeedText("0{\"pingInterval\":30,\"pingTimeout\":30}");
        await heartbeatTransport.NextSent();
        heartbeatTransport.FeedText("40{}");
        await using var heartbeatClient = await heartbeatStarting;
        await heartbeatClient.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        try { await heartbeatClient.Events.Completion; check(false, "missing heartbeat closes connection"); }
        catch (TimeoutException) { check(!heartbeatClient.IsConnected, "missing heartbeat closes connection"); }

        using var invalidTransport = new TestWebSocket();
        var invalidStarting = UuSignalClient.StartConnectedAsync(invalidTransport, TimeSpan.FromSeconds(2));
        invalidTransport.FeedText("40{}");
        try { await invalidStarting; check(false, "namespace connect before Engine.IO open rejected"); }
        catch (FormatException) { check(true, "namespace connect before Engine.IO open rejected"); }
        check(UuSignalClient.BuildUri(new("wss://signal.example.test")).ToString() == "wss://signal.example.test/socket.io/?EIO=4&transport=websocket",
            "signal WebSocket URL uses observed Engine.IO endpoint");
    }
}

sealed record TestMessage(byte[] Bytes, WebSocketMessageType Type, bool End = true)
{
    public string Text => Encoding.UTF8.GetString(Bytes);
}
sealed class TestWebSocket : WebSocket
{
    private readonly Channel<TestMessage> incoming = Channel.CreateUnbounded<TestMessage>();
    private readonly Channel<TestMessage> outgoing = Channel.CreateUnbounded<TestMessage>();
    private WebSocketState state = WebSocketState.Open;
    private TestMessage? partial;
    private int offset;
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => state;
    public override string? SubProtocol => null;
    public void FeedText(string text, bool end = true) => Feed(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, end);
    public void Feed(byte[] bytes, WebSocketMessageType type, bool end = true) => incoming.Writer.TryWrite(new(bytes, type, end));
    public async Task<TestMessage> NextSent() => await outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    public override void Abort() { state = WebSocketState.Aborted; incoming.Writer.TryComplete(); }
    public override void Dispose() => Abort();
    public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) { Abort(); return Task.CompletedTask; }
    public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) => CloseAsync(status, description, ct);
    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
    {
        partial ??= await incoming.Reader.ReadAsync(ct);
        var count = Math.Min(buffer.Count, partial.Bytes.Length - offset);
        partial.Bytes.AsSpan(offset, count).CopyTo(buffer.AsSpan());
        offset += count;
        var type = partial.Type;
        var exhausted = offset == partial.Bytes.Length;
        var end = exhausted && partial.End;
        if (exhausted) { partial = null; offset = 0; }
        return new(count, type, end);
    }
    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.Yield();
        if (state != WebSocketState.Open) throw new WebSocketException("Test transport closed.");
        await outgoing.Writer.WriteAsync(new(buffer.ToArray(), type, end), ct);
    }
}
