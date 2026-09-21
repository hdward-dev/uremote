using System.Text;

namespace URemote.Core;

// Field numbers ported from uurc-web shared/streamer/internal/connectOptionsSchema.ts (MIT).
public sealed record CaptureParameters(int Fps, int VideoQuality, bool CursorCapture, int ChooseResolutionType,
    ScreenResolution? LocalResolution, ScreenResolution? ChosenResolution, int ChromaFormat,
    int MaxCustomBitrate, bool EnableHdr, int AutoFrameQuality, int FpsCount);
public sealed record ScreenResolution(int Width, int Height);
public sealed record DecoderCapability(int Fps, int CodecType, int Width, int Height, int ChromaFormat);
public sealed record VirtualDisplayMode(int Width, int Height, int Fps);
public sealed record StreamerConnectOptions(int CaptureType, int TypeValue, CaptureParameters? Capture,
    IReadOnlyList<DecoderCapability> DecoderCapabilities, bool ForceVirtualDisplay,
    IReadOnlyList<VirtualDisplayMode> VirtualDisplays, ScreenResolution? VirtualDisplayResolution,
    int ClientType, string DeviceId, int ControlConnectType, IReadOnlyDictionary<int, int> FeatureFlags,
    string ClientVersion, int UnknownFieldCount)
{
    public override string ToString() => $"StreamerConnectOptions(CaptureType={CaptureType}, identity redacted)";

    public static StreamerConnectOptions Decode(ReadOnlyMemory<byte> data)
    {
        var fields = ProtoFields.Read(data, [4, 6]);
        CaptureParameters? capture = null;
        if (fields.Bytes(3) is { } captureBytes)
        {
            var p = ProtoFields.Read(captureBytes);
            capture = new(p.Int(1), p.Int(2), p.Bool(3), p.Int(4), Resolution(p.Bytes(5)),
                Resolution(p.Bytes(6)), p.Int(7), p.Int(8), p.Bool(9), p.Int(10), p.Int(11));
        }
        var decoders = fields.Repeated(4).Select(x =>
        {
            var p = ProtoFields.Read(x);
            return new DecoderCapability(p.Int(1), p.Int(2), p.Int(3), p.Int(4), p.Int(5));
        }).ToArray();
        var displays = fields.Repeated(6).Select(x =>
        {
            var p = ProtoFields.Read(x);
            return new VirtualDisplayMode(p.Int(1), p.Int(2), p.Int(3));
        }).ToArray();
        var features = new Dictionary<int, int>();
        if (fields.Bytes(11) is { } flagBytes)
        {
            var p = ProtoFields.Read(flagBytes);
            for (var tag = 1; tag <= 11; tag++) features[tag] = p.Int(tag);
        }
        return new(fields.Int(1), fields.Int(2), capture, decoders, fields.Bool(5), displays,
            Resolution(fields.Bytes(7)), fields.Int(8), fields.Text(9), fields.Int(10), features,
            fields.Text(12), fields.Items.Count(x => x.Tag > 12));
    }

    private static ScreenResolution? Resolution(ReadOnlyMemory<byte>? bytes)
    {
        if (bytes is null) return null;
        var p = ProtoFields.Read(bytes.Value);
        return new(p.Int(1), p.Int(2));
    }
}

internal sealed record ProtoField(int Tag, int WireType, ulong Value, ReadOnlyMemory<byte> Data);
internal sealed class ProtoFields(List<ProtoField> items)
{
    public IReadOnlyList<ProtoField> Items => items;
    public static ProtoFields Read(ReadOnlyMemory<byte> memory, int[]? repeated = null)
    {
        if (memory.Length > 131072) throw new FormatException("Protobuf message exceeds limit.");
        var data = memory.Span;
        var offset = 0;
        var items = new List<ProtoField>();
        var seen = new HashSet<int>();
        while (offset < data.Length)
        {
            if (items.Count >= 256) throw new FormatException("Too many protobuf fields.");
            var key = Varint(data, ref offset);
            if (key >> 3 is 0 or > 536870911) throw new FormatException("Invalid protobuf tag.");
            var tag = (int)(key >> 3);
            if (!seen.Add(tag) && !(repeated?.Contains(tag) ?? false)) throw new FormatException("Duplicate singular protobuf field.");
            var wire = (int)(key & 7);
            ulong value = 0;
            ReadOnlyMemory<byte> content = default;
            if (wire == 0) value = Varint(data, ref offset);
            else
            {
                ulong length = wire switch { 1 => 8, 2 => Varint(data, ref offset), 5 => 4,
                    _ => throw new FormatException("Unsupported protobuf wire type.") };
                if (length > (ulong)(data.Length - offset)) throw new FormatException("Truncated protobuf field.");
                content = memory.Slice(offset, (int)length);
                offset += (int)length;
            }
            items.Add(new(tag, wire, value, content));
        }
        return new(items);
    }
    private static ulong Varint(ReadOnlySpan<byte> bytes, ref int offset)
    {
        ulong result = 0;
        for (var i = 0; i < 10; i++)
        {
            if (offset >= bytes.Length) throw new FormatException("Truncated protobuf varint.");
            var b = bytes[offset++];
            if (i == 9 && b > 1) throw new FormatException("Overflowing protobuf varint.");
            result |= (ulong)(b & 127) << (i * 7);
            if ((b & 128) == 0) return result;
        }
        throw new FormatException("Invalid protobuf varint.");
    }
    private ProtoField? Field(int tag, int type)
    {
        var field = items.FirstOrDefault(x => x.Tag == tag);
        if (field is not null && field.WireType != type) throw new FormatException("Unexpected protobuf field type.");
        return field;
    }
    public int Int(int tag) => unchecked((int)(Field(tag, 0)?.Value ?? 0));
    public bool Bool(int tag)
    {
        var value = Field(tag, 0)?.Value ?? 0;
        if (value > 1) throw new FormatException("Invalid protobuf boolean.");
        return value == 1;
    }
    public ReadOnlyMemory<byte>? Bytes(int tag) => Field(tag, 2)?.Data;
    public string Text(int tag) => Bytes(tag) is { } bytes ? new UTF8Encoding(false, true).GetString(bytes.Span) : "";
    public IEnumerable<ReadOnlyMemory<byte>> Repeated(int tag)
    {
        foreach (var item in items.Where(x => x.Tag == tag))
        {
            if (item.WireType != 2) throw new FormatException("Unexpected repeated protobuf type.");
            yield return item.Data;
        }
    }
}
