using System.Text.Json;
using URemote.Core;
using URemote.Media;

internal static class HostTerminalPeer
{
    public static async Task RunAsync(HostMediaPeer peer, HostTerminalManager manager, bool enabled, CancellationToken ct, Action<string> report)
    {
        var attached = new HashSet<uint>(); ulong sequence = 0;
        void Send(byte type, uint id, byte[] payload)
        {
            var wire = HostTerminalProtocol.EncodeFrame(type, id, payload);
            try { peer.SendData("BINARY_DATA_CHANNEL", wire); } finally { Array.Clear(wire); }
        }
        void Reply(byte type, uint id, int code, string message = "")
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { code, message });
            try { Send(type, id, payload); } finally { Array.Clear(payload); }
        }
        try
        {
            await foreach (var message in peer.DataMessages.ReadAllAsync(ct))
            {
                try
                {
                    if (HostTerminalProtocol.EnvironmentReply(message.Bytes, enabled) is { } environment)
                    { peer.SendData(message.ChannelLabel, environment); report("terminal-environment-replied"); continue; }
                    if (message.ChannelLabel == "CONTROL_DATA_CHANNEL")
                    {
                        try
                        {
                            if (HostControlEcho.Reply(message.Bytes, ++sequence, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), false) is { } echo)
                            { peer.SendControl(echo); continue; }
                        }
                        catch (FormatException) { }
                    }
                    if (message.ChannelLabel != "BINARY_DATA_CHANNEL" || HostTerminalProtocol.DecodeFrame(message.Bytes) is not { } frame) continue;
                    try
                    {
                        if (!enabled) { Reply(8, frame.SessionId, 403, "Terminal disabled locally"); continue; }
                        switch (frame.Type)
                        {
                            case 9:
                                var list = manager.List(); try { Send(10, frame.SessionId, list); } finally { Array.Clear(list); }
                                report("terminal-list-replied"); break;
                            case 1:
                                using (var json = JsonDocument.Parse(frame.Payload))
                                {
                                    var root = json.RootElement;
                                    var cols = root.TryGetProperty("cols", out var c) ? c.GetInt32() : 80;
                                    var rows = root.TryGetProperty("rows", out var r) ? r.GetInt32() : 24;
                                    var option = root.TryGetProperty("option", out var o) ? o.GetString() : "";
                                    report($"terminal-open-request;new-id={frame.SessionId == 0};option=" + (option is "create" or "new" or "attach" or "resume" or "" ? option : "other"));
                                    var session = frame.SessionId == 0 ? manager.Create(cols, rows) : manager.Find(frame.SessionId);
                                    if (session is null || session.Exited) { Reply(13, frame.SessionId, 404, "Session not found"); break; }
                                    session.Resize(cols, rows);
                                    Reply(13, session.Id, 0, "ok"); session.Attach(Send); attached.Add(session.Id);
                                    report("terminal-opened");
                                }
                                break;
                            case 2 when attached.Contains(frame.SessionId):
                                if (manager.Find(frame.SessionId) is { } writer) await writer.WriteAsync(frame.Payload, ct);
                                break;
                            case 3 when attached.Contains(frame.SessionId):
                                using (var json = JsonDocument.Parse(frame.Payload))
                                    manager.Find(frame.SessionId)?.Resize(json.RootElement.GetProperty("cols").GetInt32(), json.RootElement.GetProperty("rows").GetInt32());
                                break;
                            case 4: case 11:
                                await manager.CloseAsync(frame.SessionId); attached.Remove(frame.SessionId);
                                Reply(12, frame.SessionId, 0); report("terminal-closed"); break;
                            case 7 when attached.Contains(frame.SessionId):
                                using (var json = JsonDocument.Parse(frame.Payload))
                                {
                                    var value = json.RootElement.GetProperty("signal");
                                    var signal = value.ValueKind == JsonValueKind.Number ? value.GetInt32() : value.GetString() switch
                                    { "SIGINT" => 2, "SIGTERM" => 15, "SIGKILL" => 9, "SIGHUP" => 1, _ => throw new ArgumentException("Unsupported signal") };
                                    manager.Find(frame.SessionId)?.Signal(signal);
                                }
                                break;
                            case 16:
                                manager.Find(frame.SessionId)?.Detach(); attached.Remove(frame.SessionId); report("terminal-detached"); break;
                            default: Reply(8, frame.SessionId, 400, "Unsupported terminal operation"); break;
                        }
                    }
                    catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or IOException or KeyNotFoundException)
                    { Reply(frame.Type == 1 ? (byte)13 : (byte)8, frame.SessionId, 400, "Terminal request could not be applied"); report("terminal-request-rejected=" + e.GetType().Name); }
                    finally { Array.Clear(frame.Payload); }
                }
                catch (FormatException) { report("terminal-frame-rejected"); }
                finally { Array.Clear(message.Bytes); }
            }
        }
        finally { foreach (var id in attached) manager.Find(id)?.Detach(); }
    }
}
