using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace URemote.Core;

// Direct .NET HTTP implementation; no Node, browser, JavaScript or WebView runtime.
public sealed class UuControllerApi : IDisposable
{
    private readonly HttpClient http;
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public LoginState State { get; private set; }

    public UuControllerApi(LoginState? state = null, HttpMessageHandler? handler = null)
    {
        State = state ?? new(ClientId: "u-remote-" + Guid.NewGuid().ToString("N")[..16], Uuid: Guid.NewGuid().ToString());
        http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = new("https://api.nrd.nie.163.com"), Timeout = TimeSpan.FromSeconds(25)
        };
    }

    public async Task InitializeControllerAsync(CancellationToken cancellationToken = default)
    {
        if (State.DeviceId.Length > 0) return;
        var profile = new
        {
            name = "U Remote Native Controller", client_id = State.ClientId,
            system_id = "u-remote-native-" + State.ClientId[Math.Max(0, State.ClientId.Length - 8)..], system_version = "15", gaid = "",
            install_id = State.Uuid, build_fingerprint = "google/shiba/shiba:15/AP3A.240905.015/release-keys",
            brand = "google", manufacturer = "Google", model = "Pixel 8", product = "shiba",
            rom = "Android", abi = "arm64-v8a", resolution = "1080x2400", screen_size = "1080x2400", dpi = 420
        };
        var result = await SendAsync(HttpMethod.Post, "/api/v1/device/android/init", profile, false, cancellationToken);
        var id = (result["data"] ?? result)["device_id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("Device initialization omitted device_id.");
        State = State with { DeviceId = id };
    }

    public async Task SendLoginCodeAsync(string mobile, string countryCode = "86", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mobile);
        await InitializeControllerAsync(cancellationToken);
        await SendAsync(HttpMethod.Post, "/api/v1/security/mobile/code",
            new { country_code = countryCode, mobile = mobile.Trim(), type = "login" }, false, cancellationToken);
    }

    public async Task LoginAsync(string mobile, string code, string countryCode = "86", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mobile);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        await InitializeControllerAsync(cancellationToken);
        var result = await SendAsync(HttpMethod.Post, "/api/v1/login/by_mobile",
            new { country_code = countryCode, mobile = mobile.Trim(), code = code.Trim() }, false, cancellationToken);
        var data = result["data"] ?? result;
        var token = data["token"]?.GetValue<string>();
        var userId = data["user_id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(userId))
            throw new InvalidDataException("Login response omitted credentials.");
        State = State with { Token = token, UserId = userId };
    }

    public Task<JsonNode> GetDevicesAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "/api/v1/device/groups/of/my", cancellationToken: ct);
    public Task<JsonNode> JoinDeviceAsync(string id, bool forceJoin = false, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/api/v1/room/join/by_device/" + DevicePath(id), new { force_join = forceJoin }, cancellationToken: ct);
    public Task<JsonNode> LeaveDeviceAsync(string id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/api/v1/room/clear/by_device/" + DevicePath(id), cancellationToken: ct);

    public async Task<JsonNode> SendAsync(HttpMethod method, string path, object? body = null,
        bool requireAuth = true, CancellationToken cancellationToken = default)
    {
        UuSigning.ValidatePath(path);
        if (requireAuth && !State.IsAuthenticated) throw new InvalidOperationException("Login required.");
        var text = body is null ? "" : JsonSerializer.Serialize(body, Json);
        using var request = new HttpRequestMessage(method, path);
        foreach (var header in UuSigning.BuildHeaders(State, method.Method, path, text))
            request.Headers.Add(header.Key, header.Value);
        if (body is not null) request.Content = new StringContent(text, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Login expired or access denied.");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"UU HTTP {(int)response.StatusCode}");
        var root = await JsonNode.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (root is not JsonObject) throw new InvalidDataException("Invalid UU response.");
        if (root["code"]?.GetValue<int>() != 0)
            throw new InvalidOperationException($"UU rejected request (code {root["code"]?.ToJsonString() ?? "missing"}).");
        return root;
    }

    private static string DevicePath(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Uri.EscapeDataString(id);
    }
    public void Dispose() => http.Dispose();
}
