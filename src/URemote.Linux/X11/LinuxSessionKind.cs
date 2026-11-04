namespace URemote.Linux;

/// <summary>
/// Wayland when the session says so. X11 only when a display is set and Wayland is not.
/// An XWayland session still has <c>WAYLAND_DISPLAY</c> and stays on the portal path.
/// </summary>
public static class LinuxSessionKind
{
    public enum Desktop { Wayland, X11, None }

    public static Desktop Current => Detect(Environment.GetEnvironmentVariable);

    public static bool UseX11() => OperatingSystem.IsLinux() && Current == Desktop.X11;

    public static Desktop Detect(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var session = read("XDG_SESSION_TYPE");
        var wayland = read("WAYLAND_DISPLAY");
        if (string.Equals(session, "wayland", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(wayland))
            return Desktop.Wayland;
        if (!string.IsNullOrWhiteSpace(read("DISPLAY")))
            return Desktop.X11;
        return Desktop.None;
    }
}
