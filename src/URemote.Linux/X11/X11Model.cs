using System.Numerics;

namespace URemote.Linux;

internal sealed record X11Monitor(uint Id, int X, int Y, int Width, int Height, int MilliHz, string Name)
{
    internal WaylandGlobal Global => new(Id, "wl_output", 1);
}

internal static class X11Model
{
    internal const int RootOutputId = 1;
    internal const uint Interlace = 16;
    internal const uint DoubleScan = 32;

    internal static int MilliHz(ulong dotClock, uint hTotal, uint vTotal, uint flags)
    {
        double vertical = vTotal;
        if ((flags & DoubleScan) != 0) vertical *= 2;
        if ((flags & Interlace) != 0) vertical /= 2;
        if (dotClock == 0 || hTotal == 0 || vertical < 1) return 60_000;
        var hz = dotClock / ((double)hTotal * vertical);
        if (hz is < 1 or > 1000 || double.IsNaN(hz) || double.IsInfinity(hz)) return 60_000;
        var milli = (int)Math.Round(hz * 1000);
        return milli <= 0 ? 60_000 : milli;
    }

    /// <summary>Intersection of a RandR rectangle with the root framebuffer. Null when nothing is visible.</summary>
    internal static (int X, int Y, int Width, int Height)? Visible(int x, int y, int width, int height, int rootWidth, int rootHeight)
    {
        if (width <= 0 || height <= 0 || rootWidth <= 0 || rootHeight <= 0) return null;
        var x0 = Math.Max(x, 0);
        var y0 = Math.Max(y, 0);
        var x1 = Math.Min((long)x + width, rootWidth);
        var y1 = Math.Min((long)y + height, rootHeight);
        if (x1 <= x0 || y1 <= y0 || x1 - x0 > 16384 || y1 - y0 > 16384) return null;
        return (x0, y0, (int)(x1 - x0), (int)(y1 - y0));
    }

    internal static bool Intersects(int x, int y, int width, int height, int otherX, int otherY, int otherWidth, int otherHeight)
    {
        if (width <= 0 || height <= 0 || otherWidth <= 0 || otherHeight <= 0) return false;
        var right = (long)x + width;
        var bottom = (long)y + height;
        var otherRight = (long)otherX + otherWidth;
        var otherBottom = (long)otherY + otherHeight;
        return x < otherRight && right > otherX && y < otherBottom && bottom > otherY;
    }

    // UU flags: 1 left, 2 right, 4 middle, 8 back, 16 forward.
    // X buttons: 1 left, 2 middle, 3 right, 8 back, 9 forward.
    internal static int Button(int uuButton) => uuButton switch
    {
        1 => 1,
        2 => 3,
        4 => 2,
        8 => 8,
        16 => 9,
        _ => throw new ArgumentException("Unknown UU mouse button.")
    };

    internal static int XKeyCode(uint linuxEvdev) => checked((int)linuxEvdev + 8);

    /// <summary>Positive Y is wheel-up (X button 4). Positive X is wheel-right (X button 7). At most 100 clicks.</summary>
    internal static int[] WheelButtons(double deltaX, double deltaY)
    {
        var (horizontal, vertical) = UInputPointerMap.Wheel(deltaX, deltaY);
        var buttons = new List<int>(Math.Abs(horizontal) + Math.Abs(vertical));
        var verticalClicks = Math.Min(Math.Abs(vertical), 100);
        var horizontalClicks = Math.Min(Math.Abs(horizontal), 100);
        for (var i = 0; i < verticalClicks; i++) buttons.Add(vertical > 0 ? 4 : 5);
        for (var i = 0; i < horizontalClicks; i++) buttons.Add(horizontal > 0 ? 7 : 6);
        return buttons.ToArray();
    }

    internal static (int X, int Y) RootPoint(X11Monitor monitor, double x, double y, URemote.Core.PointerCoordinates coordinates)
        => UInputPointerMap.Absolute(new ScreenRect(monitor.Id, monitor.X, monitor.Y, monitor.Width, monitor.Height), x, y, coordinates);
}

internal static class X11Pixels
{
    internal static bool IsBgrx(int bitsPerPixel, int byteOrder, ulong red, ulong green, ulong blue)
        => bitsPerPixel == 32 && byteOrder == 0 && red == 0x00FF0000 && green == 0x0000FF00 && blue == 0x000000FF;

