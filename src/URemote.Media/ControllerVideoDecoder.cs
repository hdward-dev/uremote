using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
namespace URemote.Media;
public sealed record ControllerFrame(int Width, int Height, byte[] Pixels);

// FFmpeg decodes H264 to framed RGBA PAM images; remote frames never touch disk.
public sealed class ControllerVideoDecoder : IAsyncDisposable
{
    private readonly System.Net.Sockets.TcpListener pixelsListener = new(System.Net.IPAddress.Loopback, 0);
    private readonly Process process;
    private readonly CancellationTokenSource stop = new();
    // Network packets arrive in bursts and vary in size. Bound memory by queuedBytes
    // rather than packet count; dropping arbitrary H264 chunks corrupts reference frames.
    private readonly Channel<byte[]> encoded = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Task worker;
    private readonly StringBuilder stderr = new();
    private long queuedBytes;
    public event Action<ControllerFrame>? Frame;
    public event Action<string?>? Failed;
    public ControllerVideoDecoder(string executable)
    {
        pixelsListener.Server.ReceiveBufferSize = 4 * 1024 * 1024;
        pixelsListener.Start(1);
        var pixelEndpoint = (System.Net.IPEndPoint)pixelsListener.LocalEndpoint;
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (OperatingSystem.IsWindows())
            foreach (var arg in new[] { "-hwaccel", "d3d11va" }) info.ArgumentList.Add(arg);
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-avioflags", "direct", "-fpsprobesize", "0", "-probesize", "32", "-analyzeduration", "0", "-flags", "low_delay", "-threads", "2", "-thread_type", "slice", "-filter_threads", "2", "-f", "h264", "-i", "pipe:0", "-an", "-pix_fmt", "rgba", "-fps_mode", "passthrough", "-c:v", "pam", "-threads", "4", "-flush_packets", "1", "-f", "image2pipe", $"tcp://127.0.0.1:{pixelEndpoint.Port}?tcp_nodelay=1" }) info.ArgumentList.Add(arg);
        if (Environment.GetEnvironmentVariable("UREMOTE_DECODER_DIAGNOSTICS") == "1")
        {
            var level = info.ArgumentList.IndexOf("error");
            info.ArgumentList[level] = "verbose";
        }
        try { process = Process.Start(info) ?? throw new IOException("Video decoder could not start."); }
        catch { pixelsListener.Stop(); throw; }
        URemote.Core.HiddenConsole.Hide(process);
        worker = RunAsync();
    }
    public void Push(byte[] bytes)
    {
        if (stop.IsCancellationRequested) return;
        if (bytes.Length > 16_000_000) { Failed?.Invoke("Video frame exceeds limit."); stop.Cancel(); return; }
        var queued = Interlocked.Add(ref queuedBytes, bytes.Length);
        if (queued > 32_000_000 || !encoded.Writer.TryWrite(bytes.ToArray())) { Interlocked.Add(ref queuedBytes, -bytes.Length); Failed?.Invoke("Video decoder queue overflow."); stop.Cancel(); }
    }
    private async Task RunAsync()
    {
        try
        {
            var tasks = new[] { WriteAsync(), ReadAsync(), DrainErrorsAsync() };
            var completed = await Task.WhenAny(tasks);
            if (!stop.IsCancellationRequested)
            {
                string detail;
                lock (stderr) detail = stderr.ToString().Trim();
                Failed?.Invoke(detail.Length == 0 ? null : detail.Length > 400 ? detail[..400] : detail);
            }
            stop.Cancel();
            if (!process.HasExited) process.Kill();
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!stop.IsCancellationRequested) Failed?.Invoke(ex.Message); }
        finally { stop.Cancel(); pixelsListener.Stop(); try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }
    }
    private async Task DrainErrorsAsync()
    {
        var buffer = new char[2048];
        int read;
        while ((read = await process.StandardError.ReadAsync(buffer.AsMemory(), stop.Token)) > 0)
        {
            lock (stderr) { if (stderr.Length < 2000) stderr.Append(buffer, 0, read); }
            if (Environment.GetEnvironmentVariable("UREMOTE_DECODER_DIAGNOSTICS") == "1") Console.Write(new string(buffer, 0, read));
        }
    }
    private async Task WriteAsync()
    {
        await foreach (var bytes in encoded.Reader.ReadAllAsync(stop.Token))
        {
            try { await process.StandardInput.BaseStream.WriteAsync(bytes, stop.Token); }
            finally { Interlocked.Add(ref queuedBytes, -bytes.Length); }
        }
    }
    private async Task ReadAsync()
    {
        using var client = await pixelsListener.AcceptTcpClientAsync(stop.Token);
        pixelsListener.Stop();
        client.NoDelay = true;
        using var stream = new BufferedStream(client.GetStream(), 256 * 1024);
        var single = new byte[1];
        while (!stop.IsCancellationRequested)
        {
            var header = new StringBuilder();
            while (!header.ToString().EndsWith("ENDHDR\n", StringComparison.Ordinal))
            {
                await stream.ReadExactlyAsync(single, stop.Token); header.Append((char)single[0]);
                if (header.Length > 512) throw new InvalidDataException("Invalid video frame header.");
            }
            var lines = header.ToString().Split('\n');
            if (lines[0] != "P7" || !lines.Contains("DEPTH 4") || !lines.Contains("MAXVAL 255")) throw new InvalidDataException("Unsupported decoded image.");
            int Size(string key) => int.Parse(lines.Single(x => x.StartsWith(key + " "))[(key.Length + 1)..]);
            var width = Size("WIDTH"); var height = Size("HEIGHT");
            if (width is < 1 or > 4096 || height is < 1 or > 8192 || (long)width * height > 16_777_216) throw new InvalidDataException("Frame dimensions exceed limit.");
            var pixels = GC.AllocateUninitializedArray<byte>(checked(width * height * 4)); await stream.ReadExactlyAsync(pixels, stop.Token);
            Frame?.Invoke(new(width, height, pixels));
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); pixelsListener.Stop(); encoded.Writer.TryComplete();
        try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
        await worker; process.Dispose(); stop.Dispose();
    }
}
