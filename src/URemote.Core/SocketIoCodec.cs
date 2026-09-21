using System.Globalization;
using System.Text.Json.Nodes;

namespace URemote.Core;

public sealed record SocketIoPacket(int Type, string Namespace = "/", int Attachments = 0, long? Id = null, JsonNode? Data = null);

// Socket.IO packet payload; Engine.IO message prefix ('4') is handled separately.
public static class SocketIoCodec
{
    public static SocketIoPacket Parse(string value)
    {
        if (value.Length == 0 || value[0] is < '0' or > '6') throw new FormatException("Invalid packet type.");
        int type = value[0] - '0', offset = 1, attachments = 0;
        if (type is 5 or 6)
        {
            var count = ReadDigits(value, ref offset);
            if (!int.TryParse(count, out attachments) || attachments > 10 || offset >= value.Length || value[offset++] != '-')
                throw new FormatException("Invalid attachment count.");
        }
        var ns = "/";
        if (offset < value.Length && value[offset] == '/')
        {
            var start = offset;
            while (offset < value.Length && value[offset] != ',') offset++;
            ns = value[start..offset];
            if (offset < value.Length) offset++;
        }
        var digits = ReadDigits(value, ref offset);
        long? id = digits.Length == 0 ? null : long.Parse(digits, CultureInfo.InvariantCulture);
        return new(type, ns, attachments, id, offset == value.Length ? null : JsonNode.Parse(value[offset..]));
    }

    public static string Encode(SocketIoPacket packet)
    {
        if (packet.Type is < 0 or > 6 || packet.Attachments is < 0 or > 10 || packet.Id < 0)
            throw new ArgumentException("Invalid packet.");
        return packet.Type.ToString(CultureInfo.InvariantCulture)
            + (packet.Type is 5 or 6 ? packet.Attachments.ToString(CultureInfo.InvariantCulture) + "-" : "")
            + (packet.Namespace == "/" ? "" : packet.Namespace + ",")
            + packet.Id?.ToString(CultureInfo.InvariantCulture) + packet.Data?.ToJsonString();
    }

    private static string ReadDigits(string value, ref int offset)
    {
        var start = offset;
        while (offset < value.Length && value[offset] is >= '0' and <= '9') offset++;
        return value[start..offset];
    }
}
