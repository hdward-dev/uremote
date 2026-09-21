using System.Text.Json;

namespace URemote.Core;

// Structural diagnostics only: no input text, key codes, coordinates, IDs, or arbitrary string values.
public static class ControlPacketShape
{
    public static string Describe(bool isText, ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > 8192) return "large";
        try
        {
            if (bytes.Length > 0 && bytes.Span[0] == (byte)'{')
            {
                using var doc = JsonDocument.Parse(bytes);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return "text-json-nonobject";
                return "json-fields=" + string.Join(',', doc.RootElement.EnumerateObject().Take(20)
                    .Where(p => p.Name.Length <= 40 && p.Name.All(c => char.IsAsciiLetter(c) || c == '_'))
                    .Select(p => p.Name + ":" + p.Value.ValueKind));
            }
            var root = ProtoFields.Read(bytes);
            string Shape(ProtoFields f) => string.Join(',', f.Items.Take(20).Select(x => x.Tag + ":" + x.WireType));
            var shape = "proto=" + Shape(root);
            foreach (var tag in root.Items.Where(f => f.WireType == 2 && f.Tag != 2).Select(f => f.Tag))
                if (root.Bytes(tag) is { } nested)
                {
                    ProtoFields fields;
                    try { fields = ProtoFields.Read(nested); } catch (FormatException) { continue; }
                    shape += ";nested" + tag + "=" + Shape(fields);
                    if (tag == 3) shape += ";echoAction=" + (fields.Int(1) is 0 or 1 ? fields.Int(1) : -1)
                        + ";argsPresent=" + (fields.Bytes(2)?.Length > 0);
                }
            return shape;
        }
        catch (Exception e) when (e is FormatException or JsonException or System.Text.DecoderFallbackException)
        { return "unrecognized-shape:" + (bytes.Length > 1 && bytes.Span[0] == 0x1f && bytes.Span[1] == 0x8b ? "gzip"
            : bytes.Length > 1 && bytes.Span[0] == 0x78 ? "possible-zlib" : "binary") + ";bytes=" + bytes.Length; }
    }
}
