using System.Buffers.Binary;
using URemote.Core;
using URemote.Linux;

static class WaylandTests
{
    public static void Run(Action<bool, string> check)
    {
        var normalized = WaylandVirtualPointer.AbsolutePayload(123, .5, .25, PointerCoordinates.MacNormalized, 0, 0);
        check(BinaryPrimitives.ReadUInt32LittleEndian(normalized.AsSpan(4)) == 500000
            && BinaryPrimitives.ReadUInt32LittleEndian(normalized.AsSpan(8)) == 250000
            && BinaryPrimitives.ReadUInt32LittleEndian(normalized.AsSpan(12)) == 1000000,
            "Mac coordinates map into Wayland absolute extents");
        var pixel = WaylandVirtualPointer.AbsolutePayload(123, 1280, 720, PointerCoordinates.WindowsPixels, 2560, 1440);
        check(BinaryPrimitives.ReadUInt32LittleEndian(pixel.AsSpan(4)) == 1280
            && BinaryPrimitives.ReadUInt32LittleEndian(pixel.AsSpan(16)) == 1440,
            "pixel coordinates retain selected output extents");
        check(WaylandVirtualPointer.LinuxButton(1) == 0x110 && WaylandVirtualPointer.LinuxButton(4) == 0x112,
            "UU pointer button flags map to Linux button codes");
        try { WaylandVirtualPointer.AbsolutePayload(0, double.NaN, 0, PointerCoordinates.MacNormalized, 0, 0); check(false, "nonfinite pointer rejected"); }
        catch (ArgumentException) { check(true, "nonfinite pointer rejected"); }
        try { WaylandVirtualPointer.AbsolutePayload(0, 3000, 0, PointerCoordinates.WindowsPixels, 2560, 1440); check(false, "out-of-display pointer rejected"); }
        catch (ArgumentException) { check(true, "out-of-display pointer rejected"); }
        check(WaylandScreenCapture.ValidateBuffer(2560, 1440, 10240) == 14745600, "capture buffer size uses stride");
        try { WaylandScreenCapture.ValidateBuffer(16384, 16384, uint.MaxValue); check(false, "oversized capture buffer rejected"); }
        catch (FormatException) { check(true, "oversized capture buffer rejected"); }
        try { WaylandScreenCapture.ValidateBuffer(2560, 1440, 1); check(false, "invalid capture stride rejected"); }
        catch (FormatException) { check(true, "invalid capture stride rejected"); }
    }
}
