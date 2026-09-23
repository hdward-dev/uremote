using System.Diagnostics;
using System.Text.Json.Nodes;

namespace URemote.Core;

public sealed record HostControlConnection(string DeviceId, int Platform, string Location, bool Assistance,
    int CaptureType, DateTimeOffset ConnectedAt, long StartedTimestamp)
{
    public bool HasInputActivity { get; init; }
    public bool ViewOnly { get; init; }
    public Action? RequestDisconnect { get; init; }
    public string PlatformName => Platform switch { 1 => "Windows", 2 => "Android", 3 => "iOS", 4 => "macOS", _ => "远程设备" };
    public string SessionName => CaptureType switch { 8 => "远程终端", 5 => "文件传输", _ => "远程控制" };
    public string ElapsedText => FormatElapsed(Stopwatch.GetElapsedTime(StartedTimestamp));
    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return $"{(long)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
    public static HostControlConnection FromRequest(JsonObject request, StreamerConnectOptions options)
    {
        static string Clean(string value) => new(value.Where(c => !char.IsControl(c) && c is not ('\u202a' or '\u202b' or '\u202d' or '\u202e' or '\u202c')).Take(96).ToArray());
        static string Read(JsonObject value, string key) => value[key] is JsonValue v && v.TryGetValue<string>(out var text) ? text : "";
        int.TryParse(request["controller_platform"]?.ToString(), out var platform);
        var location = string.Join(" · ", new[] { "subscriber_country", "subscriber_province", "subscriber_city" }
            .Select(key => Clean(Read(request, key))).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
        return new(Clean(options.DeviceId), platform, location, options.ControlConnectType == 2,
            options.CaptureType, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
    }
    public override string ToString() => "HostControlConnection(identity redacted)";
}
