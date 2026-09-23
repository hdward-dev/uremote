using System.Text;
namespace URemote.Core;

public sealed record ClipboardRequest(ulong RequestId, int Format, string Text, string BlockKey, bool IsRead)
{ public override string ToString() => "ClipboardRequest(contents redacted)"; }

// MIT upstream clipboardSchema/clipboardV3/clipboardV4 wire layout, rewritten in C#.
public static class HostClipboardProtocol
{
    public const int MaxTextBytes = 65536;
    public static ClipboardRequest? Decode(byte[] data)
    {
        if (data.Length > 131072) throw new FormatException("Clipboard envelope too large.");
        var root = ProtoFields.Read(data);
        if (root.Bytes(21) is not { } request) return null;
        var rpc = ProtoFields.Read(request);
        if (rpc.Bytes(1) is not { } header) return null;
        var id = ProtoFields.Read(header).Items.FirstOrDefault(x => x.Tag == 1 && x.WireType == 0)?.Value ?? 0;
        if (rpc.Bytes(10) is { } change)
        {
            var body = ProtoFields.Read(change); var text = body.Text(2);
            if (Encoding.UTF8.GetByteCount(text) > MaxTextBytes || text.Contains('\0')) throw new FormatException("Clipboard text rejected.");
            return new(id, body.Int(1), text, "", false);
        }
        if (rpc.Bytes(9) is { } clip && ProtoFields.Read(clip).Bytes(2) is { } ask)
        {
            var body = ProtoFields.Read(ask); var key = body.Text(2); var format = body.Int(1);
            if (key.Length is 0 or > 256) throw new FormatException("Invalid clipboard block key.");
            if (format == 0 && body.Text(3) == "public.utf8-plain-text") format = 1;
            return new(id, format, "", key, true);
        }
        return null;
    }
    public static byte[] Change(ulong id, string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxTextBytes) throw new ArgumentException("Clipboard text exceeds limit.");
        return Envelope(id, false, 10, Join(Int(1, 1), Blob(2, Encoding.UTF8.GetBytes(text))));
    }
    public static byte[] Acknowledge(ulong id, bool success) => Envelope(id, true, 6, Int(1, success ? 1u : 2u));
    public static IEnumerable<(string Channel, byte[] Data)> ReadResponse(ClipboardRequest request, string? text)
    {
        var success = text is not null && request.Format is 1 or 13;
        // V4 blocks carry the requested native format. The V3 protobuf string above
        // is UTF-8, but Windows CF_UNICODETEXT is null-terminated UTF-16LE.
        var bytes = success ? request.Format == 13
            ? new UnicodeEncoding(false, false, true).GetBytes(text! + '\0') : Encoding.UTF8.GetBytes(text!) : [];
        if (bytes.Length > MaxTextBytes) { success = false; Array.Clear(bytes); bytes = []; }
        try
        {
            var count = (bytes.Length + 32767) / 32768;
            yield return ("TEXT_DATA_CHANNEL", Envelope(request.RequestId, true, 5, Blob(2,
                Join(Int(1, success ? 1u : 2u), Blob(2, Encoding.UTF8.GetBytes(request.BlockKey)), Int(3, (ulong)count)))));
            for (int i = 0; i < count; i++)
                yield return ("FILE_DATA_CHANNEL", Envelope(request.RequestId + (ulong)i + 1, false, 9, Blob(3,
                    Join(Blob(1, Encoding.UTF8.GetBytes(request.BlockKey)), Int(2, (ulong)i + 1), Blob(3, bytes.AsSpan(i * 32768, Math.Min(32768, bytes.Length - i * 32768)).ToArray())))));
        }
        finally { Array.Clear(bytes); }
    }
    public static string DecodeTextData(int format, byte[] bytes)
    {
        if (bytes.Length > MaxTextBytes) throw new FormatException("Clipboard data exceeds limit.");
        var text = (format == 13 ? new UnicodeEncoding(false, false, true).GetString(bytes)
            : new UTF8Encoding(false, true).GetString(bytes)).TrimEnd('\0');
        if (text.Contains('\0') || Encoding.UTF8.GetByteCount(text) > MaxTextBytes)
            throw new FormatException("Clipboard text rejected.");
        return text;
    }
    private static byte[] Envelope(ulong id, bool response, uint tag, byte[] body) => Join(Int(1, id), Int(2, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        Blob(response ? 22u : 21u, Join(Blob(1, Int(1, id)), Blob(tag, body))));
    private static byte[] Join(params byte[][] parts) => parts.SelectMany(x => x).ToArray();
    private static byte[] Int(uint tag, ulong value) => Join(Var(tag << 3), Var(value));
    private static byte[] Blob(uint tag, byte[] value) => Join(Var(tag << 3 | 2), Var((ulong)value.Length), value);
    private static byte[] Var(ulong value) { var b = new List<byte>(); do { var x = (byte)(value & 127); value >>= 7; b.Add(value == 0 ? x : (byte)(x | 128)); } while (value != 0); return b.ToArray(); }
}

