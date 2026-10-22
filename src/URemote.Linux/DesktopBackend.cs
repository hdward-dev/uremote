using System.Diagnostics;
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

public interface IDesktopTouch : IAsyncDisposable
{
    bool Available { get; }
    bool Apply(uint output, HostTouchEvent input);
    void Reset();
}

// Select by compositor capabilities rather than an allowlist of desktop names.
public static class DesktopBackend
{
    private static PortalDesktopSession? portal;
    private static readonly SemaphoreSlim lifecycle = new(1);
    private static readonly object recycleLock = new();
    private static string? recycleReason;
    private static long lastRecycleTicks;
    private static long pipelineQuietUntil;
    public static bool UsingPortal => Volatile.Read(ref portal) is not null;
    public static bool RecyclePending { get { lock (recycleLock) return recycleReason is not null; } }
    // True while a portal recycle is queued or a transport reset is being absorbed.
    public static bool PipelineResetActive => RecyclePending || PipelineQuietRemaining > TimeSpan.Zero;
    public static TimeSpan PipelineQuietRemaining
    {
        get
        {
            var until = Volatile.Read(ref pipelineQuietUntil);
            if (until == 0) return TimeSpan.Zero;
            var remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), until);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
    public static void ArmPipelineQuiet(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return;
        var until = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
        long current;
        do
        {
            current = Volatile.Read(ref pipelineQuietUntil);
            if (current >= until) return;
        } while (Interlocked.CompareExchange(ref pipelineQuietUntil, until, current) != current);
    }
    // Queue a portal reopen. The host loop disposes the lease and calls OpenAsync again,
    // which is what replaces the stuck PipeWire node and its output ids.
    public static bool RequestPortalRecycle(string reason)
    {
        if (!UsingPortal) return false;
        var sanitized = reason is "unlock" or "unblank" or "low-bitrate" or "low-luma" ? reason : "low-bitrate";
        var forced = sanitized is "unlock" or "unblank";
        lock (recycleLock)
        {
            if (recycleReason is not null) return true;
            if (!forced && lastRecycleTicks != 0 && Stopwatch.GetElapsedTime(lastRecycleTicks) < TimeSpan.FromSeconds(8))
                return false;
            lastRecycleTicks = Stopwatch.GetTimestamp();
            recycleReason = sanitized;
            return true;
        }
    }
    public static string? TakeRecycleReason()
    {
        lock (recycleLock)
        {
            var reason = recycleReason;
            recycleReason = null;
            return reason;
        }
    }
    public static Task WatchPortalSessionAsync(Action<string> onTransition, Action<string> report, CancellationToken ct)
        => PortalSessionActivity.WatchAsync(onTransition, report, ct);
    public static bool HasNativeCapture(IEnumerable<WaylandGlobal> globals) => globals.Any(g => g.Interface == "zwlr_screencopy_manager_v1");
    public static bool HasNativeInput(IEnumerable<WaylandGlobal> globals) => globals.Any(g => g.Interface == "zwlr_virtual_pointer_manager_v1" && g.Version >= 2)
        && globals.Any(g => g.Interface == "zwp_virtual_keyboard_manager_v1");

    public static async Task<IReadOnlyList<WaylandGlobal>> DiscoverAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsMacOS()) return await MacDesktop.DiscoverAsync(ct);
        if (Volatile.Read(ref portal) is { } active)
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
        if (OperatingSystem.IsMacOS()) return await MacDesktop.OpenAsync(enableInput, ct);
        await lifecycle.WaitAsync(ct);
        try
        {
            var globals = await WaylandCapabilities.DiscoverAsync(ct);
            if (HasNativeCapture(globals) && (!enableInput || HasNativeInput(globals))) return new Lease(null);
            if (Volatile.Read(ref portal) is not null) throw new InvalidOperationException("已有桌面共享会话。");
            var opened = await PortalDesktopSession.OpenAsync(enableInput, ct);
            Volatile.Write(ref portal, opened);
            return new Lease(opened);
        }
        finally { lifecycle.Release(); }
    }

    public static Task<CapturedScreen> CaptureAsync(uint output, bool includeCursor = true, CancellationToken ct = default)
        => OperatingSystem.IsMacOS() ? MacDesktop.CaptureAsync(output, includeCursor, ct) : Volatile.Read(ref portal) is { } active ? active.CaptureAsync(output, ct) : WaylandScreenCapture.CaptureAsync(output, includeCursor, ct);
    public static async Task<IDesktopPointer> CreatePointerAsync(uint output, CancellationToken ct)
        => OperatingSystem.IsMacOS() ? new MacDesktop.Pointer(output) : Volatile.Read(ref portal) is { } active ? active.CreatePointer(output) : await WaylandVirtualPointer.CreateAsync(output, ct);
    public static async Task<IDesktopKeyboard> CreateKeyboardAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS()) return new MacDesktop.Keyboard();
        if (Volatile.Read(ref portal) is { } active) return active.CreateKeyboard();
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
    public static Task<IDesktopTouch?> CreateTouchAsync(CancellationToken ct)
        => OperatingSystem.IsMacOS() ? Task.FromResult<IDesktopTouch?>(null) : Volatile.Read(ref portal) is { } active ? active.CreateTouchAsync(ct) : Task.FromResult<IDesktopTouch?>(null);
    public static Task TypeTextAsync(string text, CancellationToken ct)
    {
        HostControlInput.ValidateText(text);
        return OperatingSystem.IsMacOS() ? MacDesktop.TypeTextAsync(text, ct) : Volatile.Read(ref portal) is { } active ? active.TypeTextAsync(text, ct) : WaylandVirtualKeyboard.TypeTextAsync(text, ct);
    }
    public static async Task<IReadOnlyDictionary<uint, int>> ReadRefreshRatesAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS()) return await MacDesktop.RefreshAsync(ct);
        // Portal node ids are not wl_output names, but the compositor still publishes the mode.
        var outputs = await WaylandOutputRefresh.ReadOutputsAsync(ct);
        var active = Volatile.Read(ref portal);
        if (active is null)
            return outputs.Where(item => item.MilliHz > 0).ToDictionary(item => item.Name, item => item.MilliHz);
        return PortalOutputRefresh.Map(active.Layout, outputs);
    }

    private sealed class Lease(PortalDesktopSession? owned) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (owned is null) return;
            await lifecycle.WaitAsync();
            try { if (ReferenceEquals(Volatile.Read(ref portal), owned)) { Volatile.Write(ref portal, null); await owned.DisposeAsync(); } }
            finally { lifecycle.Release(); }
        }
    }
}
