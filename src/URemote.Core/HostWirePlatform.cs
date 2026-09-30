using System.Globalization;
using System.Text.Json;

namespace URemote.Core;

// Windows fields verified against the official 4.42.0.2770 client in our own VM.
// The mac override is a rollback path for existing installations, not a second identity.
public static class HostWirePlatform
{
    public static bool Windows { get; } = Environment.GetEnvironmentVariable("UREMOTE_HOST_PLATFORM") switch
    {
        "windows" => true,
        null or "" or "mac" => false,
        _ => throw new InvalidOperationException("UREMOTE_HOST_PLATFORM must be windows or mac.")
    };
    public static string InitPath => Windows ? "/api/v1/device/windows/init" : "/api/v1/device/macos/init";
    public static string ControllablePath => Windows ? "/api/v1/device/controllable" : "/api/v1/device/mac_controllable";
    public static string SerializeProfile(HostDeviceProfile profile, bool controllable, JsonSerializerOptions options)
    {
        if (!Windows) return JsonSerializer.Serialize(profile, options);
        if (!long.TryParse(profile.Memory, NumberStyles.None, CultureInfo.InvariantCulture, out var memory) || memory < 0)
            throw new ArgumentException("Device memory must be an integer number of MiB.");
        return JsonSerializer.Serialize(new
        {
            base_board = profile.ModelIdentifier,
            client_id = profile.ClientId,
            controllable,
            cpu = profile.Cpu,
            mac = profile.Mac,
            machine_guid = profile.SystemId,
            memory,
            name = profile.Name,
            os = profile.SystemVersion,
            platform = 1,
            screen = profile.Resolution,
            system_id = profile.SystemId,
            video = profile.Video
        }, options);
    }
}
