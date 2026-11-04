namespace URemote.Linux;

/// <summary>
/// Best-effort lock hint for xfce4-screensaver and light-locker. Any D-Bus or library failure means unlocked.
/// </summary>
internal static class X11LockProbe
{
    private static readonly (string Destination, string Path, string Interface)[] candidates =
    [
        ("org.xfce.ScreenSaver", "/org/xfce/ScreenSaver", "org.xfce.ScreenSaver"),
        ("org.xfce.ScreenSaver", "/org/xfce/ScreenSaver", "org.freedesktop.ScreenSaver"),
        ("org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver"),
        ("org.freedesktop.ScreenSaver", "/ScreenSaver", "org.freedesktop.ScreenSaver"),
        ("org.gnome.ScreenSaver", "/org/gnome/ScreenSaver", "org.gnome.ScreenSaver")
    ];

    internal static async Task<bool?> TryReadAsync(CancellationToken ct)
    {
        try
        {
            await using var bus = await PortalBus.ConnectAsync(ct).ConfigureAwait(false);
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var args = PortalValue.Tuple();
                    using var reply = await bus.CallAsync(candidate.Interface, "GetActive", args, ct, candidate.Path, candidate.Destination, 800).ConfigureAwait(false);
                    using var child = reply.Child(0);
                    return child.Boolean();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) when (IsProbeFailure(e)) { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (IsProbeFailure(e)) { }
        return null;
    }

    private static bool IsProbeFailure(Exception e) => e is IOException or InvalidOperationException or FormatException or NotSupportedException
        or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException or UnauthorizedAccessException or TimeoutException;
}
