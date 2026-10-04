using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using URemote.Core;

namespace URemote.Linux;

// Keep D-Bus and PipeWire descriptors in one subprocess. stdin/stdout are private
// pipes; neither frames nor input are persisted or included in logs.
internal sealed class PortalDesktopSession : IAsyncDisposable
{
    private readonly Process helper;
    private readonly SemaphoreSlim gate = new(1);
    private bool disposed;
    public IReadOnlyList<WaylandGlobal> Outputs { get; private set; } = [];
    private PortalDesktopSession(Process helper) => this.helper = helper;

    private static PortalDesktopSession Start(string mode)
    {
        using var resource = typeof(PortalDesktopSession).Assembly.GetManifestResourceStream("URemote.Linux.desktop_portal.py")
            ?? throw new InvalidOperationException("缺少桌面 Portal 组件。");
        using var reader = new StreamReader(resource);
        var info = new ProcessStartInfo("python3") { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add("-u"); info.ArgumentList.Add("-c"); info.ArgumentList.Add(reader.ReadToEnd());
        if (mode.Length > 0) info.ArgumentList.Add(mode);
        Process process;
        try { process = Process.Start(info) ?? throw new InvalidOperationException("无法启动桌面 Portal 组件。"); }
        catch (System.ComponentModel.Win32Exception e) { throw new NotSupportedException("Portal 后端需要 Python 3、PyGObject 和 GStreamer PipeWire 插件。", e); }
        // Drain diagnostics without recording possibly sensitive third-party output.
        _ = process.StandardError.ReadToEndAsync();
        return new(process);
    }

    public static async Task ProbeAsync(CancellationToken ct)
    {
        await using var probe = Start("--probe");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await probe.ReadMetadataAsync(timeout.Token);
    }

    public static async Task<PortalDesktopSession> OpenAsync(bool enableInput, CancellationToken ct)
    {
        var session = Start(enableInput ? "--input" : "");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(130));
            var metadata = await session.ReadMetadataAsync(timeout.Token);
            var ids = metadata.GetProperty("outputs").EnumerateArray().Select(v => v.GetUInt32()).ToArray();
            if (ids.Length is < 1 or > 5 || ids.Distinct().Count() != ids.Length) throw new FormatException("无效的 Portal 显示器列表。");
            session.Outputs = ids.Select(id => new WaylandGlobal(id, "wl_output", 1)).ToArray();
            return session;
        }
        catch { await session.DisposeAsync(); throw; }
    }

    private async Task<JsonElement> ReadMetadataAsync(CancellationToken ct)
    {
        var header = new byte[4];
        try { await helper.StandardOutput.BaseStream.ReadExactlyAsync(header, ct); }
        catch (EndOfStreamException e)
        {
            if (Outputs.Count > 0) throw new DesktopSessionClosedException("桌面共享已结束，请重新开启被控并授权。");
            throw new NotSupportedException("桌面 Portal 组件已退出，请检查 Python 3、PyGObject、GStreamer 和桌面 Portal 服务。", e);
        }
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (size is 0 or > 65536) throw new FormatException("无效的 Portal 响应。");
        var bytes = new byte[size];
        await helper.StandardOutput.BaseStream.ReadExactlyAsync(bytes, ct);
        using var document = JsonDocument.Parse(bytes);
        var result = document.RootElement.Clone();
        if (result.TryGetProperty("closed", out var closed) && closed.GetBoolean())
            throw new DesktopSessionClosedException("桌面共享授权已结束，请重新开启被控并授权。");
        if (result.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
        return result;
    }

    private async Task<(JsonElement Metadata, byte[] Pixels)> ExchangeAsync(object command, CancellationToken ct)
    {
        // Cancellation may belong to one encoder generation or one controller,
        // while the portal belongs to the entire host session. Once a command
        // is sent, drain its complete response before releasing the shared pipe.
        // Only a genuine transport timeout can invalidate the bridge.
        await gate.WaitAsync(ct);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await helper.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), timeout.Token);
            await helper.StandardInput.FlushAsync(timeout.Token);
            var metadata = await ReadMetadataAsync(timeout.Token);
            byte[] pixels = [];
            if (metadata.TryGetProperty("bytes", out var length))
            {
                var width = metadata.GetProperty("width").GetUInt32();
                var height = metadata.GetProperty("height").GetUInt32();
                var stride = metadata.GetProperty("stride").GetUInt32();
                var size = WaylandScreenCapture.ValidateBuffer(width, height, stride);
                if (stride < (ulong)width * 4 || length.GetInt32() != size) throw new FormatException("无效的 Portal 视频缓冲区。");
                pixels = new byte[size];
                try { await helper.StandardOutput.BaseStream.ReadExactlyAsync(pixels, timeout.Token); }
                catch { Array.Clear(pixels); throw; }
            }
            return (metadata, pixels);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or FormatException or JsonException)
        {
            // A partially read frame cannot be reused as a new response.
            disposed = true;
            if (!helper.HasExited) helper.Kill(entireProcessTree: true);
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task<CapturedScreen> CaptureAsync(uint output, CancellationToken ct)
    {
        if (!Outputs.Any(o => o.Name == output)) throw new ArgumentException("所选显示器不可用。");
        var (metadata, pixels) = await ExchangeAsync(new { op = "capture", output }, ct);
        return new(output, metadata.GetProperty("width").GetUInt32(), metadata.GetProperty("height").GetUInt32(),
            metadata.GetProperty("stride").GetUInt32(), 1, false, pixels);
    }

    public IDesktopPointer CreatePointer(uint output)
    {
        if (!Outputs.Any(o => o.Name == output)) throw new ArgumentException("所选显示器不可用。");
        return new Pointer(this, output);
    }
    public async Task TypeTextAsync(string text, CancellationToken ct)
    {
        HostControlInput.ValidateText(text);
        await ExchangeAsync(new { op = "text", text }, ct);
    }
    public IDesktopKeyboard CreateKeyboard() => new Keyboard(this);

    public async Task CheckAliveAsync(CancellationToken ct)
    {
        if (disposed || helper.HasExited) throw new DesktopSessionClosedException("桌面共享已结束，请重新开启被控并授权。");
        await ExchangeAsync(new { op = "health" }, ct);
    }

    private async Task ReleaseAsync()
    {
        if (disposed) return;
        try { await ExchangeAsync(new { op = "release" }, CancellationToken.None); }
        catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException or DesktopSessionClosedException) { }
    }

    private sealed class Pointer(PortalDesktopSession owner, uint output) : IDesktopPointer
    {
        private readonly HashSet<uint> held = [];
        public async Task ApplyAsync(HostMouseMessage message, PointerCoordinates coordinates, uint width, uint height, CancellationToken ct = default)
        {
            if (message.ScreenId is not null) throw new ArgumentException("请先解析显示器编号。");
            switch (message.Action)
            {
                case MouseAction.MoveAbsolute:
                    // Reuse native validation; Portal uses each stream's logical dimensions.
                    WaylandVirtualPointer.AbsolutePayload(0, message.X, message.Y, coordinates, width, height);
                    var x = coordinates == PointerCoordinates.MacNormalized ? message.X : message.X / width;
                    var y = coordinates == PointerCoordinates.MacNormalized ? message.Y : message.Y / height;
                    await owner.ExchangeAsync(new { op = "move", output, x, y }, ct);
                    break;
                case MouseAction.Scroll:
                    WaylandVirtualPointer.AxisPayload(0, 0, message.Y);
                    WaylandVirtualPointer.AxisPayload(0, 1, message.X);
                    await owner.ExchangeAsync(new { op = "scroll", x = message.X, y = message.Y }, ct);
                    break;
                case MouseAction.Press:
                case MouseAction.Release:
                case MouseAction.Click:
                    var code = WaylandVirtualPointer.LinuxButton(message.Button);
                    if (message.Action == MouseAction.Click && held.Contains(code)) return;
                    if (message.Action != MouseAction.Release)
                    { await owner.ExchangeAsync(new { op = "button", code, down = true }, ct); held.Add(code); }
                    if (message.Action != MouseAction.Press)
                    { await owner.ExchangeAsync(new { op = "button", code, down = false }, ct); held.Remove(code); }
                    break;
                default: throw new ArgumentException("不支持的鼠标操作。");
            }
        }
        public async ValueTask DisposeAsync() { await owner.ReleaseAsync(); held.Clear(); }
    }

    private sealed class Keyboard(PortalDesktopSession owner) : IDesktopKeyboard
    {
        private readonly HashSet<uint> held = [];
        public async Task ApplyAsync(HostKeyMessage message, CancellationToken ct = default)
        {
            if (WaylandVirtualKeyboard.LinuxKey(message.MacKey) is not { } code) return;
            if (message.Action == KeyAction.Click && held.Contains(code)) return;
            if (message.Action != KeyAction.Release && !held.Contains(code))
            { await owner.ExchangeAsync(new { op = "key", code, down = true }, ct); held.Add(code); }
            if (message.Action != KeyAction.Press && held.Contains(code))
            { await owner.ExchangeAsync(new { op = "key", code, down = false }, ct); held.Remove(code); }
        }
        public async ValueTask DisposeAsync() { await owner.ReleaseAsync(); held.Clear(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            disposed = true;
            helper.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await helper.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!helper.HasExited) helper.Kill(entireProcessTree: true); await helper.WaitForExitAsync(); }
            helper.Dispose();
        }
        finally { gate.Release(); }
    }
}
