using System.Text.Json;

namespace URemote.Core;

public sealed record UuDevice(string Id, string Name, string Category, int Platform, string Status,
    bool Controllable, bool ControlledSupport, string Version, bool IsCurrent)
{
    public bool Online => Status is "CONNECTED" or "ONLINE";
    public string PlatformName => IsCurrent && OperatingSystem.IsLinux() ? "Linux" : Platform switch
        { 1 => "Windows", 2 => "Android", 3 => "iOS", 4 => "macOS", _ => Category == "tv" ? "TV" : "未知平台" };
    public string StatusName => Status switch
        { "CONNECTED" or "ONLINE" => "在线", "DISCONNECTED" or "OFFLINE" => "离线", "SLEEP" => "休眠", "STANDBY" => "待机", _ => "状态未知" };
    public override string ToString() => $"UuDevice({Category}, {StatusName}; identity redacted)";
}

public static class UuDeviceCatalog
{
    // Mirrors the upstream device groups schema; do not retain arbitrary account response fields.
    public static IReadOnlyList<UuDevice> Parse(JsonElement root, string currentId)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid device catalog.");
        var result = new List<UuDevice>(); var seen = new HashSet<string>();
        foreach (var (key, category) in new[] { ("desktop_devices", "desktop"), ("mobile_devices", "mobile"), ("tv_devices", "tv") })
        {
            if (!data.TryGetProperty(key, out var entries)) continue;
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 10000) throw new InvalidDataException("Invalid device group.");
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var id = Text(entry, "device_id"); if (id.Length == 0 || !seen.Add(id)) continue;
                var name = Text(entry, "alias"); if (name.Length == 0) name = Text(entry, "name");
                var platform = Text(entry, "platform");
                result.Add(new(id, name.Length == 0 ? "未命名设备" : name, category,
                    int.TryParse(platform, out var number) ? number : 0, Text(entry, "status").ToUpperInvariant(),
                    Boolean(entry, "controllable"), Boolean(entry, "controlled_support"), Text(entry, "version_name"), id == currentId));
            }
        }
        return result;
    }
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var item)
        ? item.ValueKind == JsonValueKind.String ? new string((item.GetString() ?? "").Where(c => !char.IsControl(c)).Take(256).ToArray())
        : item.ValueKind == JsonValueKind.Number ? item.GetRawText() : "" : "";
    private static bool Boolean(JsonElement value, string key) => value.TryGetProperty(key, out var item)
        && (item.ValueKind == JsonValueKind.True || item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n) && n == 1
            || item.ValueKind == JsonValueKind.String && item.GetString() is "true" or "1");
}
