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
    private static X11DesktopSession? x11;
    private static readonly SemaphoreSlim lifecycle = new(1);
    private static readonly object recycleLock = new();
    private static readonly object aliasLock = new();
    private static readonly Dictionary<uint, uint> outputAlias = [];
    private static string? recycleReason;
    private static long lastRecycleTicks;
    private static long pipelineQuietUntil;
    private static int captureRefreshActive;
    private static int portalGeneration;
    private static int portalWantsInput;
    private static Func<IReadOnlyList<uint>, CancellationToken, Task>? openHook;
    private static Func<CancellationToken, Task<int>>? closeHook;
    private static CaptureScheduler capture = new(OpenArmedAsync, CloseArmedAsync, CaptureScheduler.DefaultGrace);
    internal static PortalDesktopSession? CurrentPortal => Volatile.Read(ref portal);
    internal static int PortalGeneration => Volatile.Read(ref portalGeneration);
    internal static bool CaptureRefreshActive => Volatile.Read(ref captureRefreshActive) != 0;
    internal static bool PortalInputEnabled => Volatile.Read(ref portalWantsInput) != 0;
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
            return outputAlias.GetValueOrDefault(output, output);
    }
    // Public output ids belong to the first authorization, not to a PipeWire generation.
    // Native ids may be reused for another screen, so never follow them as alias keys.
    internal static uint LogicalOutput(uint node)
    {
        lock (aliasLock)
            return outputAlias.FirstOrDefault(pair => pair.Value == node, new(node, node)).Key;
    }
    internal static void NoteOutputAliases(IReadOnlyDictionary<uint, uint> hop)
    {
        lock (aliasLock)
        {
            if (outputAlias.Count == 0)
            {
                foreach (var pair in hop) outputAlias.Add(pair.Key, pair.Value);
                return;
            }
            var next = outputAlias.ToDictionary(pair => pair.Key,
                pair => hop.TryGetValue(pair.Value, out var node) ? node
                    : throw new IOException("portal refresh output mismatch"));
            foreach (var pair in next) outputAlias[pair.Key] = pair.Value;
        }
    }
    internal static void ResetOutputAliasesForTests()
    {
        lock (aliasLock) outputAlias.Clear();
    }
    // Host output ids survive a portal refresh. Publish the current node's refresh under those ids too.
    internal static Dictionary<uint, int> PublishRefresh(IReadOnlyList<PortalStreamBox> layout, IReadOnlyDictionary<uint, int> mapped)
    {
        var published = new Dictionary<uint, int>();
        foreach (var box in layout)
        {
            var hz = mapped.GetValueOrDefault(box.Node);
            if (hz <= 0) hz = box.RefreshMilliHz;
            if (hz > 0) published[box.Node] = hz;
        }
        // Read native rates from an immutable snapshot: overwriting a reused native id
        // with its logical screen's rate would corrupt the next screen's lookup.
        lock (aliasLock)
            return outputAlias.Count == 0 ? published : outputAlias
                .Where(pair => published.ContainsKey(pair.Value))
                .ToDictionary(pair => pair.Key, pair => published[pair.Value]);
    }
    internal static int StoredRefreshMilliHz(uint output)
    {
        var active = Volatile.Read(ref portal);
        return active is null ? 0 : active.RefreshOf(output);
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
        var swapped = false;
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
            swapped = published;
        }
        finally
        {
            Interlocked.Exchange(ref captureRefreshActive, 0);
            lifecycle.Release();
        }
        // After the lifecycle lock so pipe open cannot deadlock against this refresh.
        // Idle refresh stops at authorization and geometry. An active viewer gets new pipelines.
        if (swapped) await capture.NoteSessionSwappedAsync(ct).ConfigureAwait(false);
    }
    public static Task WatchPortalSessionAsync(Action<string> onTransition, Action<string> report, CancellationToken ct)
        => PortalSessionActivity.WatchAsync(onTransition, report, ct);
    public static bool HasNativeCapture(IEnumerable<WaylandGlobal> globals) => globals.Any(g => g.Interface == "zwlr_screencopy_manager_v1");
    public static bool HasNativeInput(IEnumerable<WaylandGlobal> globals) => globals.Any(g => g.Interface == "zwlr_virtual_pointer_manager_v1" && g.Version >= 2)
        && globals.Any(g => g.Interface == "zwp_virtual_keyboard_manager_v1");

    public static async Task<IReadOnlyList<WaylandGlobal>> DiscoverAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows()) return await WindowsDesktop.DiscoverAsync(ct);
        if (OperatingSystem.IsMacOS()) return await MacDesktop.DiscoverAsync(ct);
        if (LinuxSessionKind.UseX11())
            return Volatile.Read(ref x11) is { } x11Session ? x11Session.Outputs() : X11DesktopSession.Discover(ct);
        if (Volatile.Read(ref portal) is { } active)
        {
            await active.CheckAliveAsync(ct);
            return active.Outputs.Select(item => item with { Name = LogicalOutput(item.Name) }).ToArray();
        }
        var globals = await WaylandCapabilities.DiscoverAsync(ct);
        if (HasNativeCapture(globals)) return globals;
        await PortalDesktopSession.ProbeAsync(ct);
        return []; // The system chooser supplies authorized streams when hosting starts.
    }

    public static async Task<IAsyncDisposable> OpenAsync(bool enableInput, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) return await WindowsDesktop.OpenAsync(enableInput, ct);
        if (OperatingSystem.IsMacOS()) return await MacDesktop.OpenAsync(enableInput, ct);
        await lifecycle.WaitAsync(ct);
        try
        {
            if (LinuxSessionKind.UseX11())
            {
                if (Volatile.Read(ref x11) is not null) throw new InvalidOperationException("已有桌面共享会话。");
                var x11Opened = await X11DesktopSession.OpenAsync(enableInput, ct);
                Volatile.Write(ref x11, x11Opened);
                return new X11Lease();
            }
            var globals = await WaylandCapabilities.DiscoverAsync(ct);
            if (HasNativeCapture(globals) && (!enableInput || HasNativeInput(globals))) return new Lease(false);
            if (Volatile.Read(ref portal) is not null) throw new InvalidOperationException("已有桌面共享会话。");
            Interlocked.Exchange(ref portalWantsInput, enableInput ? 1 : 0);
            var opened = await PortalDesktopSession.OpenAsync(enableInput, ct);
            ResetOutputAliasesForTests();
            NoteOutputAliases(opened.CopyLayout().ToDictionary(box => box.Node, box => box.Node));
            Volatile.Write(ref portal, opened);
            return new Lease(true);
        }
        finally { lifecycle.Release(); }
    }

    public static async Task<CapturedScreen> CaptureAsync(uint output, bool includeCursor = true, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows()) return await WindowsDesktop.CaptureAsync(output, includeCursor, ct);
        if (OperatingSystem.IsMacOS()) return await MacDesktop.CaptureAsync(output, includeCursor, ct);
        if (LinuxSessionKind.UseX11())
        {
            if (Volatile.Read(ref x11) is not { } x11Session)
                return await X11DesktopSession.CaptureOneShotAsync(output, includeCursor, ct);
            if (!capture.StreamsOpen)
            {
                var size = x11Session.SizeOf(output);
                var width = size.Width >= 2 ? size.Width : 1280;
                var height = size.Height >= 2 ? size.Height : 720;
                return CapturePlaceholder.Create(output, width, height);
            }
            var x11Frame = await x11Session.CaptureAsync(output, includeCursor, ct);
            if (!x11Frame.Pending) NoteCaptureBuffer(output);
            return x11Frame;
        }
        if (Volatile.Read(ref captureRefreshActive) != 0) await WaitCaptureReadyAsync(ct);
        var active = Volatile.Read(ref portal);
        if (active is not null)
        {
            var frame = await active.CaptureAsync(ResolveOutput(output), ct);
            if (!frame.Pending) NoteCaptureBuffer(output);
            return frame;
        }
        return await WaylandScreenCapture.CaptureAsync(output, includeCursor, ct);
    }
    // One screen with no buffer, or a dead pipeline, becomes a black frame of that monitor's size.
    internal static async Task<CapturedScreen> CaptureOrPendingAsync(uint output, CancellationToken ct)
    {
        try { return await CaptureAsync(output, ct: ct); }
        catch (Exception e) when (!ct.IsCancellationRequested && e is TimeoutException or IOException or ObjectDisposedException or FormatException or ArgumentException)
        {
            var size = OutputSize(output);
            var width = size.Width >= 2 ? size.Width : 1280;
            var height = size.Height >= 2 ? size.Height : 720;
            return CapturePlaceholder.Create(output, width, height);
        }
    }
    // Portal logical size for a stream that has not delivered a buffer yet.
    internal static (int Width, int Height) OutputSize(uint output)
    {
        var active = Volatile.Read(ref portal);
        if (active is null) return (0, 0);
        var node = ResolveOutput(output);
        foreach (var box in active.Layout)
            if (box.Node == node && box.ModeWidth >= 2 && box.ModeHeight >= 2)
                return (box.ModeWidth, box.ModeHeight);
        foreach (var box in active.Layout)
            if (box.Node == node && box.Width >= 2 && box.Height >= 2)
                return (box.Width, box.Height);
        return (0, 0);
    }
    // Matched wl_output mode. Zero means this screen has no hardware binding yet, so a
    // later real frame may still replace a placeholder. A binding is a hard ceiling.
    internal static (int Width, int Height) BoundMode(uint output)
    {
        var active = Volatile.Read(ref portal);
        if (active is null) return (0, 0);
        var node = ResolveOutput(output);
        foreach (var box in active.Layout)
            if (box.Node == node && box.ModeWidth >= 2 && box.ModeHeight >= 2)
                return (box.ModeWidth, box.ModeHeight);
        return (0, 0);
    }
    // One screen's PipeWire pipeline. Does not open a portal session, so it cannot prompt.
    // Returns false when the screen is locked: a blank output is left running.
    internal static async Task<bool> RebuildCaptureAsync(uint output, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || SessionLock.IsLocked == true) return false;
        if (capture.ViewerCount == 0) return false;
        if (Volatile.Read(ref captureRefreshActive) != 0) await WaitCaptureReadyAsync(ct);
        if (SessionLock.IsLocked == true || capture.ViewerCount == 0) return false;
        if (!capture.StreamsOpen)
        {
            await capture.EnsureOpenAsync(ct).ConfigureAwait(false);
            return false;
        }
        if (Volatile.Read(ref portal) is not { } active) return false;
        await active.RebuildStreamAsync(ResolveOutput(output), ct);
        return SessionLock.IsLocked != true;
    }

    internal static TimeSpan CaptureGrace => capture.Grace;
    internal static bool CaptureStreamsOpenForTests => capture.StreamsOpen;
    internal static int CaptureViewersForTests => capture.ViewerCount;
    internal static bool X11DamageSubscribedForTests => Volatile.Read(ref x11)?.DamageSubscribed == true;
    internal static bool X11DamageSupportedForTests => Volatile.Read(ref x11)?.DamageSupported == true;
    internal static int X11ShmBuffersForTests => Volatile.Read(ref x11)?.ShmBuffers ?? 0;

    // Host calls this when be-controlled is accepted, before ICE and SDP. File and terminal sessions do not arm.
    internal static Task ArmForViewerAsync(IReadOnlyList<uint> outputs, bool dataOnly, Action<string> report, CancellationToken ct)
    {
        if (dataOnly || outputs.Count == 0) return Task.CompletedTask;
        return capture.ArmAsync(outputs, "viewer-requested", report, ct);
    }

    internal static void ReleaseCapture() => capture.Release();
    internal static void NoteCaptureBuffer(uint output) => capture.NoteRealFrame(output);
    internal static void SetCaptureGraceForTests(TimeSpan grace) => capture.SetGrace(grace);
    internal static Task NoteCaptureSessionSwappedForTests(CancellationToken ct) => capture.NoteSessionSwappedAsync(ct);

    internal static void InstallCaptureHooksForTests(
        Func<IReadOnlyList<uint>, CancellationToken, Task> open,
        Func<CancellationToken, Task<int>> close,
        TimeSpan? grace = null)
    {
        capture.Abandon();
        openHook = open;
        closeHook = close;
        capture = new CaptureScheduler(OpenArmedAsync, CloseArmedAsync, grace ?? TimeSpan.FromMilliseconds(40));
    }

    internal static void ResetCaptureForTests()
    {
        capture.Abandon();
        openHook = null;
        closeHook = null;
        capture = new CaptureScheduler(OpenArmedAsync, CloseArmedAsync, CaptureScheduler.DefaultGrace);
    }

    private static Task OpenArmedAsync(IReadOnlyList<uint> screens, CancellationToken ct)
        => openHook is { } hook ? hook(screens, ct) : OpenArmedCoreAsync(screens, ct);

    private static Task<int> CloseArmedAsync(CancellationToken ct)
        => closeHook is { } hook ? hook(ct) : CloseArmedCoreAsync(ct);

    private static async Task OpenArmedCoreAsync(IReadOnlyList<uint> screens, CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (LinuxSessionKind.UseX11())
            {
                (Volatile.Read(ref x11) ?? throw new InvalidOperationException("X11 桌面会话未打开。")).ArmCapture(screens);
                return;
            }
            var active = Volatile.Read(ref portal);
            if (active is null || active.IsDisposed) throw new InvalidOperationException("桌面授权会话不存在。");
            await active.OpenPipesAsync(screens, ct).ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
    }

    private static async Task<int> CloseArmedCoreAsync(CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (LinuxSessionKind.UseX11())
                return Volatile.Read(ref x11)?.ReleaseCapture() ?? 0;
            var active = Volatile.Read(ref portal);
            if (active is null || active.IsDisposed) return 0;
            return await active.ClosePipesAsync().ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
    }
    internal static bool HasPortalRestoreToken()
    {
        if (!OperatingSystem.IsLinux() || Volatile.Read(ref portal) is null) return false;
        return new PortalRestoreStore(PortalInputEnabled).Peek() is not null;
    }
    // Portal stream boxes, copied so a later recycle cannot mutate a lock-screen mapping.
    internal static ScreenRect[] ScreenLayout()
    {
        var active = Volatile.Read(ref portal);
        if (active is null) return [];
        var list = new List<ScreenRect>(active.Layout.Count);
        foreach (var box in active.CopyLayout())
            if (box.Width > 0 && box.Height > 0)
                list.Add(new ScreenRect(LogicalOutput(box.Node), box.X, box.Y, box.Width, box.Height));
        return list.ToArray();
    }
    internal static void ReportPortalOutputs(Action<string> report)
    {
        if (Volatile.Read(ref portal) is not { } active) return;
        foreach (var box in active.CopyLayout())
            report(FormattableString.Invariant($"portal-output-map;output={LogicalOutput(box.Node)};node={box.Node};x={box.X};y={box.Y};width={box.Width};height={box.Height}"));
    }
    public static async Task<IDesktopPointer> CreatePointerAsync(uint output, CancellationToken ct, Action<string>? report = null, uint width = 0, uint height = 0)
    {
        if (OperatingSystem.IsWindows()) return new WindowsDesktop.Pointer(output);
        if (OperatingSystem.IsMacOS()) return new MacDesktop.Pointer(output);
        if (LinuxSessionKind.UseX11())
            return (Volatile.Read(ref x11) ?? throw new InvalidOperationException("X11 桌面会话未打开。")).CreatePointer(output, width, height);
        IDesktopPointer primary;
        if (Volatile.Read(ref portal) is { } active)
        {
            // The host already has this screen's geometry. A pending or locked pipeline must not reject the pointer.
            var known = width >= 2 && height >= 2 || OutputSize(output).Width >= 2;
            primary = active.CreatePointer(output, allowUnbound: known);
        }
        else primary = await WaylandVirtualPointer.CreateAsync(output, ct);
        // KWin's lock screen ignores portal and wl_pointer injection. Keep a uinput absolute pointer ready to take over.
        if (!OperatingSystem.IsLinux()) return primary;
        return await LockScreenInput.WrapPointerAsync(output, primary, width, height, report ?? (_ => { }), ct);
    }
    public static async Task<IDesktopKeyboard> CreateKeyboardAsync(CancellationToken ct, Action<string>? report = null)
    {
        report ??= static _ => { };
        if (OperatingSystem.IsWindows())
        {
            report("keyboard-backend=windows");
            return new WindowsDesktop.Keyboard();
        }
        if (OperatingSystem.IsMacOS())
        {
            report("keyboard-backend=macos");
            return new MacDesktop.Keyboard();
        }
        if (LinuxSessionKind.UseX11())
        {
            report("keyboard-backend=x11");
            return (Volatile.Read(ref x11) ?? throw new InvalidOperationException("X11 桌面会话未打开。")).CreateKeyboard();
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
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || Volatile.Read(ref portal) is null)
            return Task.FromResult<IDesktopTouch?>(null);
        return Task.FromResult<IDesktopTouch?>(new LivePortalTouch());
    }
    public static Task TypeTextAsync(string text, CancellationToken ct)
    {
        HostControlInput.ValidateText(text);
        if (LinuxSessionKind.UseX11())
            return (Volatile.Read(ref x11) ?? throw new InvalidOperationException("X11 桌面会话未打开。")).TypeTextAsync(text, ct);
        if (OperatingSystem.IsWindows()) return WindowsDesktop.TypeTextAsync(text, ct);
        if (OperatingSystem.IsMacOS()) return MacDesktop.TypeTextAsync(text, ct);
        return Volatile.Read(ref portal) is { } active ? CommittedTextInjector.InjectPortalAsync(active, text, ct) : WaylandVirtualKeyboard.TypeTextAsync(text, ct);
    }
    public static async Task<IReadOnlyDictionary<uint, int>> ReadRefreshRatesAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) return await WindowsDesktop.RefreshAsync(ct);
        if (OperatingSystem.IsMacOS()) return await MacDesktop.RefreshAsync(ct);
        if (LinuxSessionKind.UseX11())
        {
            if (Volatile.Read(ref x11) is { } x11Session) return x11Session.RefreshRates();
            using var connection = X11Connection.Open(null);
            return connection.Monitors(refresh: true).ToDictionary(monitor => monitor.Id, monitor => monitor.MilliHz);
        }
        // Portal node ids are not wl_output names, but the compositor still publishes the mode.
        var active = Volatile.Read(ref portal);
        IReadOnlyList<WaylandOutputMode> outputs;
        try { outputs = await WaylandOutputRefresh.ReadOutputsAsync(ct); }
        catch (Exception e) when (active is not null && !ct.IsCancellationRequested && e is IOException or System.Net.Sockets.SocketException or OperationCanceledException or FormatException)
        { return PublishRefresh(active.CopyLayout(), active.ReadRefreshRates([])); }
        if (active is null)
            return outputs.Where(item => item.MilliHz > 0).ToDictionary(item => item.Name, item => item.MilliHz);
        var bound = PortalOutputRefresh.Bind(active.CopyLayout(), outputs);
        active.RememberModes(bound);
        var mapped = bound.Where(item => item.Value.RefreshMilliHz > 0).ToDictionary(item => item.Key, item => item.Value.RefreshMilliHz);
        foreach (var item in active.ReadRefreshRates([])) mapped.TryAdd(item.Key, item.Value);
        return PublishRefresh(active.CopyLayout(), mapped);
    }
    public static async Task<IReadOnlyList<DesktopOutput>> ReadOutputsAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) return await WindowsDesktop.ReadOutputsAsync(ct);
        if (LinuxSessionKind.UseX11())
        {
            ct.ThrowIfCancellationRequested();
            using var connection = X11Connection.Open(null);
            return connection.Monitors(refresh: true).Select(monitor => new DesktopOutput(monitor.Id, "x11:" + monitor.Name, monitor.Name, monitor.MilliHz)).OrderBy(x => x.Id).Take(5).ToArray();
        }
        if (OperatingSystem.IsMacOS())
        {
            var rates = await MacDesktop.RefreshAsync(ct);
            return (await MacDesktop.DiscoverAsync(ct)).Select(x => new DesktopOutput(x.Name, "mac:" + x.Name, "显示器 " + x.Name, rates.GetValueOrDefault(x.Name))).ToArray();
        }
        if (portal is { } active) return active.DisplayDetails.Select(item => item with { Id = LogicalOutput(item.Id) }).OrderBy(x => x.Id).Take(5).ToArray();
        var globals = await DiscoverAsync(ct);
        return globals.Any(x => x.Interface == "wl_output") ? (await WaylandOutputDetails.ReadAsync(ct)).OrderBy(x => x.Id).Take(5).ToArray() : [];
    }

    public static void NoteRequestedFrameRate(uint output, int fps) => X11FrameRateNotes.Note(output, fps);

    private sealed class X11Lease : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await lifecycle.WaitAsync();
            try
            {
                capture.Abandon();
                var current = Interlocked.Exchange(ref x11, null);
                if (current is not null) await current.DisposeAsync();
            }
            finally { lifecycle.Release(); }
        }
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
                capture.Abandon();
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
