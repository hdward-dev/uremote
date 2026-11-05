using System.Buffers.Binary;
using System.Text;
using System.Collections.Concurrent;
using URemote.Core;

namespace URemote.Linux;

/// <summary>Local black layer-shell surfaces, excluded from KWin's independent screencast scene.
/// No DPMS, brightness, output layout, persistent window rules, or keyboard focus changes.</summary>
public sealed class KdePrivacyScreen : IPrivacyScreen
{
    private readonly WaylandConnection connection;
    private readonly CancellationTokenSource stop = new();
    private Task monitor = Task.CompletedTask;
    private readonly Action? lost;
    private readonly List<Surface> surfaces = [];
    private uint blackBuffer;
    private uint shm;
    private uint customBuffer;
    private int imageWidth = 1, imageHeight = 1;
    private readonly SemaphoreSlim writer = new(1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource> syncs = new();
    private bool disposed;
    private int active;
    public bool IsActive => Volatile.Read(ref active) != 0;
    private sealed record Surface(uint Wl, uint Layer, uint Viewport)
    { internal uint Width, Height; }
    private KdePrivacyScreen(WaylandConnection connection, Action? lost) { this.connection = connection; this.lost = lost; }

    public static async Task<bool> IsSupportedAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || DesktopEnvironmentInfo.Current.Name != "KDE Plasma"
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(4));
            using var connection = await WaylandConnection.OpenAsync(timeout.Token);
            if (!new[] { "zwlr_layer_shell_v1", "wp_viewporter", "wl_compositor", "wl_shm", "wl_output" }
                .All(name => connection.Globals.Any(g => g.Interface == name))) return false;
            return await KwinPrivacyScript.RunAsync("return workspace.windowList().some(function(w) { return typeof w.excludeFromCapture === 'boolean'; });", timeout.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or NotSupportedException or FormatException or DllNotFoundException or System.Net.Sockets.SocketException) { ct.ThrowIfCancellationRequested(); return false; }
    }

    public static Task<KdePrivacyScreen> OpenAsync(Action? lost = null, CancellationToken ct = default)
        => OpenCoreAsync(lost, ct, 0);
    internal static Task<KdePrivacyScreen> OpenForTestsAsync(int testSize, CancellationToken ct)
        => OpenCoreAsync(null, ct, testSize);
    private static async Task<KdePrivacyScreen> OpenCoreAsync(Action? lost, CancellationToken ct, int testSize)
    {
        if (!OperatingSystem.IsLinux() || DesktopEnvironmentInfo.Current.Name != "KDE Plasma") throw new NotSupportedException("防窥屏需要支持采集排除的 KDE Wayland。");
        if (testSize is < 0 or > 256) throw new ArgumentOutOfRangeException(nameof(testSize));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(4));
        var screen = new KdePrivacyScreen(await WaylandConnection.OpenAsync(timeout.Token), lost);
        try { await screen.InitializeAsync(testSize, timeout.Token); return screen; }
        catch { await screen.DisposeAsync(); throw; }
    }

    private async Task InitializeAsync(int testSize, CancellationToken ct)
    {
        async Task<uint> Bind(string name, uint version = 1) => await connection.BindAsync(connection.Globals.FirstOrDefault(g => g.Interface == name)
            ?? throw new NotSupportedException("缺少防窥屏 Wayland 接口。"), version, ct);
        var compositor = await Bind("wl_compositor"); var shell = await Bind("zwlr_layer_shell_v1");
        var viewporter = await Bind("wp_viewporter"); shm = await Bind("wl_shm");
        var outputs = connection.Globals.Where(g => g.Interface == "wl_output").ToArray();
        if (outputs.Length is < 1 or > 16) throw new NotSupportedException("显示器数量超出防窥屏支持范围。");
        // Two immutable 1x1 ARGB buffers, stretched to each output's logical geometry.
        // Map transparent first; never cover even a single pixel until exclusion is acknowledged.
        using var memory = UnixFileDescriptor.CreateMemoryFile(); RandomAccess.SetLength(memory, 8);
        RandomAccess.Write(memory, new byte[] { 0, 0, 0, 0, 0, 0, 0, 255 }, 0);
        var pool = connection.AllocateId();
        await connection.SendFileDescriptorAsync(shm, 0, WaylandConnection.Words(pool, 8), memory, ct);
        var transparent = connection.AllocateId(); blackBuffer = connection.AllocateId();
        await connection.SendAsync(pool, 0, WaylandConnection.Words(transparent, 0, 1, 1, 4, 0), ct);
        await connection.SendAsync(pool, 0, WaylandConnection.Words(blackBuffer, 4, 1, 1, 4, 0), ct);
        await connection.SendAsync(pool, 1, [], ct);
        await connection.RoundtripAsync(ct);
        foreach (var global in outputs)
        {
            var output = await connection.BindAsync(global, 1, ct);
            var surface = connection.AllocateId(); var viewport = connection.AllocateId();
            await connection.SendAsync(compositor, 0, WaylandConnection.Words(surface), ct);
            await connection.SendAsync(viewporter, 1, WaylandConnection.Words(viewport, surface), ct);
            var layer = connection.AllocateId();
            // 'dock' gives KWin's window API a stable type marker. Combined with our
            // process ID this selects these native surfaces, never other applications.
            await connection.SendAsync(shell, 0, Join(WaylandConnection.Words(layer, surface, output, 3), Text("dock")), ct);
            await connection.SendAsync(layer, 0, WaylandConnection.Words((uint)testSize, (uint)testSize), ct);
            await connection.SendAsync(layer, 1, WaylandConnection.Words(testSize == 0 ? 15u : 5u), ct);
            await connection.SendAsync(layer, 2, WaylandConnection.Words(uint.MaxValue), ct); // ignore panels/exclusive zones
            await connection.SendAsync(layer, 4, WaylandConnection.Words(0), ct); // no keyboard focus
            var region = connection.AllocateId();
            await connection.SendAsync(compositor, 1, WaylandConnection.Words(region), ct);
            await connection.SendAsync(surface, 5, WaylandConnection.Words(region), ct); // empty input region
            await connection.SendAsync(region, 0, [], ct);
            surfaces.Add(new(surface, layer, viewport));
            await connection.SendAsync(surface, 6, [], ct);
        }
        var configured = new HashSet<uint>();
        while (configured.Count < surfaces.Count)
        {
            var e = await connection.ReadEventAsync(ct);
            var surface = surfaces.FirstOrDefault(s => s.Layer == e.Target);
            if (surface is null) continue;
            if (e.Opcode == 1) throw new IOException("防窥屏被合成器关闭。");
            if (e.Opcode != 0) continue;
            await ConfigureAsync(surface, e.Data, ct);
            await AttachAsync(surface, transparent, ct);
            configured.Add(surface.Layer);
        }
        await connection.RoundtripAsync(ct);
        var script = "var windows = workspace.windowList().filter(function(w) { return Number(w.pid) === " + Environment.ProcessId + " && w.dock; });" +
            "if (windows.length !== " + surfaces.Count + ") return false;" +
            "windows.forEach(function(w) { w.excludeFromCapture = true; w.skipsCloseAnimation = true; });" +
            "return windows.every(function(w) { return w.excludeFromCapture === true; });";
        if (!await KwinPrivacyScript.RunAsync(script, ct)) throw new NotSupportedException("KWin 未确认遮罩采集排除，未开启防窥屏。");
        foreach (var surface in surfaces) await AttachAsync(surface, blackBuffer, ct);
        await connection.RoundtripAsync(ct);
        Volatile.Write(ref active, 1);
        monitor = MonitorAsync(stop.Token);
    }

    private async Task ConfigureAsync(Surface surface, byte[] data, CancellationToken ct)
    {
        if (data.Length != 12) throw new FormatException("Invalid privacy surface configuration.");
        var serial = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var width = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)); var height = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        if (width is 0 or > 32768 || height is 0 or > 32768) throw new IOException("Invalid privacy surface size.");
        surface.Width = width; surface.Height = height;
        await connection.SendAsync(surface.Layer, 6, WaylandConnection.Words(serial), ct);
        await connection.SendAsync(surface.Viewport, 2, WaylandConnection.Words(width, height), ct);
        await CropAsync(surface, ct);
    }
    internal static (uint X, uint Y, uint Width, uint Height) Crop(int imageWidth, int imageHeight, uint outputWidth, uint outputHeight)
    {
        if (imageWidth < 1 || imageHeight < 1 || outputWidth < 1 || outputHeight < 1) throw new ArgumentOutOfRangeException(nameof(imageWidth));
        var scale = Math.Max((double)outputWidth / imageWidth, (double)outputHeight / imageHeight);
        var width = Math.Max(1u, (uint)Math.Round(outputWidth / scale * 256));
        var height = Math.Max(1u, (uint)Math.Round(outputHeight / scale * 256));
        width = Math.Min((uint)imageWidth * 256, width); height = Math.Min((uint)imageHeight * 256, height);
        return (((uint)imageWidth * 256 - width) / 2, ((uint)imageHeight * 256 - height) / 2, width, height);
    }
    private Task CropAsync(Surface surface, CancellationToken ct)
    {
        var crop = Crop(imageWidth, imageHeight, surface.Width, surface.Height);
        return connection.SendAsync(surface.Viewport, 1, WaylandConnection.Words(crop.X, crop.Y, crop.Width, crop.Height), ct);
    }
    public async Task SetImageAsync(PrivacyScreenImage? image, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(4));
        var token = deadline.Token; uint callback = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await writer.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!IsActive) throw new IOException("Privacy screen is inactive.");
            var buffer = blackBuffer;
            if (image is not null)
            {
                using var memory = UnixFileDescriptor.CreateMemoryFile(); RandomAccess.SetLength(memory, image.Pixels.Length);
                RandomAccess.Write(memory, image.Pixels.Span, 0);
                var pool = connection.AllocateId();
                await connection.SendFileDescriptorAsync(shm, 0, WaylandConnection.Words(pool, (uint)image.Pixels.Length), memory, token);
                buffer = connection.AllocateId();
                await connection.SendAsync(pool, 0, WaylandConnection.Words(buffer, 0, (uint)image.Width, (uint)image.Height, (uint)image.Width * 4, 0), token);
                await connection.SendAsync(pool, 1, [], token);
            }
            imageWidth = image?.Width ?? 1; imageHeight = image?.Height ?? 1;
            foreach (var surface in surfaces) { await CropAsync(surface, token); await AttachAsync(surface, buffer, token); }
            if (customBuffer != 0) await connection.SendAsync(customBuffer, 0, [], token);
            customBuffer = image is null ? 0 : buffer;
            callback = connection.AllocateId(); syncs[callback] = completion;
            await connection.SendAsync(1, 0, WaylandConnection.Words(callback), token);
        }
        catch { if (callback != 0) syncs.TryRemove(callback, out _); throw; }
        finally { writer.Release(); }
        try { await completion.Task.WaitAsync(token); }
        finally { if (callback != 0) syncs.TryRemove(callback, out _); }
    }
    private async Task AttachAsync(Surface surface, uint buffer, CancellationToken ct)
    {
        await connection.SendAsync(surface.Wl, 1, WaylandConnection.Words(buffer, 0, 0), ct);
        await connection.SendAsync(surface.Wl, 2, WaylandConnection.Words(0, 0, int.MaxValue, int.MaxValue), ct);
        await connection.SendAsync(surface.Wl, 6, [], ct);
    }
    private async Task MonitorAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var e = await connection.ReadEventAsync(ct);
                if (e.Opcode == 0 && syncs.TryRemove(e.Target, out var completion)) { completion.TrySetResult(); continue; }
                // A changed output topology invalidates the all-displays guarantee.
                if (e.Target == 2 && (e.Opcode == 1 || e.Opcode == 0 && Encoding.UTF8.GetString(e.Data).Contains("wl_output")))
                    throw new IOException("显示器配置已变化，防窥屏已关闭。");
                var surface = surfaces.FirstOrDefault(s => s.Layer == e.Target);
                if (surface is null) continue;
                if (e.Opcode == 1) throw new IOException("防窥屏被合成器关闭。");
                if (e.Opcode != 0) continue;
                await writer.WaitAsync(ct);
                try { await ConfigureAsync(surface, e.Data, ct); await AttachAsync(surface, customBuffer != 0 ? customBuffer : blackBuffer, ct); }
                finally { writer.Release(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or FormatException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            Volatile.Write(ref active, 0);
            connection.Dispose();
            if (!ct.IsCancellationRequested) lost?.Invoke();
        }
    }
    private static byte[] Text(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text + '\0'); var result = new byte[4 + ((bytes.Length + 3) & ~3)];
        BinaryPrimitives.WriteInt32LittleEndian(result, bytes.Length); bytes.CopyTo(result, 4); return result;
    }
    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        Volatile.Write(ref active, 0);
        stop.Cancel(); connection.Dispose(); // Closing the socket removes every surface, even after a host crash.
        await monitor; stop.Dispose();
    }
}