public sealed record ClipboardTransfer(string Kind, ulong Id, string Key, int BlockId, int Count, int Result, byte[] Data, int Format = 0)
{ public override string ToString() => "ClipboardTransfer(contents redacted)"; }
public static class HostClipboardTransfers
{
    public static ClipboardTransfer? Decode(byte[] bytes)
    {
        var root = ProtoFields.Read(bytes);
        var response = root.Bytes(22) is not null;
        var rpcBytes = root.Bytes(response ? 22 : 21); if (rpcBytes is null) return null;
        var rpc = ProtoFields.Read(rpcBytes.Value); if (rpc.Bytes(1) is not { } head) return null;
        var id = ProtoFields.Read(head).Items.FirstOrDefault(x => x.Tag == 1 && x.WireType == 0)?.Value ?? 0;
        if (response && rpc.Bytes(6) is { } changed)
            return new("text-ack", id, "", 0, 0, ProtoFields.Read(changed).Int(1), []);
        if (rpc.Bytes(response ? 5 : 9) is not { } clipBytes) return null;
        var clip = ProtoFields.Read(clipBytes);
        if (response && clip.Bytes(1) is not null) return new("formats-ack", id, "", 0, 0, 1, []);
        if (!response && clip.Bytes(1) is { } list)
        {
            var formats = ProtoFields.Read(list, [1]).Repeated(1).Select(x => ProtoFields.Read(x)).ToArray();
            var format = formats.Any(f => f.Int(1) == 13) ? 13
                : formats.Any(f => f.Text(2) == "public.utf8-plain-text") ? 0
                : formats.Any(f => f.Int(1) == 1) ? 1 : -1;
            return new("formats", id, "", 0, 0, 0, [], format);
        }
        if (response && clip.Bytes(2) is { } confirm)
        { var p = ProtoFields.Read(confirm); return new("confirm", id, p.Text(2), 0, p.Int(3), p.Int(1), []); }
        if (!response && clip.Bytes(3) is { } block)
        { var p = ProtoFields.Read(block); return new("block", id, p.Text(1), p.Int(2), 0, 0, p.Bytes(3)?.ToArray() ?? []); }
        return null;
    }
    public static byte[] Advertise(ulong id) => Envelope(id, false, 1, Join(
        Blob(1, Int(1, 13)),
        Blob(1, Blob(2, Encoding.UTF8.GetBytes("public.utf8-plain-text")))));
    public static byte[] AcknowledgeFormats(ulong id) => Envelope(id, true, 1, []);
    public static byte[] Ask(ulong id, string key, int format) => Envelope(id, false, 2, Join(Int(1, (ulong)format), Blob(2, Encoding.UTF8.GetBytes(key)),
        format == 0 ? Blob(3, Encoding.UTF8.GetBytes("public.utf8-plain-text")) : []));
    public static byte[] AcknowledgeBlock(ClipboardTransfer block, bool success) => Envelope(block.Id, true, 3,
        Join(Blob(1, Encoding.UTF8.GetBytes(block.Key)), Int(2, (ulong)block.BlockId), Int(3, success ? 1u : 2u)));
    private static byte[] Envelope(ulong id, bool response, uint tag, byte[] body) => Join(Int(1, id), Int(2, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        Blob(response ? 22u : 21u, Join(Blob(1, Int(1, id)), Blob(response ? 5u : 9u, Blob(tag, body)))));
    private static byte[] Join(params byte[][] parts) => parts.SelectMany(x => x).ToArray();
    private static byte[] Int(uint tag, ulong value) => Join(Var(tag << 3), Var(value));
    private static byte[] Blob(uint tag, byte[] value) => Join(Var(tag << 3 | 2), Var((ulong)value.Length), value);
    private static byte[] Var(ulong value) { var b = new List<byte>(); do { var x = (byte)(value & 127); value >>= 7; b.Add(value == 0 ? x : (byte)(x | 128)); } while (value != 0); return b.ToArray(); }
}
