using System.Threading.Channels;

namespace URemote.Linux;

// Lock/blank leaves the existing KWin screencast node on black frames. Unlock has to drop that lease.
// The same signals feed SessionLock so input can leave the portal while the greeter is up.
internal static class PortalSessionActivity
{
    internal static async Task WatchAsync(Action<string> onTransition, Action<string> report, CancellationToken ct)
    {
        SessionLock.BeginInitialProbe();
        var transitions = Channel.CreateBounded<string>(new BoundedChannelOptions(4)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var saver = WatchScreenSaverAsync(transitions.Writer, report, linked.Token);
        var login = WatchLoginAsync(transitions.Writer, report, linked.Token);
        report("session-activity-watch-started");
        var last = StopwatchTimestamp.None;
        try
        {
            await foreach (var reason in transitions.Reader.ReadAllAsync(ct))
            {
                if (last.IsSet && StopwatchTimestamp.Elapsed(last) < TimeSpan.FromSeconds(2)) continue;
                last = StopwatchTimestamp.Now;
                try { onTransition(reason); }
                catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
                { report("portal-session-recreate-failed;type=" + e.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            linked.Cancel();
            transitions.Writer.TryComplete();
            try { await Task.WhenAll(saver, login); } catch { }
        }
    }

    private static async Task WatchScreenSaverAsync(ChannelWriter<string> writer, Action<string> report, CancellationToken ct)
    {
        try
        {
            await using var bus = await PortalBus.ConnectAsync(ct);
            var active = -1; // -1 unknown, 0 clear, 1 active. ActiveChanged does not replay the current value.
            var signaled = 0;
            var id = await bus.SubscribeAnyAsync(null, "org.freedesktop.ScreenSaver", "ActiveChanged", null, value =>
            {
                try
                {
                    value.Require("(b)");
                    using var state = value.Child(0);
                    var now = state.Boolean();
                    Interlocked.Exchange(ref signaled, 1);
                    if (Interlocked.Exchange(ref active, now ? 1 : 0) == 1 && !now) writer.TryWrite("unblank");
                    SessionLock.ObserveScreensaver(now);
                }
                catch { /* Ignore malformed screensaver signals. */ }
            });
            try
            {
                try
                {
                    if (await SessionLock.TryReadScreenSaverActiveAsync(bus, ct) is bool now
                        && Interlocked.CompareExchange(ref signaled, 1, 0) == 0)
                    {
                        Interlocked.Exchange(ref active, now ? 1 : 0);
                        SessionLock.ObserveScreensaver(now);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) when (IsBusFailure(e))
                { report("session-lock-probe-failed;source=screensaver;type=" + e.GetType().Name); }
                SessionLock.NoteProbeFinished(SessionLock.ScreensaverProbe, report);
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally { try { await bus.UnsubscribeAsync(id); } catch (IOException) { } }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) when (IsBusFailure(e))
        {
            report("session-activity-watch-unavailable;source=screensaver;type=" + e.GetType().Name);
            SessionLock.NoteProbeFinished(SessionLock.ScreensaverProbe, report);
        }
    }

    private static async Task WatchLoginAsync(ChannelWriter<string> writer, Action<string> report, CancellationToken ct)
    {
        try
        {
            await using var bus = await PortalBus.ConnectSystemAsync(ct);
            var path = await SessionLock.LoginSessionPathAsync(bus, ct);
            var locked = -1; // -1 unknown, 0 clear, 1 locked
            if (await SessionLock.TryReadLockedHintAsync(bus, path, ct) is bool initial)
            {
                locked = initial ? 1 : 0;
                SessionLock.ObserveLoginHint(initial);
            }
            SessionLock.NoteProbeFinished(SessionLock.LoginProbe, report);
            var properties = await bus.SubscribeAnyAsync("org.freedesktop.login1", "org.freedesktop.DBus.Properties", "PropertiesChanged", path, value =>
            {
                try
                {
                    value.Require("(sa{sv}as)");
                    using var iface = value.Child(0);
                    if (iface.Text() != "org.freedesktop.login1.Session") return;
                    using var changed = value.Child(1);
                    using var hint = changed.Get("LockedHint");
                    if (hint is null) return;
                    using var boxed = hint.Type == "v" ? hint.Unbox() : null;
                    var now = (boxed ?? hint).Boolean();
                    if (Interlocked.Exchange(ref locked, now ? 1 : 0) == 1 && !now) writer.TryWrite("unlock");
                    SessionLock.ObserveLoginHint(now);
                }
                catch { /* Ignore unrelated property changes. */ }
            });
            var unlock = await bus.SubscribeAnyAsync("org.freedesktop.login1", "org.freedesktop.login1.Session", "Unlock", path, _ =>
            {
                writer.TryWrite("unlock");
                Interlocked.Exchange(ref locked, 0);
                SessionLock.ObserveLoginHint(false);
            });
            var lockSignal = await bus.SubscribeAnyAsync("org.freedesktop.login1", "org.freedesktop.login1.Session", "Lock", path, _ =>
            {
                Interlocked.Exchange(ref locked, 1);
                SessionLock.ObserveLoginHint(true);
            });
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally
            {
                try { await bus.UnsubscribeAsync(properties); } catch (IOException) { }
                try { await bus.UnsubscribeAsync(unlock); } catch (IOException) { }
                try { await bus.UnsubscribeAsync(lockSignal); } catch (IOException) { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) when (IsBusFailure(e))
        {
            report("session-activity-watch-unavailable;source=login1;type=" + e.GetType().Name);
            SessionLock.NoteProbeFinished(SessionLock.LoginProbe, report);
        }
    }

    private static bool IsBusFailure(Exception e) => e is IOException or InvalidOperationException or FormatException or NotSupportedException
        or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException or UnauthorizedAccessException or TimeoutException;

    private readonly record struct StopwatchTimestamp(long Ticks)
    {
        internal static StopwatchTimestamp None => default;
        internal bool IsSet => Ticks != 0;
        internal static StopwatchTimestamp Now => new(System.Diagnostics.Stopwatch.GetTimestamp());
        internal static TimeSpan Elapsed(StopwatchTimestamp start) => System.Diagnostics.Stopwatch.GetElapsedTime(start.Ticks);
    }
}