    internal static byte[] CopyBgr0(ReadOnlySpan<byte> source, int width, int height, int stride, int bitsPerPixel, int byteOrder, ulong red, ulong green, ulong blue)
    {
        if (width is < 1 or > 16384 || height is < 1 or > 16384 || stride <= 0)
            throw new FormatException("无效的 X11 图像尺寸。");
        var tight = width * 4;
        if ((long)stride * height > source.Length || stride < (bitsPerPixel == 32 ? tight : width * 3))
            throw new FormatException("X11 图像缓冲区不足。");
        var pixels = new byte[tight * height];
        try
        {
            if (IsBgrx(bitsPerPixel, byteOrder, red, green, blue))
            {
                if (stride == tight) source[..pixels.Length].CopyTo(pixels);
                else for (var row = 0; row < height; row++)
                    source.Slice(row * stride, tight).CopyTo(pixels.AsSpan(row * tight, tight));
                for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 0;
                return pixels;
            }
            if (bitsPerPixel is not (24 or 32)) throw new FormatException("X11 像素格式不受支持。");
            for (var row = 0; row < height; row++)
            {
                var sourceRow = source.Slice(row * stride);
                var dest = row * tight;
                for (var column = 0; column < width; column++)
                {
                    var value = Pixel(sourceRow, column, bitsPerPixel, byteOrder);
                    pixels[dest++] = Channel(value, blue);
                    pixels[dest++] = Channel(value, green);
                    pixels[dest++] = Channel(value, red);
                    pixels[dest++] = 0;
                }
            }
            return pixels;
        }
        catch
        {
            Array.Clear(pixels);
            throw;
        }
    }

    internal static void OverlayCursor(byte[] pixels, int width, int height, int cursorX, int cursorY, int cursorWidth, int cursorHeight, int hotX, int hotY, ReadOnlySpan<uint> argb)
    {
        if (cursorWidth <= 0 || cursorHeight <= 0 || cursorWidth > 256 || cursorHeight > 256 || argb.Length < cursorWidth * cursorHeight)
            return;
        var originX = cursorX - hotX;
        var originY = cursorY - hotY;
        for (var row = 0; row < cursorHeight; row++)
        {
            var targetY = originY + row;
            if ((uint)targetY >= (uint)height) continue;
            for (var column = 0; column < cursorWidth; column++)
            {
                var targetX = originX + column;
                if ((uint)targetX >= (uint)width) continue;
                var pixel = argb[row * cursorWidth + column];
                var alpha = (byte)(pixel >> 24);
                if (alpha == 0) continue;
                var index = (targetY * width + targetX) * 4;
                if (alpha == 255)
                {
                    pixels[index] = (byte)pixel;
                    pixels[index + 1] = (byte)(pixel >> 8);
                    pixels[index + 2] = (byte)(pixel >> 16);
                    pixels[index + 3] = 0;
                    continue;
                }
                pixels[index] = Blend(pixels[index], (byte)pixel, alpha);
                pixels[index + 1] = Blend(pixels[index + 1], (byte)(pixel >> 8), alpha);
                pixels[index + 2] = Blend(pixels[index + 2], (byte)(pixel >> 16), alpha);
            }
        }
    }

    private static uint Pixel(ReadOnlySpan<byte> row, int column, int bitsPerPixel, int byteOrder)
    {
        var offset = column * (bitsPerPixel / 8);
        if (bitsPerPixel == 24)
            return byteOrder == 0 ? row[offset] | ((uint)row[offset + 1] << 8) | ((uint)row[offset + 2] << 16)
                : ((uint)row[offset] << 16) | ((uint)row[offset + 1] << 8) | row[offset + 2];
        return byteOrder == 0
            ? row[offset] | ((uint)row[offset + 1] << 8) | ((uint)row[offset + 2] << 16) | ((uint)row[offset + 3] << 24)
            : ((uint)row[offset] << 24) | ((uint)row[offset + 1] << 16) | ((uint)row[offset + 2] << 8) | row[offset + 3];
    }

    private static byte Channel(uint value, ulong mask)
    {
        if (mask == 0) return 0;
        var bits = (uint)mask;
        var shift = BitOperations.TrailingZeroCount(bits);
        var width = BitOperations.PopCount(bits);
        var raw = (value & bits) >> shift;
        if (width >= 8) return (byte)(raw >> (width - 8));
        var full = (1 << width) - 1;
        return full == 0 ? (byte)0 : (byte)(raw * 255 / full);
    }

    private static byte Blend(byte destination, byte source, byte alpha)
        => (byte)((source * alpha + destination * (255 - alpha) + 127) / 255);
}
