namespace URemote.Core;

/// <summary>Owned opaque BGRA pixels. Disposal clears the image; contents never belong in diagnostics.</summary>
public sealed class PrivacyScreenImage : IDisposable
{
    private readonly byte[] pixels;
    private bool disposed;
    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> Pixels { get { ObjectDisposedException.ThrowIf(disposed, this); return pixels; } }
    // Takes ownership of bgra. Composite transparency over black so the local desktop
    // cannot show through a transparent PNG selected as a privacy image.
    public PrivacyScreenImage(int width, int height, byte[] bgra)
    {
        if (width is < 1 or > 8192 || height is < 1 or > 8192 || (long)width * height > 16_777_216
            || bgra.Length != checked(width * height * 4)) throw new ArgumentException("Invalid privacy image dimensions.");
        Width = width; Height = height; pixels = bgra;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha == 255) continue;
            for (var channel = 0; channel < 3; channel++) pixels[i + channel] = (byte)((pixels[i + channel] * alpha + 127) / 255);
            pixels[i + 3] = 255;
        }
    }
    public override string ToString() => $"PrivacyScreenImage({Width}x{Height}, contents omitted)";
    public void Dispose() { if (disposed) return; disposed = true; Array.Clear(pixels); }
}
