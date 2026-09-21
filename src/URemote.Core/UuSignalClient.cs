using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace URemote.Core;

/// <summary>One connection. Reconnection needs a new instance and freshly authorized room configuration.</summary>
public sealed class UuSignalClient : IAsyncDisposable
{
    private readonly WebSocket socket;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendGate = new(1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<UuSignalFrame>> acknowledgements = new();
    private readonly Channel<UuSignalFrame> events = Channel.CreateBounded<UuSignalFrame>(128);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? pump;
    private long nextId;
    private int opened;
    private int connected;
    private TimeSpan heartbeatBudget = TimeSpan.FromSeconds(30);
    private DateTimeOffset lastHeartbeat;
    private readonly TimeSpan connectTimeout;
    public bool IsConnected => Volatile.Read(ref connected) == 1 && !lifetime.IsCancellationRequested;
    public ChannelReader<UuSignalFrame> Events => events.Reader;
    public Task Completion => pump ?? Task.CompletedTask;

    private UuSignalClient(WebSocket socket, TimeSpan connectTimeout)
    {
        this.socket = socket;
        this.connectTimeout = connectTimeout;
    }

    public static Uri BuildUri(Uri server)
    {
        if (!server.IsAbsoluteUri || server.Scheme != "wss" || server.UserInfo.Length != 0 || server.Fragment.Length != 0)
            throw new ArgumentException("Signaling requires a WSS URL without credentials or fragment.");
        var builder = new UriBuilder(server);
        if (builder.Path.Length <= 1) builder.Path = "/socket.io/";
        else if (!builder.Path.EndsWith('/')) builder.Path += "/";
        // Room endpoints are server addresses. Reject preexisting query fields rather than duplicate EIO/transport.
        if (builder.Query.Length != 0) throw new ArgumentException("Unexpected signaling endpoint query.");
        builder.Query = "EIO=4&transport=websocket";
        return builder.Uri;
    }

    public static async Task<UuSignalClient> ConnectAsync(HostRoomConfiguration room, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(room.Token)) throw new ArgumentException("Room authorization is required.");
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(room.WebSocketConnectTimeoutMs, 1000, 60000));
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan; // Engine.IO owns its heartbeat.
        ws.Options.SetRequestHeader("X-NRD-AUTH", room.Token);
        ws.Options.SetRequestHeader("X-NRD-CONTROLLING", "0");
        ws.Options.SetRequestHeader("streamer_version", "V4.5.3");
        ws.Options.SetRequestHeader("streamer_flag", "{\"sdp_flags\":{\"gzip_sdp\":true}}");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            await ws.ConnectAsync(BuildUri(room.SignalingServer), deadline.Token);
            return await StartConnectedAsync(ws, timeout, ct);
        }
        catch { ws.Dispose(); throw; }
    }

    // Also supports an already connected in-memory transport for deterministic protocol tests.
    public static async Task<UuSignalClient> StartConnectedAsync(WebSocket transport, TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (transport.State != WebSocketState.Open || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentException("An open transport and bounded handshake timeout are required.");
        var client = new UuSignalClient(transport, timeout);
        client.pump = client.ReceiveLoopAsync();
        try { await client.ready.Task.WaitAsync(timeout, ct); return client; }
        catch { await client.DisposeAsync(); throw; }
    }

    public Task<UuSignalFrame> GetRoomInfoAsync(CancellationToken ct = default) =>
        EmitWithAckAsync("room_info", null, TimeSpan.FromSeconds(10), ct);

    public async Task<UuSignalFrame> EmitWithAckAsync(string name, JsonNode? payload,
        TimeSpan timeout, CancellationToken ct = default, IReadOnlyList<byte[]>? attachments = null)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<UuSignalFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!acknowledgements.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate request id.");
        try
        {
            await SendEventAsync(name, payload, attachments, id: id, ct: ct);
            return await completion.Task.WaitAsync(timeout, ct);
        }
        finally { acknowledgements.TryRemove(id, out _); }
    }

    public Task SendEventAsync(string name, JsonNode? payload, IReadOnlyList<byte[]>? attachments = null,
        long? id = null, CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException("Signal namespace is not connected.");
        if (name.Length is < 1 or > 64 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new ArgumentException("Invalid event name.", nameof(name));
        var data = new JsonArray(JsonValue.Create(name));
        if (payload is not null) data.Add(payload.DeepClone());
        var count = attachments?.Count ?? 0;
        if (count > 10 || (attachments?.Sum(b => (long)b.Length) ?? 0) > UuSignalReader.MaximumBytes)
            throw new ArgumentException("Signal attachments exceed limits.");
        return SendPacketAsync(new(count > 0 ? 5 : 2, Id: id, Attachments: count, Data: data), attachments, ct);
    }

    private async Task SendPacketAsync(SocketIoPacket packet, IReadOnlyList<byte[]>? attachments, CancellationToken ct)
    {
        var text = Encoding.UTF8.GetBytes("4" + SocketIoCodec.Encode(packet));
        if (text.Length > UuSignalReader.MaximumBytes) throw new ArgumentException("Signal packet is too large.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(30));
        await sendGate.WaitAsync(linked.Token);
        try
        {
            await socket.SendAsync(text.AsMemory(), WebSocketMessageType.Text, true, linked.Token);
            foreach (var attachment in attachments ?? [])
            {
                var bytes = new byte[attachment.Length + 1];
                bytes[0] = 4;
                attachment.CopyTo(bytes, 1);
                await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Binary, true, linked.Token);
            }
        }
        catch { lifetime.Cancel(); socket.Abort(); throw; }
        finally { sendGate.Release(); }
    }

    private async Task SendControlAsync(string text, CancellationToken ct)
    {
        await sendGate.WaitAsync(ct);
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, true, ct); }
        finally { sendGate.Release(); }
    }

    private async Task ReceiveLoopAsync()
    {
        Exception? failure = null;
        var reader = new UuSignalReader();
        lastHeartbeat = DateTimeOffset.UtcNow;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var budget = (IsConnected ? heartbeatBudget : connectTimeout) - (DateTimeOffset.UtcNow - lastHeartbeat);
                if (budget <= TimeSpan.Zero) throw new TimeoutException("UU signaling heartbeat expired.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(budget);
                using var message = new MemoryStream();
                var chunk = new byte[8192];
                WebSocketReceiveResult received;
                WebSocketMessageType? type = null;
                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), deadline.Token);
                    if (received.MessageType == WebSocketMessageType.Close) throw new IOException("Signal server closed the connection.");
                    if (type is not null && type != received.MessageType) throw new FormatException("Mixed WebSocket fragment types.");
                    type = received.MessageType;
                    if (message.Length + received.Count > UuSignalReader.MaximumBytes + 1)
                        throw new FormatException("WebSocket message exceeds limit.");
                    message.Write(chunk, 0, received.Count);
                } while (!received.EndOfMessage);
                var bytes = message.ToArray();
                var frame = type == WebSocketMessageType.Binary ? reader.ReadBinary(bytes)
                    : reader.ReadText(new UTF8Encoding(false, true).GetString(bytes));
                if (frame is null) continue;
                switch (frame.EngineType)
                {
                    case 0:
                        if (Interlocked.Exchange(ref opened, 1) != 0) throw new FormatException("Repeated Engine.IO open.");
                        var interval = Duration(frame.OpenData, "pingInterval");
                        var timeout = Duration(frame.OpenData, "pingTimeout");
                        heartbeatBudget = TimeSpan.FromMilliseconds(interval + timeout);
                        lastHeartbeat = DateTimeOffset.UtcNow;
                        await SendControlAsync("40", deadline.Token);
                        break;
                    case 2:
                        if (opened == 0) throw new FormatException("Ping before Engine.IO open.");
                        lastHeartbeat = DateTimeOffset.UtcNow;
                        // Mirror ping payload, if present, as Engine.IO requires.
                        await SendControlAsync("3" + Encoding.UTF8.GetString(bytes)[1..], deadline.Token);
                        break;
                    case 1: throw new IOException("Engine.IO connection closed.");
                    case 4:
                        var packet = frame.Packet!;
                        if (packet.Namespace != "/") throw new FormatException("Unexpected Socket.IO namespace.");
                        if (packet.Type == 0)
                        {
                            if (opened == 0 || Interlocked.Exchange(ref connected, 1) != 0)
                                throw new FormatException("Invalid namespace connection sequence.");
                            ready.TrySetResult();
                        }
                        else if (packet.Type is 1 or 4) throw new IOException("UU namespace disconnected or rejected authorization.");
                        else if (!IsConnected) throw new FormatException("Event before namespace connection.");
                        else if (packet.Type is 3 or 6)
                        {
                            if (packet.Id is long id && acknowledgements.TryRemove(id, out var completion)) completion.TrySetResult(frame);
                        }
                        else if (!events.Writer.TryWrite(frame)) throw new IOException("Signal event consumer is too slow.");
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
        { failure = new TimeoutException("UU signaling handshake or heartbeat expired."); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e) { failure = e; }
        finally
        {
            Volatile.Write(ref connected, 0);
            lifetime.Cancel();
            socket.Abort();
            var error = failure ?? new IOException("Signal client closed.");
            ready.TrySetException(error);
            foreach (var item in acknowledgements.Values) item.TrySetException(error);
            acknowledgements.Clear();
            events.Writer.TryComplete(failure);
        }
    }

    private static int Duration(JsonNode? data, string key)
    {
        if (data?[key] is not JsonValue value || !value.TryGetValue<int>(out var duration) || duration is < 1 or > 300000)
            throw new FormatException("Invalid Engine.IO heartbeat duration.");
        return duration;
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        socket.Abort();
        if (pump is not null) await pump;
        socket.Dispose();
        // Keep synchronization objects valid for racing canceled callers; no native resources retained.
    }
}
