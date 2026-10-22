using System.Threading.Channels;

namespace URemote.Linux;

// Lock/blank leaves the existing KWin screencast node on black frames. Unlock has to drop that lease.
internal static class PortalSessionActivity
{
    internal static async Task WatchAsync(Action<string> onTransition, Action<string> report, CancellationToken ct)
    {
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
            bool? active = null;
            var id = await bus.SubscribeAnyAsync(null, "org.freedesktop.ScreenSaver", "ActiveChanged", null, value =>
            {
                try
                {
                    value.Require("(b)");
                    using var state = value.Child(0);
                    var now = state.Boolean();
                    if (active == true && !now) writer.TryWrite("unblank");
                    active = now;
                }
                catch { /* Ignore malformed screensaver signals. */ }
            });
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { try { await bus.UnsubscribeAsync(id); } catch (IOException) { } }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or InvalidOperationException or FormatException or NotSupportedException
            or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException or UnauthorizedAccessException or TimeoutException)
        { report("session-activity-watch-unavailable;source=screensaver;type=" + e.GetType().Name); }
    }

    private static async Task WatchLoginAsync(ChannelWriter<string> writer, Action<string> report, CancellationToken ct)
    {
        try
        {
            await using var bus = await PortalBus.ConnectSystemAsync(ct);
            using var pid = PortalValue.UInt((uint)Environment.ProcessId);
            using var args = PortalValue.Tuple(pid);
            using var reply = await bus.CallAsync("org.freedesktop.login1.Manager", "GetSessionByPID", args, ct,
                "/org/freedesktop/login1", "org.freedesktop.login1");
            using var pathValue = reply.Child(0);
            var path = pathValue.Text();
            if (!path.StartsWith("/org/freedesktop/login1/session/", StringComparison.Ordinal))
                throw new FormatException("无效的登录会话。");
            bool? locked = null;
            try
            {
                using var iface = PortalValue.String("org.freedesktop.login1.Session");
                using var name = PortalValue.String("LockedHint");
                using var get = PortalValue.Tuple(iface, name);
                using var current = await bus.CallAsync("org.freedesktop.DBus.Properties", "Get", get, ct, path, "org.freedesktop.login1");
                using var field = current.Child(0);
                using var boxed = field.Type == "v" ? field.Unbox() : null;
                locked = (boxed ?? field).Boolean();
            }
            catch (Exception e) when (e is IOException or FormatException or InvalidOperationException) { }
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
                    if (locked == true && !now) writer.TryWrite("unlock");
                    locked = now;
                }
                catch { /* Ignore unrelated property changes. */ }
            });
            var unlock = await bus.SubscribeAnyAsync("org.freedesktop.login1", "org.freedesktop.login1.Session", "Unlock", path, _ => writer.TryWrite("unlock"));
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally
            {
                try { await bus.UnsubscribeAsync(properties); } catch (IOException) { }
                try { await bus.UnsubscribeAsync(unlock); } catch (IOException) { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or InvalidOperationException or FormatException or NotSupportedException
            or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException or UnauthorizedAccessException or TimeoutException)
        { report("session-activity-watch-unavailable;source=login1;type=" + e.GetType().Name); }
    }

    private readonly record struct StopwatchTimestamp(long Ticks)
    {
        internal static StopwatchTimestamp None => default;
        internal bool IsSet => Ticks != 0;
        internal static StopwatchTimestamp Now => new(System.Diagnostics.Stopwatch.GetTimestamp());
        internal static TimeSpan Elapsed(StopwatchTimestamp start) => System.Diagnostics.Stopwatch.GetElapsedTime(start.Ticks);
    }
}
