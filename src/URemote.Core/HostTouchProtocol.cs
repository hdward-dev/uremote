using System.Buffers.Binary;
namespace URemote.Core;

public sealed record HostTouchPoint(uint Id, double X, double Y)
{
    public override string ToString() => "TouchPoint(redacted)";
}
public sealed record HostTouchEvent(int Phase, IReadOnlyList<HostTouchPoint> Points)
{
    public override string ToString() => "TouchEvent(redacted)";
}
// Official 4.41 Swift protobuf name maps: envelope.input_event=20,
// InputEvent.touch_event=1, TouchEvent.event_type=1/touch_points=2.
public static class HostTouchProtocol
{
    public static HostTouchEvent? Decode(ReadOnlyMemory<byte> packet)
    {
        if (packet.Length == 0 || packet.Span[0] == '{') return null;
        if (packet.Length > 8192) throw new FormatException("Touch envelope too large.");
        var root = ProtoFields.Read(packet);
        if (root.Bytes(20) is not { } input || ProtoFields.Read(input).Bytes(1) is not { } bytes) return null;
        var body = ProtoFields.Read(bytes, [2]);
        var phase = body.Int(1);
        if (body.Items.Any(f => f.Tag == 1 && f.Value > 4)) throw new FormatException("Invalid touch phase.");
        if (phase == 0) return new(0, []); // Initial empty touch state, not a contact.
        var points = new List<HostTouchPoint>(); var ids = new HashSet<uint>();
        foreach (var raw in body.Repeated(2))
        {
            if (points.Count >= 16) throw new FormatException("Too many touch contacts.");
            var point = ProtoFields.Read(raw);
            var idField = point.Items.FirstOrDefault(f => f.Tag == 1);
            if (idField is not null && (idField.WireType != 0 || idField.Value > uint.MaxValue)) throw new FormatException("Invalid touch ID.");
            var id = (uint)(idField?.Value ?? 0);
            if (!ids.Add(id)) throw new FormatException("Duplicate touch ID.");
            double Read(int tag)
            {
                var field = point.Items.FirstOrDefault(f => f.Tag == tag);
                if (field is null) return 0; // Protobuf float default.
                if (field.WireType != 5) throw new FormatException("Invalid touch coordinate type.");
                var value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(field.Data.Span));
                if (!float.IsFinite(value) || value < 0 || value > 1) throw new FormatException("Touch coordinate is not normalized.");
                return value;
            }
            points.Add(new(id, Read(2), Read(3)));
        }
        return new(phase, points);
    }
    public static byte[]? ReplyMetrics(ReadOnlyMemory<byte> packet, ulong sequence, bool supportTouch)
    {
        if (packet.Length == 0 || packet.Length > 8192 || packet.Span[0] == '{') return null;
        var root = ProtoFields.Read(packet);
        if (root.Bytes(13) is not { } request) return null;
        var metrics = ProtoFields.Read(request, [1]);
        if (metrics.Bytes(3) is not null || metrics.Bytes(4) is not null || metrics.Text(2).Length != 0) return null;
        // SystemMetrics.general=3, GeneralMetric.support_touch=1.
        return FileTransferProtocol.Join(FileTransferProtocol.Int(1, sequence),
            FileTransferProtocol.Blob(13, FileTransferProtocol.Blob(3, FileTransferProtocol.Int(1, supportTouch ? 1u : 0u))));
    }
}
