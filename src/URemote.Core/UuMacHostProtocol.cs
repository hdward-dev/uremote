using System.Globalization;
using System.Text;
using System.Text.Json;

namespace URemote.Core;

// Official macOS wire compatibility: 4.41.0 (622), including terminal negotiation.
// No network calls are made here. Do not reuse a running official device identity.
public static class UuMacHostProtocol
{
    public static HttpRequestMessage CreateRoomRequest(LoginState state, long lastControlledInterval,
        long? timestamp = null, Guid? nonce = null)
    {
        if (!state.IsAuthenticated || string.IsNullOrWhiteSpace(state.ClientId))
            throw new ArgumentException("An initialized, authenticated host identity is required.", nameof(state));
        ArgumentOutOfRangeException.ThrowIfNegative(lastControlledInterval);
        const string path = "/api/v1/room/create";
        var body = JsonSerializer.Serialize(new { last_controlled_interval = lastControlledInterval });
        return BuildRequest(state, HttpMethod.Post, path, body, timestamp, nonce);
    }

    internal static HttpRequestMessage BuildRequest(LoginState state, HttpMethod method, string path,
        string body, long? timestamp = null, Guid? nonce = null)
    {
        UuSigning.ValidatePath(path);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Param-PLAT"] = "4", ["X-Param-CHN"] = "gwqd",
            ["X-Param-PKGN"] = "com.netease.uuremote", ["X-Param-VC"] = "622",
            ["X-Param-VN"] = "4.41.0", ["X-Param-OPR"] = "None",
            ["X-Param-ENT"] = "", ["X-Param-REL"] = "prod",
            ["X-Param-CNT"] = "CN", ["X-Param-LANG"] = "zh-CN",
            ["X-Param-client-id"] = state.ClientId, ["X-Param-device-id"] = state.DeviceId,
            ["X-Param-user-id"] = state.UserId,
            ["X-Param-TS"] = (timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(CultureInfo.InvariantCulture),
            ["X-Param-NONCE"] = (nonce ?? Guid.NewGuid()).ToString()
        };
        headers["X-Param-SIGN"] = UuSigning.ComputeSignature(headers, method.Method, path, body);
        var request = new HttpRequestMessage(method, "https://api.nrd.nie.163.com" + path);
        foreach (var header in headers) request.Headers.Add(header.Key, header.Value);
        if (state.Token.Length > 0) request.Headers.Authorization = new("Bearer", state.Token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    public static HostRoomConfiguration ParseRoomResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("code", out var code) || code.GetInt32() != 0)
            throw new InvalidDataException("UU did not accept room creation.");
        var data = root.GetProperty("data");
        string Text(string key) => data.GetProperty(key).GetString()
            ?? throw new InvalidDataException("Missing room configuration field.");
        var signaling = data.GetProperty("signaling_list").EnumerateArray()
            .Select(x => SecureUri(x.GetString() ?? "", "wss")).ToArray();
        return new(SecureUri(Text("signaling_server"), "wss"), signaling, Text("token"),
            data.GetProperty("ws_connect_timeout_ms").GetInt32(),
            data.GetProperty("max_reconnect_delta").GetInt32(), Text("report_token"),
            SecureUri(Text("report_url"), "https"), Text("report_server_address"),
            data.GetProperty("streamer_retry_delta_ms").GetInt32(),
            data.GetProperty("international_connect").GetBoolean());
    }

    private static Uri SecureUri(string text, string scheme)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != scheme
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException("Invalid room service URL.");
        return uri;
    }
}

public sealed record HostRoomConfiguration(Uri SignalingServer, IReadOnlyList<Uri> SignalingList,
    string Token, int WebSocketConnectTimeoutMs, int MaxReconnectDelta, string ReportToken,
    Uri ReportUrl, string ReportServerAddress, int StreamerRetryDeltaMs, bool InternationalConnect)
{
    public override string ToString() => "HostRoomConfiguration(credentials and endpoints redacted)";
}
