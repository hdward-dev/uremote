using System.Security.Cryptography;
using System.Text;

namespace URemote.Core;

// Protocol port from iola1999/uurc-web, MIT, commit 7d1cccb.
// This is the upstream Android CONTROLLER identity, not a publisher/host identity.
public sealed record LoginState(string Token = "", string UserId = "", string ClientId = "",
    string DeviceId = "", string Oaid = "", string Uuid = "", string Channel = "nochannel")
{
    public bool IsAuthenticated => Token.Length > 0 && UserId.Length > 0 && DeviceId.Length > 0;
    public override string ToString() => $"LoginState(Authenticated={IsAuthenticated}, credentials redacted)";
}

public sealed record ControllerFingerprint(string VersionName = "4.39.1", string VersionCode = "439100",
    string HttpPlatform = "2");

public static class UuSigning
{
    private const string SigningKey = "alWiSzXZTLu3WfFnw13uBru3";
    private static readonly HashSet<string> V2Paths =
    [
        "/api/v2/room/join/share/by_code", "/api/v2/room/join/share/by_confirmation",
        "/api/v2/room/share/upload_sign", "/api/v2/room/share/control_mode", "/api/v2/room/share/cancel_remote_assist"
    ];

    public static void ValidatePath(string path)
    {
        var pathOnly = path.Split('?')[0];
        var decoded = Uri.UnescapeDataString(pathOnly);
        if ((!path.StartsWith("/api/v1/", StringComparison.Ordinal) && !V2Paths.Contains(pathOnly)) ||
            decoded.Contains("..", StringComparison.Ordinal) || path.Contains('#') ||
            decoded.Contains('\\') || path.Any(char.IsControl))
            throw new ArgumentException("Unsupported UU API path.", nameof(path));
    }

    public static Dictionary<string, string> BuildHeaders(LoginState state, string method, string path,
        string body = "", long? timestamp = null, ControllerFingerprint? fingerprint = null)
    {
        ValidatePath(path);
        fingerprint ??= new();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Param-CHN"] = state.Channel, ["X-Param-OPR"] = "",
            ["X-Param-PKGN"] = "com.netease.uuremote", ["X-Param-PLAT"] = fingerprint.HttpPlatform,
            ["X-Param-REL"] = "prod", ["X-Param-VC"] = fingerprint.VersionCode,
            ["X-Param-VN"] = fingerprint.VersionName, ["X-Param-ABI"] = "arm64-v8a",
            ["X-Param-client-id"] = state.ClientId, ["X-Param-device-id"] = state.DeviceId,
            ["X-Param-user-id"] = state.UserId, ["X-Param-OAID"] = state.Oaid,
            ["X-Param-TS"] = (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["X-Param-CNT"] = "CN", ["X-Param-LANG"] = "zh-CN"
        };
        headers["X-Param-SIGN"] = ComputeSignature(headers, method, path, body);
        if (state.Token.Length > 0) headers["Authorization"] = "Bearer " + state.Token;
        return headers;
    }

    // Also verified offline against an authorized official macOS room/create capture.
    public static string ComputeSignature(IReadOnlyDictionary<string, string> headers,
        string method, string path, string body)
    {
        ValidatePath(path);
        var canonical = string.Join('&', headers
            .Where(x => x.Key.StartsWith("x-param-", StringComparison.OrdinalIgnoreCase)
                && !x.Key.Equals("x-param-sign", StringComparison.OrdinalIgnoreCase))
            .Select(x => (Key: x.Key.ToLowerInvariant(), x.Value))
            .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}"));
        var bytes = Encoding.UTF8.GetBytes(method.ToUpperInvariant() + path + canonical + body);
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SigningKey), bytes));
    }
}
