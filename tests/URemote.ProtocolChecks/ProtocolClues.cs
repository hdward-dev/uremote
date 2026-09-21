using System.Text.RegularExpressions;

namespace URemote.Probe;

// Allow-list extraction only: never return raw lines, token values, identifiers,
// addresses, arbitrary JSON keys or filenames supplied by log content.
public static partial class ProtocolClues
{
    private static readonly string[] KnownEvents =
    [
        "be-controlled", "ControlledCreateRoom", "CreateRoomConfig", "createRoomRefetch",
        "connect_signaling", "publisher_disconnect", "device_capability", "forward_setting",
        "onPeerConnectionState", "onSignalPush", "X-NRD-AUTH", "X-NRD-CONTROLLING",
        "X-NRD-RECONN-KEY", "X-Param-H", "X-Param-M", "X-Param-L", "X-Param-SIGN"
    ];
    private static readonly HashSet<string> KnownKeys =
    [
        "token", "device_id", "client_id", "user_id", "signaling_list", "signaling_server",
        "ice_id", "iceServers", "app_control_id", "streamer_data", "app_data", "type",
        "sdp", "candidate", "platform", "controlled_support", "controllable", "code",
        "reconn_key", "streamer_flag", "device_capability", "status", "data", "msg",
        "publisher_platform", "publisher_availability_status", "report_token", "room_id"
    ];
    private static readonly string[] Routes =
    [
        "/api/v1/device/macos/init", "/api/v1/device/android/init", "/api/v1/device/mac_controllable",
        "/api/v1/room/create/refetch", "/api/v1/room/create", "/api/v1/guest/room/create",
        "/api/v1/room/join/refetch", "/api/v1/device/groups/of/my", "/api/v1/device/list",
        "/api/v1/device/share/info", "/api/v1/login/by_mobile", "/api/v1/login/by_qrcode"
    ];

    public static Clues Extract(string text) => new(
        Routes.Where(route => text.Contains(route, StringComparison.Ordinal)).ToArray(),
        KnownEvents.Where(evt => text.Contains(evt, StringComparison.Ordinal)).ToArray(),
        FieldRegex().Matches(text).Select(m => m.Groups[1].Value).Where(KnownKeys.Contains).Distinct().Order().ToArray());

    [GeneratedRegex("\"([a-zA-Z_][a-zA-Z0-9_]*)\"\\s*:")]
    private static partial Regex FieldRegex();
}

public sealed record Clues(string[] Routes, string[] Events, string[] FieldNames);
