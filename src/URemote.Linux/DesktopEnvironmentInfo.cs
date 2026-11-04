using System.Runtime.InteropServices;

namespace URemote.Linux;

public sealed record DesktopEnvironmentInfo(string Name, string SessionType)
{
    public string DisplayName => $"{Name} / {SessionType}";
    public static DesktopEnvironmentInfo Current => Detect(Environment.GetEnvironmentVariable, SameUserCommands);

    public static DesktopEnvironmentInfo Detect(Func<string, string?> read)
        => Detect(read, commands: null);

    /// <summary>
    /// <paramref name="commands"/> is consulted only when no desktop variable is set and the session is X11.
    /// Null skips that lookup so detection tests do not depend on the machine.
    /// </summary>
    public static DesktopEnvironmentInfo Detect(Func<string, string?> read, Func<IEnumerable<string>>? commands)
    {
        ArgumentNullException.ThrowIfNull(read);
        var desktop = read("XDG_CURRENT_DESKTOP");
        if (string.IsNullOrWhiteSpace(desktop)) desktop = read("XDG_SESSION_DESKTOP");
        if (string.IsNullOrWhiteSpace(desktop)) desktop = read("DESKTOP_SESSION");
        var names = (desktop ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = NameOf(names);
        var type = read("XDG_SESSION_TYPE")?.ToLowerInvariant();
        type = type switch { "wayland" => "Wayland", "x11" => "X11", _ => !string.IsNullOrEmpty(read("WAYLAND_DISPLAY")) ? "Wayland" : !string.IsNullOrEmpty(read("DISPLAY")) ? "X11" : "未知会话" };
        if (name == "未知桌面" && type == "X11" && commands?.Invoke().Any(IsXfceSession) == true)
            name = "XFCE";
        return new(name, type);
    }

    private static string NameOf(string[] names)
    {
        if (names.Any(n => Same(n, "KDE") || Same(n, "plasma"))) return "KDE Plasma";
        if (names.Any(n => Same(n, "GNOME"))) return "GNOME";
        if (names.Any(n => Same(n, "niri"))) return "niri";
        if (names.Any(n => Same(n, "XFCE"))) return "XFCE";
        if (names.Any(n => Same(n, "Cinnamon") || Same(n, "X-Cinnamon"))) return "Cinnamon";
        if (names.Any(n => Same(n, "MATE"))) return "MATE";
        if (names.Any(n => Same(n, "Unity"))) return "Unity";
        return names.Length > 0 ? string.Join(" · ", names) : "未知桌面";
    }

    private static bool Same(string value, string expected) => value.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsXfceSession(string command)
    {
        var name = Path.GetFileName(command.Trim());
        return name is "xfwm4" or "xfdesktop" or "xfce4-session";
    }

    private static IEnumerable<string> SameUserCommands()
    {
        if (!OperatingSystem.IsLinux()) yield break;
        uint uid;
        try { uid = geteuid(); }
        catch (DllNotFoundException) { yield break; }
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories("/proc").ToArray(); }
        catch (IOException) { yield break; }
        foreach (var dir in dirs)
        {
            if (!uint.TryParse(Path.GetFileName(dir), out _)) continue;
            string? command = null;
            uint? owner = null;
            try
            {
                foreach (var line in File.ReadLines(Path.Combine(dir, "status")))
                {
                    if (line.StartsWith("Name:", StringComparison.Ordinal)) command = line["Name:".Length..].Trim();
                    else if (line.StartsWith("Uid:", StringComparison.Ordinal))
                    {
                        var first = line["Uid:".Length..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                        if (uint.TryParse(first, out var id)) owner = id;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (command is not null && owner == uid) yield return command;
        }
    }

    [DllImport("libc")] private static extern uint geteuid();
}
