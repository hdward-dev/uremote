using System.Text;
namespace URemote.Core;

public sealed record TransferField(int Tag, ulong Number, byte[] Data);
public sealed record TransferMessage(bool Response, ulong RequestId, int Kind, byte[] Body);
public static class FileTransferProtocol
{
    public static byte[] Join(params byte[][] values) => values.SelectMany(x => x).ToArray();
    public static byte[] Int(uint tag, ulong value) => Join(Var(tag << 3), Var(value));
    public static byte[] Text(uint tag, string value) => Blob(tag, Encoding.UTF8.GetBytes(value));
    public static byte[] Blob(uint tag, byte[] value) => Join(Var(tag << 3 | 2), Var((ulong)value.Length), value);
    private static byte[] Var(ulong value) { var b = new List<byte>(); do { var x = (byte)(value & 127); value >>= 7; b.Add(value == 0 ? x : (byte)(x | 128)); } while(value != 0); return b.ToArray(); }
    public static byte[] Encode(bool response, ulong id, int kind, byte[] body) => Join(Int(1, id), Blob(response ? 22u : 21u,
        Join(Blob(1, Int(1, id)), Blob(response ? 4u : 8u, Blob((uint)kind, body)))));
    public static TransferMessage? Decode(byte[] bytes)
    {
        var root = Read(bytes); var response = root.Any(x => x.Tag == 22);
        var rpcBytes = root.FirstOrDefault(x => x.Tag == (response ? 22 : 21))?.Data;
        if (rpcBytes is null) return null;
        var rpc = Read(rpcBytes); var ftp = rpc.FirstOrDefault(x => x.Tag == (response ? 4 : 8))?.Data;
        if (ftp is null) return null;
        var header = rpc.FirstOrDefault(x => x.Tag == 1)?.Data ?? [];
        var operation = Read(ftp).SingleOrDefault() ?? throw new FormatException("Missing file operation.");
        return new(response, Number(header, 1), operation.Tag, operation.Data);
    }
    public static ulong Number(byte[] bytes, int tag) => Read(bytes).FirstOrDefault(x => x.Tag == tag)?.Number ?? 0;
    public static byte[] Data(byte[] bytes, int tag) => Read(bytes).FirstOrDefault(x => x.Tag == tag)?.Data ?? [];
    public static string String(byte[] bytes, int tag) => new UTF8Encoding(false, true).GetString(Data(bytes, tag));
    public static IReadOnlyList<TransferField> Read(byte[] bytes)
    {
        if (bytes.Length > 524288) throw new FormatException("Transfer packet exceeds limit.");
        var result = new List<TransferField>(); var pos = 0;
        ulong GetVar() { ulong v = 0; for(int i=0;i<10;i++) { if(pos>=bytes.Length) throw new FormatException(); var b=bytes[pos++]; if(i==9 && b>1) throw new FormatException(); v |= (ulong)(b&127) << (i*7); if((b&128)==0) return v; } throw new FormatException(); }
        while(pos<bytes.Length)
        {
            if(result.Count>=8192) throw new FormatException();
            var key = GetVar(); var tag = (int)(key >> 3); if(tag<=0) throw new FormatException();
            if((key&7)==0) result.Add(new(tag,GetVar(),[]));
            else { var len=(key&7) switch { 2=>GetVar(),1=>8UL,5=>4UL,_=>throw new FormatException() }; if(len>(ulong)(bytes.Length-pos)) throw new FormatException(); result.Add(new(tag,0,bytes.AsSpan(pos,(int)len).ToArray())); pos+=(int)len; }
        }
        return result;
    }
}
