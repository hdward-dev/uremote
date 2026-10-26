namespace URemote.Linux;

// Combined lock signal. KWin can report the screensaver active while logind LockedHint stays false.
internal sealed class SessionLockState
{
    private bool? screensaver;
    private bool? loginHint;
    internal bool HasScreensaver => screensaver is not null;
    internal bool HasLoginHint => loginHint is not null;
    // Screensaver is authoritative. LockedHint can only add a lock: on Plasma it stays false while the greeter is up.
    internal bool? IsLocked
    {
        get
        {
            if (screensaver == true || loginHint == true) return true;
            if (screensaver == false) return false;
            return null;
        }
    }

    internal bool ObserveScreensaver(bool active)
    {
        var before = IsLocked;
        screensaver = active;
        return IsLocked != before;
    }

    internal bool ObserveLoginHint(bool locked)
    {
        var before = IsLocked;
        loginHint = locked;
        return IsLocked != before;
    }

    internal void Reset()
    {
        screensaver = null;
        loginHint = null;
    }
}

// Process-wide lock state fed by the screensaver (primary) and logind LockedHint (secondary).
internal static class SessionLock
{
    internal const int ScreensaverProbe = 1;
    internal const int LoginProbe = 2;
    private static readonly object gate = new();
    private static readonly SessionLockState state = new();
    private static Action? changed;
    private static int readyBits;
    private static int startLogged;
    private static Task? probeTask;

    internal static event Action? Changed
    {
        add { lock (gate) changed += value; }
        remove { lock (gate) changed -= value; }
    }

    internal static bool? IsLocked { get { lock (gate) return state.IsLocked; } }
    internal static bool HasScreensaver { get { lock (gate) return state.HasScreensaver; } }
    internal static bool HasLoginHint { get { lock (gate) return state.HasLoginHint; } }

    internal static bool ObserveScreensaver(bool active) => Observe(state.ObserveScreensaver, active);
    internal static bool ObserveLoginHint(bool locked) => Observe(state.ObserveLoginHint, locked);

    private static bool Observe(Func<bool, bool> apply, bool value)
    {
        bool changedNow;
        Action? handler;
        lock (gate)
        {
            changedNow = apply(value);
            handler = changed;
        }
        if (changedNow) handler?.Invoke();
        return changedNow;
    }

    // A new portal watch should log the startup lock again. Previous observations stay until replaced.
    internal static void BeginInitialProbe()
    {
        Interlocked.Exchange(ref readyBits, 0);
        Interlocked.Exchange(ref startLogged, 0);
    }

    internal static void NoteProbeFinished(int bit, Action<string> report)
    {
        if (bit is not (ScreensaverProbe or LoginProbe)) return;
        var bits = Interlocked.Or(ref readyBits, bit) | bit;
        if ((bits & 3) != 3 || Interlocked.Exchange(ref startLogged, 1) != 0) return;
        var locked = IsLocked;
        report("portal-start-locked=" + (locked switch { true => "1", false => "0", _ => "unknown" }));
    }

    internal static async Task<bool> EnsureKnownAsync(CancellationToken ct)
    {
        if (IsLocked is bool known) return known;
        var task = StartProbe();
        var finished = await Task.WhenAny(task, Task.Delay(1200, ct)).ConfigureAwait(false);
        if (finished == task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { }
        }
        return IsLocked == true;
    }

    private static Task StartProbe()
    {
        lock (gate)
        {
            if (state.IsLocked is not null) return Task.CompletedTask;
            if (probeTask is not null) return probeTask;
        }
        var started = ProbeAsync();
        lock (gate) return probeTask ??= started;
    }

    private static async Task ProbeAsync()
    {
        bool? saver = null;
        bool? hint = null;
        try
        {
            await using var bus = await PortalBus.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            saver = await TryReadScreenSaverActiveAsync(bus, CancellationToken.None, 800).ConfigureAwait(false);
        }
        catch (Exception e) when (IsBusFailure(e)) { }
        try
        {
            await using var bus = await PortalBus.ConnectSystemAsync(CancellationToken.None).ConfigureAwait(false);
            var path = await LoginSessionPathAsync(bus, CancellationToken.None, 800).ConfigureAwait(false);
            hint = await TryReadLockedHintAsync(bus, path, CancellationToken.None, 800).ConfigureAwait(false);
        }
        catch (Exception e) when (IsBusFailure(e)) { }
        var notify = false;
        Action? handler;
        lock (gate)
        {
            if (saver is bool active && !state.HasScreensaver) notify |= state.ObserveScreensaver(active);
            if (hint is bool locked && !state.HasLoginHint) notify |= state.ObserveLoginHint(locked);
            handler = changed;
        }
        if (notify) handler?.Invoke();
    }

    internal static async Task<bool?> TryReadScreenSaverActiveAsync(PortalBus bus, CancellationToken ct, int timeoutMs = 10000)
    {
        foreach (var path in new[] { "/org/freedesktop/ScreenSaver", "/ScreenSaver" })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var args = PortalValue.Tuple();
                using var reply = await bus.CallAsync("org.freedesktop.ScreenSaver", "GetActive", args, ct, path, "org.freedesktop.ScreenSaver", timeoutMs).ConfigureAwait(false);
                using var child = reply.Child(0);
                return child.Boolean();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is IOException or FormatException or InvalidOperationException or TimeoutException) { }
        }
        return null;
    }

    internal static async Task<string> LoginSessionPathAsync(PortalBus bus, CancellationToken ct, int timeoutMs = 10000)
    {
        using var pid = PortalValue.UInt((uint)Environment.ProcessId);
        using var args = PortalValue.Tuple(pid);
        using var reply = await bus.CallAsync("org.freedesktop.login1.Manager", "GetSessionByPID", args, ct,
            "/org/freedesktop/login1", "org.freedesktop.login1", timeoutMs).ConfigureAwait(false);
        using var pathValue = reply.Child(0);
        var path = pathValue.Text();
        if (!path.StartsWith("/org/freedesktop/login1/session/", StringComparison.Ordinal))
            throw new FormatException("无效的登录会话。");
        return path;
    }

    // logind LockedHint. On Plasma this can stay false while kscreenlocker is up; callers must not treat that as unlocked.
    internal static async Task<bool?> TryReadLockedHintAsync(PortalBus bus, string path, CancellationToken ct, int timeoutMs = 10000)
    {
        try
        {
            using var iface = PortalValue.String("org.freedesktop.login1.Session");
            using var name = PortalValue.String("LockedHint");
            using var get = PortalValue.Tuple(iface, name);
            using var current = await bus.CallAsync("org.freedesktop.DBus.Properties", "Get", get, ct, path, "org.freedesktop.login1", timeoutMs).ConfigureAwait(false);
            using var field = current.Child(0);
            using var boxed = field.Type == "v" ? field.Unbox() : null;
            return (boxed ?? field).Boolean();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or FormatException or InvalidOperationException or TimeoutException) { return null; }
    }

    internal static void ResetForTests()
    {
        lock (gate)
        {
            state.Reset();
            changed = null;
            probeTask = null;
        }
        Interlocked.Exchange(ref readyBits, 0);
        Interlocked.Exchange(ref startLogged, 0);
    }

    private static bool IsBusFailure(Exception e) => e is IOException or InvalidOperationException or FormatException or NotSupportedException
        or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException or UnauthorizedAccessException or TimeoutException;
}
