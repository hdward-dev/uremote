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
    private static readonly object aliasLock = new();
    private static readonly Dictionary<uint, uint> outputAlias = [];
    private static string? recycleReason;
    private static long lastRecycleTicks;
    private static long pipelineQuietUntil;
    private static int captureRefreshActive;
    private static int portalGeneration;
    internal static PortalDesktopSession? CurrentPortal => Volatile.Read(ref portal);
    internal static int PortalGeneration => Volatile.Read(ref portalGeneration);
    internal static bool CaptureRefreshActive => Volatile.Read(ref captureRefreshActive) != 0;
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
    // Queue a hard portal reopen. Unlock and unblank do not use this: they refresh capture
    // under the live preview so the viewer and control room stay up.
    public static bool RequestPortalRecycle(string reason)
    {
        lock (recycleLock)
        {
            var since = lastRecycleTicks == 0 ? (TimeSpan?)null : Stopwatch.GetElapsedTime(lastRecycleTicks);
            if (!ShouldQueueRecycle(reason, UsingPortal, recycleReason is not null, since, out var sanitized))
                return false;
            if (recycleReason is not null) return true;
            lastRecycleTicks = Stopwatch.GetTimestamp();
            recycleReason = sanitized;
            return true;
        }
    }
    // refresh-failed is forced: NoteSoftRefresh already started the 8s cooldown, and a
    // failed in-place swap must still be able to restart the preview immediately.
    internal static bool ShouldQueueRecycle(string reason, bool portalOpen, bool alreadyPending, TimeSpan? sinceLast, out string sanitized)
    {
        sanitized = reason is "unlock" or "unblank" or "low-bitrate" or "low-luma" or "refresh-failed" ? reason : "low-bitrate";
        if (!portalOpen) return false;
        if (alreadyPending) return true;
        var forced = sanitized is "unlock" or "unblank" or "refresh-failed";
        return forced || sinceLast is not { } elapsed || elapsed >= TimeSpan.FromSeconds(8);
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
    // Low-bitrate/low-luma recycles are not forced. A soft refresh starts their cooldown
    // so the black frames of the swap do not cancel the preview.
    internal static void NoteSoftRefresh()
    {
        lock (recycleLock) lastRecycleTicks = Stopwatch.GetTimestamp();
    }
    internal static string PortalTopologySignature()
    {
        var active = Volatile.Read(ref portal);
        return active is null ? "" : PortalTopology.Signature(active.Layout);
    }
    internal static uint ResolveOutput(uint output)
    {
        lock (aliasLock)
        {
            var guard = 0;
            while (outputAlias.TryGetValue(output, out var next) && next != output && guard++ < 8)
                output = next;
            return output;
        }
    }
    internal static void NoteOutputAliases(IReadOnlyDictionary<uint, uint> hop)
    {
        lock (aliasLock)
        {
            foreach (var key in outputAlias.Keys.ToArray())
                if (hop.TryGetValue(outputAlias[key], out var newest))
                    outputAlias[key] = newest;
            foreach (var pair in hop)
                outputAlias[pair.Key] = pair.Value;
        }
    }
    internal static void ResetOutputAliasesForTests()
    {
        lock (aliasLock) outputAlias.Clear();
    }
    internal static async Task WaitCaptureReadyAsync(CancellationToken ct)
    {
        for (var i = 0; i < 200 && Volatile.Read(ref captureRefreshActive) != 0; i++)
            await Task.Delay(50, ct).ConfigureAwait(false);
    }
    // Replace the PipeWire streams after the greeter. The existing lease keeps owning whatever
    // session is current, and callers keep using the original stream ids through ResolveOutput.
    internal static async Task RefreshPortalAsync(bool enableInput, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return;
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Exchange(ref captureRefreshActive, 1);
        try
        {
            var previous = Volatile.Read(ref portal);
            if (previous is null || previous.IsDisposed) return;
            var opened = await PortalDesktopSession.OpenAsync(enableInput, ct).ConfigureAwait(false);
            var published = false;
            try
            {
                var map = PortalStreamMap.Match(previous.Layout, opened.Layout);
                if (previous.Layout.Count == 0 || map.Count != previous.Layout.Count)
                    throw new IOException("portal refresh output mismatch");
                NoteOutputAliases(map);
                Volatile.Write(ref portal, opened);
                Interlocked.Increment(ref portalGeneration);
                published = true;
            }
            finally { if (!published) await opened.DisposeAsync().ConfigureAwait(false); }
            // The new session is already live. A close error on the greeter lease must not
            // look like a failed refresh and tear the viewer down.
            try { await previous.DisposeAsync().ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or DesktopSessionClosedException or InvalidOperationException or TimeoutException) { }
        }
        finally
        {
            Interlocked.Exchange(ref captureRefreshActive, 0);
            lifecycle.Release();
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
            if (HasNativeCapture(globals) && (!enableInput || HasNativeInput(globals))) return new Lease(false);
            if (Volatile.Read(ref portal) is not null) throw new InvalidOperationException("已有桌面共享会话。");
            var opened = await PortalDesktopSession.OpenAsync(enableInput, ct);
            ResetOutputAliasesForTests();
            Volatile.Write(ref portal, opened);
            return new Lease(true);
        }
        finally { lifecycle.Release(); }
    }

    public static async Task<CapturedScreen> CaptureAsync(uint output, bool includeCursor = true, CancellationToken ct = default)
    {
        if (OperatingSystem.IsMacOS()) return await MacDesktop.CaptureAsync(output, includeCursor, ct);
        if (Volatile.Read(ref captureRefreshActive) != 0) await WaitCaptureReadyAsync(ct);
        var active = Volatile.Read(ref portal);
        if (active is not null) return await active.CaptureAsync(ResolveOutput(output), ct);
        return await WaylandScreenCapture.CaptureAsync(output, includeCursor, ct);
    }
    // Portal stream boxes, copied so a later recycle cannot mutate a lock-screen mapping.
    internal static ScreenRect[] ScreenLayout()
    {
        var active = Volatile.Read(ref portal);
        if (active is null) return [];
        var list = new List<ScreenRect>(active.Layout.Count);
        foreach (var box in active.Layout)
            if (box.Width > 0 && box.Height > 0)
                list.Add(new ScreenRect(box.Node, box.X, box.Y, box.Width, box.Height));
        return list.ToArray();
    }
    public static async Task<IDesktopPointer> CreatePointerAsync(uint output, CancellationToken ct, Action<string>? report = null, uint width = 0, uint height = 0)
    {
        if (OperatingSystem.IsMacOS()) return new MacDesktop.Pointer(output);
        IDesktopPointer primary = Volatile.Read(ref portal) is { } active ? active.CreatePointer(output) : await WaylandVirtualPointer.CreateAsync(output, ct);
        // KWin's lock screen ignores portal and wl_pointer injection. Keep a uinput absolute pointer ready to take over.
        if (!OperatingSystem.IsLinux()) return primary;
        return await LockScreenInput.WrapPointerAsync(output, primary, width, height, report ?? (_ => { }), ct);
    }
    public static async Task<IDesktopKeyboard> CreateKeyboardAsync(CancellationToken ct, Action<string>? report = null)
    {
        report ??= static _ => { };
        if (OperatingSystem.IsMacOS())
        {
            report("keyboard-backend=macos");
            return new MacDesktop.Keyboard();
        }
        if (Volatile.Read(ref portal) is { } active)
            return await LockScreenInput.WrapKeyboardAsync(active.CreateKeyboard(), "portal", report, ct);
        var backend = Environment.GetEnvironmentVariable("UREMOTE_KEYBOARD_BACKEND") ?? "auto";
        if (backend is not ("auto" or "uinput" or "wayland"))
            throw new ArgumentException("UREMOTE_KEYBOARD_BACKEND must be auto, uinput or wayland.");
        if (backend != "wayland")
        {
            try
            {
                var keyboard = await UInputKeyboard.CreateAsync(ct);
                report("keyboard-backend=uinput");
                return keyboard;
            }
            catch (UnauthorizedAccessException) when (backend == "auto") { }
        }
        return await LockScreenInput.WrapKeyboardAsync(await WaylandVirtualKeyboard.CreateAsync(ct), "wayland", report, ct);
    }
    public static Task<IDesktopTouch?> CreateTouchAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (OperatingSystem.IsMacOS() || Volatile.Read(ref portal) is null)
            return Task.FromResult<IDesktopTouch?>(null);
        return Task.FromResult<IDesktopTouch?>(new LivePortalTouch());
    }
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

    // Owns whichever portal session is current, including one swapped in by a lock-screen refresh.
    private sealed class Lease(bool ownsPortal) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (!ownsPortal) return;
            await lifecycle.WaitAsync();
            try
            {
                var current = Interlocked.Exchange(ref portal, null);
                ResetOutputAliasesForTests();
                if (current is not null) await current.DisposeAsync();
            }
            finally { lifecycle.Release(); }
        }
    }

    private sealed class LivePortalTouch : IDesktopTouch
    {
        public bool Available => CurrentPortal is { IsDisposed: false, TouchAvailable: true };
        public bool Apply(uint output, HostTouchEvent input) => CurrentPortal is { IsDisposed: false } live && live.TryApplyTouch(output, input);
        public void Reset() { if (CurrentPortal is { IsDisposed: false } live) live.ResetTouch(); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
