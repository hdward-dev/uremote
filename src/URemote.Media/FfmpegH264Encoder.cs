using System.Diagnostics;

namespace URemote.Media;

/// <summary>Bounded one-frame CPU encoder for the initial interoperability prototype.</summary>
public sealed class FfmpegH264Encoder(string executable)
{
    public async Task<byte[]> EncodeAsync(byte[] pixels, int width, int height, int stride,
        bool yInverted, uint shmFormat, CancellationToken ct = default)
    {
        if (width is < 2 or > 16384 || height is < 2 or > 16384 || stride < width * 4
            || (long)stride * height != pixels.Length || shmFormat is not (0 or 1))
            throw new ArgumentException("Unsupported 32-bit screen buffer.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        // No shell. No frame files. H264 profile matches the observed official offer's constrained baseline.
        string[] args = ["-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pixel_format", shmFormat == 1 ? "bgr0" : "bgra",
            "-video_size", $"{width}x{height}", "-framerate", "2", "-i", "pipe:0", "-frames:v", "1", "-an",
            "-vf", (yInverted ? "vflip," : "") + "scale=1280:720:force_original_aspect_ratio=decrease:force_divisible_by=2,pad=1280:720:(ow-iw)/2:(oh-ih)/2",
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-profile:v", "baseline", "-level:v", "3.1",
            "-pix_fmt", "yuv420p", "-x264-params", "aud=1:repeat-headers=1:keyint=1", "-threads", "2", "-f", "h264", "pipe:1"];
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Unable to start H264 encoder.");
        using var cancel = deadline.Token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        try
        {
            var output = ReadOutputAsync(process.StandardOutput.BaseStream, deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                for (var row = 0; row < height; row++)
                    await process.StandardInput.BaseStream.WriteAsync(pixels.AsMemory(row * stride, width * 4), deadline.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(deadline.Token);
                var encoded = await output;
                await error;
                if (process.ExitCode != 0 || encoded.Length == 0) throw new IOException("H264 encoder failed; verify libx264 support.");
                return encoded;
            }
            catch
            {
                deadline.Cancel();
                try { await output; } catch (Exception e) when (e is IOException or OperationCanceledException) { }
                try { await error; } catch (OperationCanceledException) { }
                throw;
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    private static async Task<byte[]> ReadOutputAsync(Stream stream, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + count > 8_000_000) throw new IOException("Encoded frame exceeds limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
