using System.Text.RegularExpressions;

namespace URemote.Linux;

public static class KdeWallpaper
{
    public static string? Resolve(string configRoot, IReadOnlyList<string> dataRoots)
    {
        var desktop = Read(Path.Combine(configRoot, "plasma-org.kde.plasma.desktop-appletsrc"));
        var globals = Read(Path.Combine(configRoot, "kdeglobals"));
        var color = Value(globals, "Colors:Window", "BackgroundNormal").Split(',');
        var dark = color.Length >= 3 && color.Take(3).All(c => int.TryParse(c, out _))
            && color.Take(3).Sum(c => int.Parse(c)) < 384;
        var activity = Value(Read(Path.Combine(configRoot, "kactivitymanagerdrc")), "activities", "currentActivity");
        var containments = desktop.Where(p => Regex.IsMatch(p.Key, @"^Containments/\d+$")
            && p.Value.GetValueOrDefault("plugin") != "org.kde.panel"
            && p.Value.GetValueOrDefault("wallpaperplugin") == "org.kde.image")
            .OrderByDescending(p => activity.Length > 0 && p.Value.GetValueOrDefault("activityId") == activity)
            .ThenBy(p => int.TryParse(p.Value.GetValueOrDefault("lastScreen"), out var screen) && screen >= 0 ? screen : int.MaxValue);
        foreach (var containment in containments)
        {
            var image = Value(desktop, containment.Key + "/Wallpaper/org.kde.image/General", "Image");
            if (image.Length == 0)
            {
                var theme = Value(globals, "KDE", "LookAndFeelPackage");
                if (theme.Length == 0) theme = "org.kde.breeze.desktop";
                if (Path.GetFileName(theme) != theme) continue;
                image = dataRoots.Select(root => Value(Read(Path.Combine(root, "plasma/look-and-feel", theme, "contents/defaults")), "Wallpaper", "Image"))
                    .FirstOrDefault(value => value.Length > 0) ?? "";
            }
            var resolved = ResolveImage(image, dark, dataRoots);
            if (resolved is not null) return resolved;
        }
        return null;
    }

    public static string? ResolveImage(string source, bool dark, IReadOnlyList<string> dataRoots)
    {
        if (source.Length == 0) return null;
        var fragment = source.LastIndexOf('#');
        if (fragment >= 0 && source[fragment..] is "#dark" or "#light" or "#day-night")
        {
            var mode = source[fragment..]; source = source[..fragment];
            if (mode == "#dark") dark = true;
            if (mode == "#light") dark = false;
            if (mode == "#day-night") dark = DateTime.Now.Hour is < 7 or >= 19;
        }
        if (source.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || !uri.IsFile || !uri.IsLoopback) return null;
            source = uri.LocalPath;
        }
        if (File.Exists(source) && Path.IsPathFullyQualified(source)) return source;
        var packages = Path.IsPathFullyQualified(source) ? new[] { source }
            : Path.GetFileName(source) == source ? dataRoots.Select(root => Path.Combine(root, "wallpapers", source)) : [];
        foreach (var package in packages)
        {
            foreach (var folder in dark ? new[] { "images_dark", "images" } : new[] { "images", "images_dark" })
            {
                var images = Path.Combine(package, "contents", folder);
                if (!Directory.Exists(images)) continue;
                var selected = Directory.EnumerateFiles(images)
                    .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".avif" or ".svg")
                    .OrderByDescending(path => IsPreviewAspect(Path.GetFileNameWithoutExtension(path)))
                    .ThenByDescending(path => ImageArea(Path.GetFileNameWithoutExtension(path)))
                    .ThenBy(path => path, StringComparer.Ordinal).FirstOrDefault();
                if (selected is not null) return selected;
            }
        }
        return null;
    }

    private static long ImageArea(string name)
    {
        var match = Regex.Match(name, @"^(\d{1,5})x(\d{1,5})$");
        return match.Success ? long.Parse(match.Groups[1].Value) * long.Parse(match.Groups[2].Value) : 0;
    }
    private static bool IsPreviewAspect(string name)
    {
        var match = Regex.Match(name, @"^(\d{1,5})x(\d{1,5})$");
        return match.Success && int.Parse(match.Groups[2].Value) > 0
            && Math.Abs(double.Parse(match.Groups[1].Value) / int.Parse(match.Groups[2].Value) - 16d / 9) < .02;
    }
    private static string Value(Dictionary<string, Dictionary<string, string>> values, string section, string key)
        => values.GetValueOrDefault(section)?.GetValueOrDefault(key) ?? "";
    private static Dictionary<string, Dictionary<string, string>> Read(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (!File.Exists(path) || new FileInfo(path).Length > 2_000_000) return result;
        Dictionary<string, string>? section = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = string.Join('/', Regex.Matches(line, @"\[([^\]]+)\]").Select(m => m.Groups[1].Value));
                if (!result.TryGetValue(name, out section)) result[name] = section = new(StringComparer.Ordinal);
                continue;
            }
            var separator = line.IndexOf('=');
            if (section is null || separator < 1) continue;
            var key = line[..separator].Replace("[$e]", "", StringComparison.Ordinal);
            var value = line[(separator + 1)..].Replace("\\s", " ", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
            section[key] = value;
        }
        return result;
    }
}
