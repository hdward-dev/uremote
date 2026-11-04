using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace URemote.Linux;

/// <summary>
/// One wallpaper resolver for the desktop <see cref="DesktopEnvironmentInfo"/> already selected.
/// XFCE reads xfce4-desktop. GNOME, Cinnamon, MATE and Unity read gsettings.
/// A desktop with no configured wallpaper returns nothing; the card keeps its placeholder.
/// </summary>
internal static class X11Wallpaper
{
    internal readonly record struct Source(string? Image, string? Color, string? SecondColor, bool Vertical);
    internal sealed record Slot(string Monitor, int? Workspace, int? ImageStyle, int? ColorStyle, string? Image, string? Color, string? SecondColor);

    internal static Source? ResolveXfce(string configRoot)
    {
        var hint = ReadHint();
        var queried = ReadQuery();
        if (queried is not null && Select(ParseQuery(queried), hint.Output, hint.Workspace, ImageExists) is { } fromQuery)
            return fromQuery;
        var xml = Path.Combine(configRoot, "xfce4", "xfconf", "xfce-perchannel-xml", "xfce4-desktop.xml");
        if (!File.Exists(xml) || new FileInfo(xml).Length is <= 0 or > 2_000_000) return null;
        return Select(ParseXml(File.ReadAllText(xml)), hint.Output, hint.Workspace, ImageExists);
    }

    internal static Source? ResolveGsettings(string desktop)
    {
        var path = desktop switch
        {
            "Cinnamon" => Gsettings("org.cinnamon.desktop.background", "picture-uri"),
            "MATE" => Gsettings("org.mate.background", "picture-filename"),
            _ => GnomePicture()
        };
        return path is not null && ImageExists(path) ? new Source(path, null, null, false) : null;
    }

    internal static Source? Select(IReadOnlyList<Slot> slots, string? output, int? workspace, Func<string, bool> exists)
    {
        foreach (var slot in slots.OrderByDescending(slot => Score(slot, output, workspace)))
        {
            if (slot.ImageStyle == 0)
            {
                if (slot.Color is not null) return new Source(null, slot.Color, slot.ColorStyle is 1 or 2 ? slot.SecondColor : null, slot.ColorStyle == 2);
                continue;
            }
            if (slot.Image is { Length: > 0 } image && exists(image)) return new Source(image, null, null, false);
        }
        return null;
    }

    internal static IReadOnlyList<Slot> ParseXml(string text)
    {
        XDocument document;
        try { document = XDocument.Parse(text); }
        catch (System.Xml.XmlException) { return []; }
        var backdrop = document.Descendants("property").FirstOrDefault(item => Attr(item, "name") == "backdrop");
        if (backdrop is null) return [];
        var slots = new List<Slot>();
        foreach (var screen in Elements(backdrop))
        {
            if (!Attr(screen, "name").StartsWith("screen", StringComparison.Ordinal)) continue;
            foreach (var monitor in Elements(screen))
            {
                var monitorName = Attr(monitor, "name");
                if (!monitorName.StartsWith("monitor", StringComparison.Ordinal)) continue;
                var workspaces = Elements(monitor).Where(item => Attr(item, "name").StartsWith("workspace", StringComparison.Ordinal)).ToArray();
                if (workspaces.Length == 0) slots.Add(ReadSlot(monitor, monitorName, null));
                else foreach (var item in workspaces) slots.Add(ReadSlot(item, monitorName, Workspace(Attr(item, "name"))));
            }
        }
        return slots;
    }

