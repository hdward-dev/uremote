using System.Text;
using System.Text.Json;

namespace URemote.Core;

public static class HostControlEcho
{
    // Protocol port from upstream controlChannelEncode/Decode; never echo arbitrary JSON or input.
    public static byte[]? Reply(ReadOnlyMemory<byte> packet, ulong sequence, ulong timestamp, bool enableInput, bool enableClipboard = false, bool enableFiles = false)
    {
        if (packet.Length > 8192 || (packet.Length > 0 && packet.Span[0] == (byte)'{')) return null;
        var envelope = ProtoFields.Read(packet);
        if (envelope.Bytes(3) is not { } simple) return null;
        var action = ProtoFields.Read(simple);
        if (action.Int(1) != 0) return null;
        var args = action.Text(2);
        if (args.Length > 256) throw new FormatException("Echo arguments exceed limit.");
        // Some native clients omit args; their request sequence lives in the outer envelope.
        var requestSequence = envelope.Items.FirstOrDefault(f => f.Tag == 1 && f.WireType == 0)?.Value ?? 0;
        if (args.Length != 0)
        {
            try
            {
                using var json = JsonDocument.Parse(args);
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.EnumerateObject().Count() == 1
                    && json.RootElement.TryGetProperty("seq", out var seq) && seq.TryGetUInt64(out var parsed))
                    requestSequence = parsed;
                else throw new FormatException("Invalid echo sequence.");
            }
            catch (JsonException) { throw new FormatException("Non-JSON echo arguments require protocol investigation."); }
        }
        var body = new List<byte>();
        Var(body, 8); Var(body, 1);
        Bytes(body, 2, Encoding.UTF8.GetBytes("{\"seq\":" + requestSequence + "}"));
        var flags = new List<byte>();
        if (enableInput) flags.AddRange([0x18, 2]);
        if (enableClipboard) flags.AddRange([0x08, 2, 0x10, 2]);
        // Official 4.41 host advertises FTP list compression as FeatureFlag field 6 = 2.
        // Without this capability iOS opens the channel but never issues an FTP request.
        if (enableFiles) flags.AddRange([0x30, 2]);
        if (flags.Count > 0) Bytes(body, 4, flags.ToArray());
        var result = new List<byte>();
        Var(result, 8); Var(result, sequence); Var(result, 16); Var(result, timestamp); Bytes(result, 3, body.ToArray());
        return result.ToArray();
    }
    private static void Bytes(List<byte> to, uint tag, byte[] value)
    { Var(to, (tag << 3) | 2); Var(to, (ulong)value.Length); to.AddRange(value); }
    private static void Var(List<byte> to, ulong value)
    {
        do { var b = (byte)(value & 127); value >>= 7; to.Add(value > 0 ? (byte)(b | 128) : b); } while (value > 0);
    }
}
