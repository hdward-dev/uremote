using System.Buffers.Binary;
namespace URemote.Core;

public sealed record TerminalFrame(byte Type, ushort Flags, uint SessionId, byte[] Payload)
{ public override string ToString() => $"TerminalFrame(type={Type}; contents redacted)"; }

public static class HostTerminalProtocol
{
    public static TerminalFrame? DecodeFrame(byte[] bytes)
    {
        if (bytes.Length < 4 || !bytes.AsSpan(0, 4).SequenceEqual("TERM"u8)) return null;
        if (bytes.Length < 16 || bytes.Length > 65552 || bytes[4] != 1 || bytes[5] is < 1 or > 16
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)) != bytes.Length - 16)
            throw new FormatException("Invalid terminal frame.");
        return new(bytes[5], BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)), bytes.AsSpan(16).ToArray());
    }
    public static byte[] EncodeFrame(byte type, uint sessionId, ReadOnlySpan<byte> payload)
    {
        if (type is < 1 or > 16 || payload.Length > 65536) throw new ArgumentOutOfRangeException(nameof(type));
        var result = new byte[16 + payload.Length]; "TERM"u8.CopyTo(result); result[4] = 1; result[5] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), sessionId);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), (uint)payload.Length);
        payload.CopyTo(result.AsSpan(16)); return result;
    }
    // Official 4.41: RpcRequest.terminalCheckEnvReq=28; RpcResponse.terminalCheckEnvResp=24.
    public static byte[]? EnvironmentReply(byte[] bytes, bool enabled = true)
    {
        if (bytes.Length > 8192) return null;
        ProtoFields root;
        try { root = ProtoFields.Read(bytes); } catch (FormatException) { return null; }
        if (root.Bytes(21) is not { } request) return null;
        var rpc = ProtoFields.Read(request);
        if (rpc.Bytes(28) is not { Length: 0 } || rpc.Bytes(1) is not { } header) return null;
        var id = ProtoFields.Read(header).Items.FirstOrDefault(f => f.Tag == 1 && f.WireType == 0)?.Value ?? 0;
        return Blob(22, Join(Blob(1, Int(1, id)), Blob(24, Int(1, enabled ? 0u : 403u))));
    }
    private static byte[] Join(params byte[][] parts) => parts.SelectMany(x => x).ToArray();
    private static byte[] Int(uint tag, ulong value) => Join(Var(tag << 3), Var(value));
    private static byte[] Blob(uint tag, byte[] value) => Join(Var(tag << 3 | 2), Var((ulong)value.Length), value);
    private static byte[] Var(ulong value) { var b = new List<byte>(); do { var x = (byte)(value & 127); value >>= 7; b.Add(value == 0 ? x : (byte)(x | 128)); } while (value != 0); return b.ToArray(); }
}
