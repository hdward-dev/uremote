using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace URemote.Core;

public sealed record UuSignalFrame(int EngineType, JsonNode? OpenData = null,
    SocketIoPacket? Packet = null, IReadOnlyList<byte[]>? Attachments = null)
{
    public override string ToString() => $"UuSignalFrame(EngineType={EngineType}, payload redacted)";
}

// Feed complete WebSocket messages, using a separate instance per direction.
// Frame format verified against authorized official Mac host traffic.
public sealed class UuSignalReader
{
    public const int MaximumBytes = 1_000_000;
    private SocketIoPacket? pending;
    private readonly List<byte[]> buffers = [];
    private int bufferedBytes;

    public UuSignalFrame? ReadText(string text)
    {
        if (text.Length == 0 || Encoding.UTF8.GetByteCount(text) > MaximumBytes || text[0] is < '0' or > '6')
            throw new FormatException("Invalid Engine.IO message.");
        var type = text[0] - '0';
        if (type != 4)
            return new(type, type == 0 ? JsonNode.Parse(text[1..]) : null);
        if (pending is not null) throw new FormatException("Binary attachments are incomplete.");
        var packet = SocketIoCodec.Parse(text[1..]);
        if (packet.Attachments == 0) return new(4, Packet: packet, Attachments: Array.Empty<byte[]>());
        pending = packet;
        bufferedBytes = 0;
        buffers.Clear();
        return null;
    }

    public UuSignalFrame? ReadBinary(ReadOnlySpan<byte> message)
    {
        if (pending is null || message.Length == 0 || message[0] != 4
            || message.Length - 1 > MaximumBytes - bufferedBytes)
            throw new FormatException("Invalid or unexpected Engine.IO binary attachment.");
        var content = message[1..].ToArray();
        buffers.Add(content);
        bufferedBytes += content.Length;
        if (buffers.Count < pending.Attachments) return null;
        var result = new UuSignalFrame(4, Packet: pending, Attachments: buffers.ToArray());
        pending = null;
        buffers.Clear();
        bufferedBytes = 0;
        return result;
    }

    public static string DecodeGzipSdp(UuSignalFrame frame)
    {
        var placeholder = frame.Packet?.Data?[1]?["data"]?["gzip_sdp"];
        if (placeholder?["_placeholder"]?.GetValue<bool>() != true
            || placeholder["num"] is not JsonValue number || !number.TryGetValue<int>(out var index)
            || index < 0 || frame.Attachments is null || index >= frame.Attachments.Count)
            throw new FormatException("Missing SDP binary attachment.");
        using var input = new MemoryStream(frame.Attachments[index], writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = gzip.Read(chunk)) != 0)
        {
            if (output.Length + count > MaximumBytes) throw new InvalidDataException("SDP exceeds the size limit.");
            output.Write(chunk, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(output.ToArray());
    }
}
