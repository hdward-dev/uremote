using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace URemote.Core;

// Native port of uurc-web's MIT connect options and control channel encoders.
public static class ControllerProtocol
{
    public static byte[] ConnectOptions(string deviceId, int captureType = 1) => Join(Int(1, (ulong)captureType), Int(2, ulong.MaxValue),
        Blob(3, Join(Int(1, 2), Int(2, 3), Int(3, 1), Int(4, 1), Blob(5, Join(Int(1, 1920), Int(2, 1080))))),
        Blob(4, Join(Int(1, 60), Int(2, 1), Int(3, 3840), Int(4, 2160), Int(5, 1))),
        Int(8, 4), Text(9, deviceId), Blob(11, Join(Int(1, 2), Int(2, 1), Int(3, 2))), Text(12, "4.41.0"));
    public static string StreamerData(string controlId, string iceId) => JsonSerializer.Serialize(new
    {
        control_id = controlId,
        device_capability = new {
            display_info = new[] { new { id = 0, fps = 60, type = 0, hdr = -1 } },
            video_codec_capability = new[] { new { video_codec = 1, width = 3840, height = 2160, chroma_sampling = 1, bit_depth = 8, codec_impl = -1 } }, ice_id = iceId }
    });
    public static byte[] Input(ulong seq, string json, int displayId) => Envelope(seq, 11, Join(Text(2, json), Int(3, (ulong)displayId)));
    public static byte[] Echo(ulong seq) => Envelope(seq, 3, Join(Text(2, "{\"seq\":" + seq + "}"), Blob(4, Int(3, 2))));
    public static byte[] Capture(ulong seq, int displayId, int quality = 3) => Envelope(seq, 9,
        Join(Int(2, 2), Int(3, (ulong)quality), Int(4, 1), Int(5, (ulong)displayId), Int(8, 1)));
    private static byte[] Envelope(ulong seq, uint tag, byte[] body) => Join(Int(1, seq), Int(2, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds()), Blob(tag, body));
    private static byte[] Join(params byte[][] parts) => parts.SelectMany(x => x).ToArray();
    private static byte[] Int(uint tag, ulong value) => Join(Var(tag << 3), Var(value));
    private static byte[] Text(uint tag, string value) => Blob(tag, Encoding.UTF8.GetBytes(value));
    private static byte[] Blob(uint tag, byte[] value) => Join(Var(tag << 3 | 2), Var((ulong)value.Length), value);
    private static byte[] Var(ulong value) { var bytes = new List<byte>(); do { var b = (byte)(value & 127); value >>= 7; bytes.Add(value == 0 ? b : (byte)(b | 128)); } while (value != 0); return bytes.ToArray(); }
}
