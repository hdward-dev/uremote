namespace URemote.Core;
public sealed record CaptureUpdate(int Screen, int Width, int Height, int Fps, int Quality, ulong Sequence, int Flag, ulong? RpcId);
public static class HostCaptureProtocol
{
    // Restrict diagnostics to numeric capture settings; never log arbitrary channel payloads.
    public static string DescribeSettings(byte[] bytes)
    {
        if (bytes.Length > 8192) return "oversized";
        var root = ProtoFields.Read(bytes);
        if (root.Bytes(9) is { } config) return Describe("config", config);
        if (root.Bytes(21) is not { } request) return "none";
        var rpc = ProtoFields.Read(request);
        foreach (var tag in new[] { 2, 3, 4, 6 })
            if (rpc.Bytes(tag) is { } settings) return Describe("rpc" + tag, settings);
        return "none";
    }
    private static string Describe(string kind, ReadOnlyMemory<byte> settings) => kind + ":" + string.Join(",",
        ProtoFields.Read(settings).Items.Where(x => x.WireType == 0).Take(32).Select(x => $"{x.Tag}={x.Value}"));

    public static CaptureUpdate? Decode(byte[] bytes)
    {
        if (bytes.Length > 8192 || (bytes.Length > 0 && bytes[0] == '{')) return null;
        var root = ProtoFields.Read(bytes);
        var seq = root.Items.FirstOrDefault(x => x.Tag == 1 && x.WireType == 0)?.Value ?? 0;
        if (root.Bytes(9) is { } config)
        {
            var p = ProtoFields.Read(config);
            return new(p.Int(5), p.Int(6), p.Int(7), Fps(p.Int(2)), p.Int(3), seq, p.Int(8), null);
        }
        if (root.Bytes(21) is not { } rpcBytes) return null;
        var rpc = ProtoFields.Read(rpcBytes);
        if (rpc.Bytes(1) is not { } header) return null;
        var id = ProtoFields.Read(header).Items.FirstOrDefault(x => x.Tag == 1 && x.WireType == 0)?.Value ?? 0;
        if (rpc.Bytes(2) is { } capture)
        {
            var p = ProtoFields.Read(capture);
            return new(p.Int(4), p.Int(5), p.Int(6), p.Int(18) > 0 ? p.Int(18) : Fps(p.Int(1)), p.Int(2), seq, 0, id);
        }
        if (rpc.Bytes(3) is { } fps) return new(-1, 0, 0, Fps(ProtoFields.Read(fps).Int(1)), 0, seq, 0, id);
        if (rpc.Bytes(4) is { } quality) return new(-1, 0, 0, 0, ProtoFields.Read(quality).Int(1), seq, 0, id);
        if (rpc.Bytes(6) is { } resolution) { var p = ProtoFields.Read(resolution); return new(-1, p.Int(1), p.Int(2), 0, 0, seq, 0, id); }
        return null;
    }
    private static int Fps(int value) => value switch { 1 => 30, 2 => 60, 3 => 90, 4 => 144, >= 15 and <= 144 => value, _ => 0 };
    public static byte[] Reply(CaptureUpdate update, bool success)
    {
        if (update.RpcId is { } id) return Join(Int(1, update.Sequence), Blob(22, Join(Blob(1, Int(1, id)), Blob(2, success ? [] : Int(1, 1)))));
        return Join(Int(1, update.Sequence), Blob(19, Join(Int(1, update.Sequence), Int(2, (ulong)update.Flag), Int(3, success ? 0u : 1u))));
    }
    private static byte[] Join(params byte[][] parts) => parts.SelectMany(x => x).ToArray();
    private static byte[] Int(uint tag, ulong value) => Join(Var(tag << 3), Var(value));
    private static byte[] Blob(uint tag, byte[] value) => Join(Var(tag << 3 | 2), Var((ulong)value.Length), value);
    private static byte[] Var(ulong value) { var b = new List<byte>(); do { var x = (byte)(value & 127); value >>= 7; b.Add(value == 0 ? x : (byte)(x | 128)); } while (value != 0); return b.ToArray(); }
}
