using System.Diagnostics;
using System.Globalization;
using System.Text;
using URemote.Core;

namespace URemote.Media;

public static class PrivacyImageDecoder
{
    public static async Task<PrivacyScreenImage> DecodeAsync(string ffmpeg, byte[] image, CancellationToken ct)
    {
        if (image.Length is < 4 or > 16_777_216) throw new FormatException("Privacy image size exceeds limit.");
        var codec = image.AsSpan().StartsWith(new byte[] { 137,80,78,71,13,10,26,10 }) ? "png"
            : image.AsSpan().StartsWith(new byte[] { 255,216,255 }) ? "mjpeg"
            : image.Length >= 12 && image.AsSpan(0,4).SequenceEqual("RIFF"u8) && image.AsSpan(8,4).SequenceEqual("WEBP"u8) ? "webp"
            : throw new FormatException("Unsupported privacy image format.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        using var process = new Process { StartInfo = new(ffmpeg)
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-protocol_whitelist", "pipe", "-f", "image2pipe",
            // Official preset PNGs can be 11520x6480. Bound source decoding, then
            // resize once to a 4K-long-edge static buffer rather than retaining 12K RGBA.
            "-c:v", codec, "-max_pixels", "134217728", "-threads", "1", "-i", "pipe:0", "-frames:v", "1", "-an", "-sn", "-dn",
            "-vf", "scale=w='min(4096,iw)':h='min(4096,ih)':force_original_aspect_ratio=decrease:flags=bilinear", "-filter_threads", "1",
            "-map_metadata", "-1", "-pix_fmt", "rgba", "-c:v", "pam", "-threads", "1", "-f", "image2pipe", "pipe:1" }) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new IOException("Privacy image decoder did not start.");
        HiddenConsole.Hide(process);
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
        async Task WriteAsync()
        {
            try { await process.StandardInput.BaseStream.WriteAsync(image, token); }
            finally { process.StandardInput.Close(); }
        }
        var writer = WriteAsync(); byte[]? pixels = null;
        try
        {
            var output = process.StandardOutput.BaseStream; var header = new StringBuilder(); var single = new byte[1];
            while (!header.ToString().EndsWith("ENDHDR\n", StringComparison.Ordinal))
            {
                await output.ReadExactlyAsync(single, token); header.Append((char)single[0]);
                if (header.Length > 512) throw new FormatException("Invalid decoded privacy image header.");
            }
            var lines = header.ToString().Split('\n');
            if (lines[0] != "P7" || !lines.Contains("DEPTH 4") || !lines.Contains("MAXVAL 255")) throw new FormatException("Invalid decoded privacy image format.");
            int Size(string key) => int.Parse(lines.Single(line => line.StartsWith(key + " ", StringComparison.Ordinal))[(key.Length + 1)..], CultureInfo.InvariantCulture);
            var width = Size("WIDTH"); var height = Size("HEIGHT");
            if (width is < 1 or > 8192 || height is < 1 or > 8192 || (long)width * height > 16_777_216) throw new FormatException("Decoded privacy image exceeds limit.");
            pixels = new byte[checked(width * height * 4)]; await output.ReadExactlyAsync(pixels, token);
            await output.CopyToAsync(Stream.Null, token); await writer;
            await process.WaitForExitAsync(token); await errors;
            if (process.ExitCode != 0) throw new IOException("Privacy image decode failed.");
            for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            var result = new PrivacyScreenImage(width, height, pixels); pixels = null; return result;
        }
        finally
        {
            deadline.Cancel();
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(writer, errors); } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
            if (pixels is not null) Array.Clear(pixels);
        }
    }
}
