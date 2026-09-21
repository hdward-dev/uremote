using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace URemote.Media;

/// <summary>One persistent, backpressured encoder per output. No frame files.</summary>
public sealed class StreamingH264Encoder : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> errors;
    private readonly int width, height;
    public StreamingH264Encoder(string executable, int width, int height, bool inverted, uint format, int fps = 30, int outputWidth = 1280, int outputHeight = 720, int bitrate = 4000000, int crf = 18)
    {
        if (crf is < 0 or > 51 || outputWidth is < 2 or > 7680 || outputHeight is < 2 or > 4320 || bitrate is < 1000000 or > 40000000 || width is < 2 or > 16384 || height is < 2 or > 16384 || format > 1 || fps is < 1 or > 60)
            throw new ArgumentException("Unsupported stream format.");
        this.width = width; this.height = height;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        string[] args = ["-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pixel_format", format == 1 ? "bgr0" : "bgra",
            "-video_size", $"{width}x{height}", "-framerate", fps.ToString(), "-i", "pipe:0", "-an",
            "-vf", (inverted ? "vflip," : "") + $"scale={outputWidth}:{outputHeight}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=bilinear,pad={outputWidth}:{outputHeight}:(ow-iw)/2:(oh-ih)/2",
            "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-profile:v", "baseline", "-level:v", "5.1",
            "-pix_fmt", "yuv420p", "-crf", crf.ToString(), "-maxrate", bitrate.ToString(), "-bufsize", (bitrate / 2).ToString(),
            "-x264-params", $"aud=1:repeat-headers=1:keyint={fps}:min-keyint={fps}:scenecut=0", "-threads", "2",
            "-flush_packets", "1", "-f", "h264", "pipe:1"];
        foreach (var arg in args) start.ArgumentList.Add(arg);
        process = Process.Start(start) ?? throw new IOException("Cannot start video encoder.");
        errors = process.StandardError.ReadToEndAsync();
    }
    public async Task WriteAsync(byte[] pixels, int stride, CancellationToken ct)
    {
        if (stride < width * 4 || (long)stride * height != pixels.Length) throw new ArgumentException("Frame size changed.");
        if (stride == width * 4) await process.StandardInput.BaseStream.WriteAsync(pixels, ct);
        else for (var row = 0; row < height; row++)
            await process.StandardInput.BaseStream.WriteAsync(pixels.AsMemory(row * stride, width * 4), ct);
    }
    public void CompleteInput() => process.StandardInput.Close();
    public async IAsyncEnumerable<byte[]> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var parser = new AnnexBAccessUnits();
        var buffer = new byte[65536];
        try
        {
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, ct)) != 0)
                foreach (var unit in parser.Push(buffer.AsSpan(0, count))) yield return unit;
            if (parser.Finish() is { } tail) yield return tail;
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw new IOException("Streaming encoder failed.");
        }
        finally { Array.Clear(buffer); parser.Clear(); }
    }
    public async ValueTask DisposeAsync()
    {
        try { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await errors; }
        finally { process.Dispose(); }
    }
}

// AUD marks access-unit boundaries; accepts start codes split across arbitrary pipe reads.
public sealed class AnnexBAccessUnits
{
    private readonly List<byte> pending = [];
    private int scanned;
    private bool sawAud;
    public List<byte[]> Push(ReadOnlySpan<byte> bytes)
    {
        if (pending.Count + bytes.Length > 8_000_000) throw new InvalidDataException("H264 access unit exceeds limit.");
        foreach (var b in bytes) pending.Add(b);
        var result = new List<byte[]>();
        while (scanned + 4 < pending.Count)
        {
            int prefix = pending[scanned] == 0 && pending[scanned + 1] == 0
                ? pending[scanned + 2] == 1 ? 3 : pending[scanned + 2] == 0 && pending[scanned + 3] == 1 ? 4 : 0 : 0;
            if (prefix > 0 && (pending[scanned + prefix] & 31) == 9)
            {
                if (sawAud && scanned > 0)
                { result.Add(pending.GetRange(0, scanned).ToArray()); pending.RemoveRange(0, scanned); scanned = 0; }
                sawAud = true; scanned += prefix;
            }
            else scanned++;
        }
        return result;
    }
    public byte[]? Finish() { var result = sawAud && pending.Count > 0 ? pending.ToArray() : null; Clear(); return result; }
    public void Clear() { for (int i = 0; i < pending.Count; i++) pending[i] = 0; pending.Clear(); scanned = 0; sawAud = false; }
}
