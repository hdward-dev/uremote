using URemote.Core;

namespace URemote.Linux;

public sealed class DesktopSessionClosedException(string message) : Exception(message);

public interface IDesktopPointer : IAsyncDisposable
{
    Task ApplyAsync(HostMouseMessage message, PointerCoordinates coordinates, uint width, uint height, CancellationToken ct = default);
}

public interface IDesktopKeyboard : IAsyncDisposable
{
    Task ApplyAsync(HostKeyMessage message, CancellationToken ct = default);
}

// Select by compositor capabilities rather than an allowlist of desktop names.
public static class DesktopBackend
{
    private static PortalDesktopSession? portal;
    private static readonly SemaphoreSlim lifecycle = new(1);
    public static bool HasNativeCapture(IEnumerable<WaylandGlobal> globals) => globals.Any(g => g.Interface == "zwlr_screencopy_manager_v1");
    public static bool HasNativeInput(IEnumerable<WaylandGlobal> globals) => globals.Any(g => g.Interface == "zwlr_virtual_pointer_manager_v1" && g.Version >= 2)
        && globals.Any(g => g.Interface == "zwp_virtual_keyboard_manager_v1");

    public static async Task<IReadOnlyList<WaylandGlobal>> DiscoverAsync(CancellationToken ct = default)
    {
        if (portal is { } active)
        {
            await active.CheckAliveAsync(ct);
            return active.Outputs;
        }
        var globals = await WaylandCapabilities.DiscoverAsync(ct);
        if (HasNativeCapture(globals)) return globals;
        await PortalDesktopSession.ProbeAsync(ct);
        return []; // The system chooser supplies authorized streams when hosting starts.
    }

    public static async Task<IAsyncDisposable> OpenAsync(bool enableInput, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            var globals = await WaylandCapabilities.DiscoverAsync(ct);
            if (HasNativeCapture(globals) && (!enableInput || HasNativeInput(globals))) return new Lease(null);
            if (portal is not null) throw new InvalidOperationException("已有桌面共享会话。");
            portal = await PortalDesktopSession.OpenAsync(enableInput, ct);
            return new Lease(portal);
        }
        finally { lifecycle.Release(); }
    }

    public static Task<CapturedScreen> CaptureAsync(uint output, bool includeCursor = true, CancellationToken ct = default)
        => portal is { } active ? active.CaptureAsync(output, ct) : WaylandScreenCapture.CaptureAsync(output, includeCursor, ct);
    public static async Task<IDesktopPointer> CreatePointerAsync(uint output, CancellationToken ct)
        => portal is { } active ? active.CreatePointer(output) : await WaylandVirtualPointer.CreateAsync(output, ct);
    public static async Task<IDesktopKeyboard> CreateKeyboardAsync(CancellationToken ct)
    {
        if (portal is { } active) return active.CreateKeyboard();
        var backend = Environment.GetEnvironmentVariable("UREMOTE_KEYBOARD_BACKEND") ?? "auto";
        if (backend is not ("auto" or "uinput" or "wayland"))
            throw new ArgumentException("UREMOTE_KEYBOARD_BACKEND must be auto, uinput or wayland.");
        if (backend != "wayland")
        {
            try { return await UInputKeyboard.CreateAsync(ct); }
            catch (UnauthorizedAccessException) when (backend == "auto") { }
        }
        return await WaylandVirtualKeyboard.CreateAsync(ct);
    }
    public static Task<IReadOnlyDictionary<uint, int>> ReadRefreshRatesAsync(CancellationToken ct)
        => portal is not null ? Task.FromResult<IReadOnlyDictionary<uint, int>>(new Dictionary<uint, int>()) : WaylandOutputRefresh.ReadAsync(ct);

    private sealed class Lease(PortalDesktopSession? owned) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (owned is null) return;
            await lifecycle.WaitAsync();
            try { if (ReferenceEquals(portal, owned)) { portal = null; await owned.DisposeAsync(); } }
            finally { lifecycle.Release(); }
        }
    }
}