    internal static IReadOnlyList<Slot> ParseQuery(string text)
    {
        var grouped = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var match = Regex.Match(raw.Trim(), @"^(?<path>/backdrop/\S+)\s+(?<value>\S.*)$");
            if (!match.Success) continue;
            var path = match.Groups["path"].Value;
            var slash = path.LastIndexOf('/');
            if (slash <= 0) continue;
            var value = match.Groups["value"].Value.Trim();
            if (value.Length == 0 || value.Contains("UNSUPPORTED", StringComparison.Ordinal)) continue;
            if (!grouped.TryGetValue(path[..slash], out var fields)) grouped[path[..slash]] = fields = new(StringComparer.Ordinal);
            fields[path[(slash + 1)..]] = value;
        }
        var slots = new List<Slot>();
        foreach (var (parent, fields) in grouped)
        {
            var parts = parent.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var monitor = parts.LastOrDefault(part => part.StartsWith("monitor", StringComparison.Ordinal)) ?? "";
            if (monitor.Length == 0) continue;
            var workspaceName = parts.LastOrDefault(part => part.StartsWith("workspace", StringComparison.Ordinal));
            slots.Add(new Slot(monitor, workspaceName is null ? null : Workspace(workspaceName), Int(fields, "image-style"), Int(fields, "color-style"),
                First(fields, "last-image", "image-path"), ColorOf(fields.GetValueOrDefault("rgba1") ?? fields.GetValueOrDefault("color1")),
                ColorOf(fields.GetValueOrDefault("rgba2") ?? fields.GetValueOrDefault("color2"))));
        }
        return slots;
    }

    internal static string? ParseGsettings(string text)
    {
        var value = text.Trim();
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') value = value[1..^1].Replace("\\'", "'", StringComparison.Ordinal);
        if (value.Length == 0) return null;
        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile || !uri.IsLoopback) return null;
            return uri.LocalPath;
        }
        return Path.IsPathFullyQualified(value) ? value : null;
    }

    internal static async Task<byte[]?> RenderColorAsync(string ffmpeg, string color, string? second, bool vertical, CancellationToken ct)
    {
        if (!IsHex(color) || (second is not null && !IsHex(second))) return null;
        var scratch = Directory.CreateTempSubdirectory("uremote-wallpaper-").FullName;
        try
        {
            var output = Path.Combine(scratch, "wallpaper.png");
            if (second is null)
                await LinuxWallpaper.RunTool(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i", "color=c=" + color.Replace("#", "0x") + ":s=960x540", "-frames:v", "1", output], ct);
            else
            {
                var axis = vertical ? "Y/H" : "X/W";
                await LinuxWallpaper.RunTool(ffmpeg, ["-hide_banner", "-loglevel", "error", "-nostdin", "-f", "lavfi", "-i", "color=c=" + color.Replace("#", "0x") + ":s=960x540",
                    "-f", "lavfi", "-i", "color=c=" + second.Replace("#", "0x") + ":s=960x540", "-filter_complex", "blend=all_expr='A*(1-" + axis + ")+B*(" + axis + ")'", "-frames:v", "1", output], ct);
            }
            var bytes = await File.ReadAllBytesAsync(output, ct);
            return bytes.Length is > 0 and <= 5_000_000 ? bytes : null;
        }
        finally { Directory.Delete(scratch, true); }
    }

    private static int Score(Slot slot, string? output, int? workspace)
    {
        var score = 0;
        if (!string.IsNullOrEmpty(output) && slot.Monitor.Equals("monitor" + output, StringComparison.OrdinalIgnoreCase)) score += 100;
        if (workspace is int current && slot.Workspace == current) score += 10;
        else if (workspace is null && slot.Workspace == 0) score += 1;
        return score;
    }

    private static (string? Output, int? Workspace) ReadHint()
    {
        if (!LinuxSessionKind.UseX11()) return default;
        try
        {
            using var connection = X11Connection.Open(null);
            return connection.WallpaperHint();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or FormatException or DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        { return default; }
    }

    private static string? ReadQuery()
    {
        var exe = Which("xfconf-query");
        return exe is null ? null : RunText(exe, ["-c", "xfce4-desktop", "-l", "-v"]);
    }

    private static string? GnomePicture()
    {
        var scheme = GsettingsRaw("org.gnome.desktop.interface", "color-scheme");
        if (scheme is not null && scheme.Contains("prefer-dark", StringComparison.Ordinal) && Gsettings("org.gnome.desktop.background", "picture-uri-dark") is { } dark)
            return dark;
        return Gsettings("org.gnome.desktop.background", "picture-uri");
    }

    private static string? Gsettings(string schema, string key) => ParseGsettings(GsettingsRaw(schema, key) ?? "");

    private static string? GsettingsRaw(string schema, string key)
    {
        var exe = Which("gsettings");
        return exe is null ? null : RunText(exe, ["get", schema, key]);
    }

    private static string? Which(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string? RunText(string exe, string[] args)
    {
        using var process = new Process { StartInfo = new(exe) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000)) { process.Kill(true); return null; }
            var text = output.GetAwaiter().GetResult();
            return process.ExitCode == 0 && text.Length is > 0 and < 1_000_000 ? text : null;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!process.HasExited) try { process.Kill(true); } catch (InvalidOperationException) { }
            return null;
        }
    }

    private static bool ImageExists(string path)
    {
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile && uri.IsLoopback)
            path = uri.LocalPath;
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return false;
        try { return new FileInfo(path).Length is > 0 and <= 100_000_000; }
        catch (IOException) { return false; }
    }

    private static Slot ReadSlot(XElement node, string monitor, int? workspace)
    {
        string? value = null, color = null, second = null;
        int? imageStyle = null, colorStyle = null;
        foreach (var child in Elements(node))
        {
            var name = Attr(child, "name");
            if (name is "last-image" or "image-path") value ??= Attr(child, "value");
            else if (name == "image-style" && int.TryParse(Attr(child, "value"), out var image)) imageStyle = image;
            else if (name == "color-style" && int.TryParse(Attr(child, "value"), out var style)) colorStyle = style;
            else if (name is "rgba1" or "color1") color = ColorOf(child);
            else if (name is "rgba2" or "color2") second = ColorOf(child);
        }
        return new Slot(monitor, workspace, imageStyle, colorStyle, string.IsNullOrWhiteSpace(value) ? null : value, color, second);
    }

    private static string? ColorOf(XElement node)
    {
        var values = node.Elements("value").Select(item => Attr(item, "value")).ToArray();
        if (values.Length == 0 && Attr(node, "value") is { Length: > 0 } single) return ColorOf(single);
        return ColorOf(string.Join(',', values));
    }

    private static string? ColorOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split([',', ' ', '[', ']'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var packed))
        {
            if (packed > 0xFFFFFF) packed &= 0xFFFFFF;
            return "#" + packed.ToString("X6", CultureInfo.InvariantCulture);
        }
        if (parts.Length < 3) return null;
        var channels = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || double.IsNaN(number) || double.IsInfinity(number))
                return null;
            if (number > 1) number /= number <= 255 ? 255 : 65535;
            if (number is < 0 or > 1) return null;
            channels[i] = (int)Math.Round(number * 255);
        }
            return $"#{channels[0]:X2}{channels[1]:X2}{channels[2]:X2}";
    }

    private static int? Int(Dictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out var value) && int.TryParse(value, out var number) ? number : null;

    private static string? First(Dictionary<string, string> fields, params string[] keys)
    {
        foreach (var key in keys)
            if (fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
        return null;
    }

    private static int? Workspace(string name)
        => name.StartsWith("workspace", StringComparison.Ordinal) && int.TryParse(name["workspace".Length..], out var index) ? index : null;

    private static IEnumerable<XElement> Elements(XElement node) => node.Elements("property");
    private static string Attr(XElement node, string name) => (string?)node.Attribute(name) ?? "";
    private static bool IsHex(string color) => Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$");
}
