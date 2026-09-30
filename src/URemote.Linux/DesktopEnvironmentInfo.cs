namespace URemote.Linux;

public sealed record DesktopEnvironmentInfo(string Name, string SessionType)
{
    public string DisplayName => $"{Name} / {SessionType}";
    public static DesktopEnvironmentInfo Current => Detect(Environment.GetEnvironmentVariable);

    public static DesktopEnvironmentInfo Detect(Func<string, string?> read)
    {
        var desktop = read("XDG_CURRENT_DESKTOP");
        if (string.IsNullOrWhiteSpace(desktop)) desktop = read("XDG_SESSION_DESKTOP");
        if (string.IsNullOrWhiteSpace(desktop)) desktop = read("DESKTOP_SESSION");
        var names = (desktop ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = names.Any(n => n.Equals("KDE", StringComparison.OrdinalIgnoreCase) || n.Equals("plasma", StringComparison.OrdinalIgnoreCase)) ? "KDE Plasma"
            : names.Any(n => n.Equals("niri", StringComparison.OrdinalIgnoreCase)) ? "niri"
            : names.Length > 0 ? string.Join(" · ", names) : "未知桌面";
        var type = read("XDG_SESSION_TYPE")?.ToLowerInvariant();
        type = type switch { "wayland" => "Wayland", "x11" => "X11", _ => !string.IsNullOrEmpty(read("WAYLAND_DISPLAY")) ? "Wayland" : !string.IsNullOrEmpty(read("DISPLAY")) ? "X11" : "未知会话" };
        return new(name, type);
    }
}
