using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Text;

namespace URemote.Media;

/// <summary>One persistent, backpressured encoder per output. No frame files.</summary>
public sealed class StreamingH264Encoder : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> errors;
    private readonly int width, height;
    private readonly string executable;
    private readonly SemaphoreSlim? inputSlots;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentQueue<long> inputTimes = new();
    private readonly Channel<int> packetSizes = Channel.CreateBounded<int>(2);
    private double frameDelayMilliseconds;
    public double FrameDelayMilliseconds => Volatile.Read(ref frameDelayMilliseconds);
    public int MaxInFlightFrames { get; }
    public int InputPipeBytes { get; }
    public string Codec { get; }
    public bool Hardware => Codec != "libx264";
    public int ChromaFormat { get; }
    // Authorized official codec-name tables: Nvenc=0, VideoToolbox=3;
    // -1 is the generic software implementation. Report only a probed backend.
    public static async Task<int> ImplementationAsync(string executable, CancellationToken ct = default)
        => OperatingSystem.IsMacOS() ? 3 : OperatingSystem.IsLinux()
            && await VideoEncoderSelection.NvencAsync(executable, ct).ConfigureAwait(false) ? 0 : -1;
    public static async Task<bool> SupportsTrueColorAsync(string executable, CancellationToken ct = default)
        => OperatingSystem.IsLinux() && await ImplementationAsync(executable, ct).ConfigureAwait(false) == 0
            && await VideoEncoderSelection.NvencAsync(executable, ct, 3).ConfigureAwait(false);
    public static async Task<StreamingH264Encoder> CreateAsync(string executable, int width, int height, bool inverted, uint format,
        int fps = 30, int outputWidth = 1280, int outputHeight = 720, int bitrate = 4000000, int crf = 18, CancellationToken stopToken = default, int chromaFormat = 1)
    {
        var useNvenc = await ImplementationAsync(executable, stopToken).ConfigureAwait(false) == 0;
        if (chromaFormat == 3 && (!useNvenc || !await SupportsTrueColorAsync(executable, stopToken).ConfigureAwait(false)))
            throw new VideoEncoderException("h264_nvenc", chromaFormat: 3);
        stopToken.ThrowIfCancellationRequested();
        return new(executable, width, height, inverted, format, fps, outputWidth, outputHeight, bitrate, crf, useNvenc, chromaFormat);
    }
    public StreamingH264Encoder(string executable, int width, int height, bool inverted, uint format, int fps = 30, int outputWidth = 1280, int outputHeight = 720, int bitrate = 4000000, int crf = 18, bool useNvenc = false, int chromaFormat = 1)
    {
        if (chromaFormat is not (1 or 3) || (chromaFormat == 3 && !useNvenc)
            || crf is < 0 or > 51 || outputWidth is < 2 or > 7680 || outputHeight is < 2 or > 4320 || bitrate is < 1000000 or > 40000000 || width is < 2 or > 16384 || height is < 2 or > 16384 || format > 1 || fps is < 1 or > URemote.Core.HostDisplayInfo.MaxSupportedFps)
            throw new ArgumentException("Unsupported stream format.");
        this.width = width; this.height = height; this.executable = executable;
        // Packet lengths arrive before their payload on stderr. Bound the number
        // of in-flight frames so input copying can overlap encoding and sending,
        // without the old unbounded FFmpeg queue or next-AUD wait.
        MaxInFlightFrames = useNvenc ? chromaFormat == 3 && fps >= 60 ? 4 : Math.Clamp((fps + 19) / 20, 2, 4) : 0;
        inputSlots = useNvenc ? new SemaphoreSlim(MaxInFlightFrames, MaxInFlightFrames) : null;
        ChromaFormat = chromaFormat;
        Codec = useNvenc ? "h264_nvenc" : OperatingSystem.IsMacOS() ? "h264_videotoolbox" : "libx264";
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden };
        // Frequent IDRs plus a 125 ms VBV caused periodic text-quality collapse.
        // Keep 500 ms of encoder rate-control budget (not a playback queue),
        // without periodic IDRs; each peer starts with an IDR and authenticated PLI/FIR
        // requests a refresh when needed. No inter-frame lookahead or B-frame queue.
        string[] args = ["-hide_banner", "-loglevel", "error", "-probesize", "32", "-analyzeduration", "0", "-fpsprobesize", "0",
            "-thread_queue_size", "1", "-f", "rawvideo", "-pixel_format", format == 1 ? "bgr0" : "bgra",
            "-video_size", $"{width}x{height}", "-framerate", fps.ToString(), "-i", "pipe:0", "-an",
            "-vf", (inverted ? "vflip," : "") + $"scale={outputWidth}:{outputHeight}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=bilinear,pad={outputWidth}:{outputHeight}:(ow-iw)/2:(oh-ih)/2",
            "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-profile:v", "baseline", "-level:v", "5.2",
            "-pix_fmt", "yuv420p", "-crf", crf.ToString(), "-maxrate", bitrate.ToString(), "-bufsize", (bitrate / 2).ToString(),
            "-x264-params", "aud=1:repeat-headers=1:keyint=infinite:scenecut=0", "-threads", Math.Clamp(Environment.ProcessorCount / 2, 2, 8).ToString(),
            "-flush_packets", "1", "-f", "h264", "pipe:1"];
        if (useNvenc)
        {
            var codec = Array.IndexOf(args, "-c:v"); var tail = Array.IndexOf(args, "-flush_packets");
            var input = args[..codec].ToList();
            var directRgb = width == outputWidth && height == outputHeight;
            if (directRgb)
            {
                // NVENC accepts packed RGB and converts it on the GPU. Avoid a full
                // 4K BGRA->YUV420 conversion on the CPU when no resize is needed.
                var filter = input.IndexOf("-vf");
                input.RemoveRange(filter, 2);
                if (inverted) input.AddRange(["-vf", "vflip"]);
            }
            args = [..input, "-c:v", "h264_nvenc", "-preset", "p1", "-tune", "ull", "-bf", "0", "-rc-lookahead", "0",
                "-zerolatency", "1", "-delay", "0", "-profile:v", chromaFormat == 3 ? "high444p" : "baseline", "-level:v", "5.2",
                "-pix_fmt", directRgb ? format == 1 ? "bgr0" : "bgra" : chromaFormat == 3 ? "yuv444p" : "yuv420p", "-rgb_mode", chromaFormat == 3 ? "yuv444" : "yuv420",
                "-rc", "vbr", "-cq", crf.ToString(), "-b:v", bitrate.ToString(), "-maxrate", bitrate.ToString(),
                "-bufsize", (bitrate / 2).ToString(), "-g", "999999", "-aud", "1", ..args[tail..]];
        }
        else if (OperatingSystem.IsMacOS())
        {
            var codec = Array.IndexOf(args, "-c:v"); var tail = Array.IndexOf(args, "-flush_packets");
            args = [..args[..codec], "-c:v", "h264_videotoolbox", "-realtime", "1", "-allow_sw", "1", "-bf", "0",
                "-profile:v", "baseline", "-level:v", "5.2", "-pix_fmt", "yuv420p", "-b:v", bitrate.ToString(),
                "-g", "600", "-bsf:v", "h264_metadata=aud=insert", ..args[tail..]];
        }
        if (useNvenc)
        {
            // tee writes each AVPacket to its outputs in order. Flush the size
            // FIRST, then the exact Annex-B bytes, so neither pipe can deadlock
            // waiting for the other. This also emits a lone frame immediately.
            args = [..args[..^3], "-map", "0:v:0", "-f", "tee",
                "[f=framecrc:flush_packets=1]pipe:2|[f=h264:flush_packets=1]pipe:1"];
        }
        foreach (var arg in args) start.ArgumentList.Add(arg);
        process = Process.Start(start) ?? throw new IOException("Cannot start video encoder.");
        InputPipeBytes = EncoderPipeBuffer.Grow(process.StandardInput.BaseStream, 1048576);
        _ = EncoderPipeBuffer.Grow(process.StandardOutput.BaseStream, 262144);
        URemote.Core.HiddenConsole.Hide(process);
        errors = DrainErrorsAsync();
    }
    // The host waits BEFORE taking a frame from its latest-frame mailbox. A
    // producer must not hold an old captured desktop while waiting for a slot.
    public async Task WaitForCapacityAsync(CancellationToken ct)
    {
        if (inputSlots is null) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        await inputSlots.WaitAsync(stop.Token).ConfigureAwait(false);
        inputSlots.Release();
    }
    public async Task WriteAsync(byte[] pixels, int stride, CancellationToken ct)
    {
        if (stride < width * 4 || (long)stride * height != pixels.Length) throw new ArgumentException("Frame size changed.");
        try
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            if (inputSlots is not null) await inputSlots.WaitAsync(stop.Token).ConfigureAwait(false);
            inputTimes.Enqueue(Stopwatch.GetTimestamp());
            if (stride == width * 4) await process.StandardInput.BaseStream.WriteAsync(pixels, stop.Token);
            else for (var row = 0; row < height; row++)
                await process.StandardInput.BaseStream.WriteAsync(pixels.AsMemory(row * stride, width * 4), stop.Token);
        }
        catch (IOException e) { throw Failure(e); }
    }
    public void CompleteInput() => process.StandardInput.Close();
    public async IAsyncEnumerable<byte[]> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (inputSlots is not null)
        {
            while (await ReadPacketAsync(ct).ConfigureAwait(false) is { } packet)
            {
                NoteFrameDelay();
                try { yield return packet; }
                finally { inputSlots.Release(); }
            }
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw Failure();
            yield break;
        }
        var parser = new AnnexBAccessUnits();
        var buffer = new byte[65536];
        try
        {
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, ct)) != 0)
                foreach (var unit in parser.Push(buffer.AsSpan(0, count)))
                {
                    NoteFrameDelay();
                    try { yield return unit; }
                    finally { inputSlots?.Release(); }
                }
            if (parser.Finish() is { } tail)
            {
                NoteFrameDelay();
                try { yield return tail; }
                finally { inputSlots?.Release(); }
            }
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw Failure();
        }
        finally { Array.Clear(buffer); parser.Clear(); }
    }
    private async Task<string> DrainErrorsAsync()
    {
        var diagnostic = new StringBuilder();
        Exception? failure = null;
        try
        {
            while (await process.StandardError.ReadLineAsync(lifetime.Token).ConfigureAwait(false) is { } line)
            {
                if (inputSlots is not null && FfmpegPacketLength.TryRead(line, out var size))
                    await packetSizes.Writer.WriteAsync(size, lifetime.Token).ConfigureAwait(false);
                else if (!line.StartsWith('#') && diagnostic.Length < 8192)
                    diagnostic.AppendLine(line[..Math.Min(line.Length, 8192 - diagnostic.Length)]);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or FormatException or ChannelClosedException) { failure = e; }
        finally { packetSizes.Writer.TryComplete(failure); }
        return diagnostic.ToString();
    }
    private async Task<byte[]?> ReadPacketAsync(CancellationToken ct)
    {
        try
        {
            if (!await packetSizes.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) return null;
            var size = await packetSizes.Reader.ReadAsync(ct).ConfigureAwait(false);
            var packet = GC.AllocateUninitializedArray<byte>(size);
            try { await process.StandardOutput.BaseStream.ReadExactlyAsync(packet, ct).ConfigureAwait(false); return packet; }
            catch { Array.Clear(packet); throw; }
        }
        catch (Exception e) when (e is IOException or FormatException or ChannelClosedException) { throw Failure(e); }
    }
    private void NoteFrameDelay()
    {
        if (inputTimes.TryDequeue(out var started))
            Volatile.Write(ref frameDelayMilliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
    private VideoEncoderException Failure(Exception? inner = null)
    {
        if (Codec == "h264_nvenc") VideoEncoderSelection.DisableNvenc(executable, ChromaFormat);
        return new(Codec, inner, ChromaFormat);
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        try { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await errors; }
        finally { process.Dispose(); lifetime.Dispose(); inputSlots?.Dispose(); inputTimes.Clear(); }
    }
}

// FFmpeg framecrc records: stream, DTS, PTS, duration, packet size, checksum,
// followed by optional side data. Only lengths are needed; never log payloads.
internal static class FfmpegPacketLength
{
    internal static bool TryRead(string line, out int size)
    {
        size = 0;
        if (!line.AsSpan().TrimStart().StartsWith("0,")) return false;
        var fields = line.Split(',');
        if (fields.Length < 6 || !int.TryParse(fields[4].AsSpan().Trim(), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out size) || size is < 1 or > 8_000_000)
            throw new FormatException("Invalid encoded packet length.");
        return true;
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
