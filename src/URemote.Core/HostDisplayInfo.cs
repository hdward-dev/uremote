namespace URemote.Core;

// Screen-list protobuf shape observed in authorized official host signal_app_data captures.
// Uses physical screen dimensions; iOS switches sources on the primary video track.
public static class HostDisplayInfo
{
    public const int MaxSupportedFps = 60;
    public static byte[] Encode(ulong sequence, ulong timestamp, int displayCount = 1, IReadOnlyList<(int Width, int Height)>? dimensions = null, bool singleVideoStream = false)
    {
        if (displayCount is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(displayCount));
        var list = new List<byte>();
        for (var index = 0; index < displayCount; index++)
        {
        var rectangle = new List<byte>();
        var width = (ulong)(dimensions?[index].Width ?? 1280); var height = (ulong)(dimensions?[index].Height ?? 720);
        Int(rectangle, 3, width); Int(rectangle, 4, height); Int(rectangle, 5, width); Int(rectangle, 6, height);
        var screen = new List<byte>();
        Int(screen, 1, (ulong)index); Int(screen, 2, MaxSupportedFps);
        Blob(screen, 3, rectangle.ToArray()); Blob(screen, 4, rectangle.ToArray()); Blob(screen, 6, rectangle.ToArray());
        Int(screen, 7, index == 0 ? 1u : 0u);
        Var(screen, (8 << 3) | 1); // IEEE double scale, little endian.
        screen.AddRange([0, 0, 0, 0, 0, 0, 0xf0, 0x3f]);
        // Official Swift metadata: field 11 resolutionType, 12 videoTrackIndex, 13 builtinScreenType.
        Int(screen, 11, 4); Int(screen, 12, singleVideoStream ? 0u : (ulong)index); Int(screen, 13, 2);
        Blob(list, 1, screen.ToArray());
        }
        Int(list, 2, 0);
        var envelope = new List<byte>(); Int(envelope, 1, sequence); Int(envelope, 2, timestamp); Blob(envelope, 7, list.ToArray());
        return envelope.ToArray();
    }
    private static void Int(List<byte> b, uint tag, ulong value) { Var(b, tag << 3); Var(b, value); }
    private static void Blob(List<byte> b, uint tag, byte[] value) { Var(b, (tag << 3) | 2); Var(b, (ulong)value.Length); b.AddRange(value); }
    private static void Var(List<byte> b, ulong value)
    { do { var x = (byte)(value & 127); value >>= 7; b.Add(value == 0 ? x : (byte)(x | 128)); } while (value != 0); }
}
