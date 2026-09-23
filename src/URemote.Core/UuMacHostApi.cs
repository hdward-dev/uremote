using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace URemote.Core;

// DeviceInfo fields/types recovered from official Mac Swift reflection metadata.
// A fresh Linux identity was accepted in an explicitly authorized initialization test.
public sealed record HostDeviceProfile(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("system_id")] string SystemId,
    [property: JsonPropertyName("system_version")] string SystemVersion,
    [property: JsonPropertyName("cpu")] string Cpu,
    [property: JsonPropertyName("memory")] string Memory,
    [property: JsonPropertyName("model_identifier")] string ModelIdentifier,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("model_number")] string ModelNumber,
    [property: JsonPropertyName("system_name")] string SystemName,
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("mac")] string Mac,
    [property: JsonPropertyName("resolution")] string Resolution,
    [property: JsonPropertyName("video")] string[] Video,
    [property: JsonPropertyName("dpi")] int Dpi)
{
    public override string ToString() => "HostDeviceProfile(identity redacted)";
}

public sealed class UuMacHostApi : IDisposable
{
    private readonly HttpClient http;
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public LoginState State { get; private set; }
    public UuMacHostApi(LoginState state, HttpMessageHandler? handler = null)
    {
        State = state;
        http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(25) };
    }

    public async Task InitializeAsync(HostDeviceProfile profile, CancellationToken ct = default)
    {
        if (State.DeviceId.Length > 0) throw new InvalidOperationException("This identity is already initialized.");
        if (string.IsNullOrWhiteSpace(profile.ClientId) || profile.ClientId != State.ClientId || string.IsNullOrWhiteSpace(profile.SystemId))
            throw new ArgumentException("A matching independently generated identity is required.");
        using var request = UuMacHostProtocol.BuildRequest(State, HttpMethod.Post, "/api/v1/device/macos/init", JsonSerializer.Serialize(profile, Json));
        using var doc = await SendAsync(request, ct);
        var data = doc.RootElement.GetProperty("data");
        if (!data.TryGetProperty("device_id", out var field) || field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString()))
            throw new InvalidDataException("Initialization did not return a device identity.");
        State = State with { DeviceId = field.GetString()! };
    }

    public async Task RefreshDeviceProfileAsync(HostDeviceProfile profile, CancellationToken ct = default)
    {
        RequireInitialized();
        if (profile.ClientId != State.ClientId || string.IsNullOrWhiteSpace(profile.SystemId)) throw new ArgumentException("Device identity mismatch.");
        using var request = UuMacHostProtocol.BuildRequest(State, HttpMethod.Post, "/api/v1/device/macos/init", JsonSerializer.Serialize(profile, Json));
        using var doc = await SendAsync(request, ct);
        if (doc.RootElement.GetProperty("data").GetProperty("device_id").GetString() != State.DeviceId)
            throw new InvalidOperationException("Metadata refresh returned a different device identity.");
    }

    public async Task<HostRoomConfiguration> CreateRoomAsync(long lastControlledInterval, CancellationToken ct = default)
    {
        using var request = UuMacHostProtocol.CreateRoomRequest(State, lastControlledInterval);
        using var response = await SendAsync(request, ct);
        return UuMacHostProtocol.ParseRoomResponse(response.RootElement.GetRawText());
    }

    public async Task<HostRoomConfiguration> JoinDeviceAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!State.IsAuthenticated) throw new InvalidOperationException("Login required.");
        using var request = UuMacHostProtocol.BuildRequest(State, HttpMethod.Post,
            "/api/v1/room/join/by_device/" + Uri.EscapeDataString(id), "{\"force_join\":false}");
        using var response = await SendAsync(request, ct);
        return UuMacHostProtocol.ParseRoomResponse(response.RootElement.GetRawText());
    }

    public async Task<AssistanceRoom> JoinAssistanceAsync(string connectId, string connectCode, CancellationToken ct = default)
    {
        AssistanceRequest.Validate(connectId, connectCode);
        if (!State.IsAuthenticated) throw new InvalidOperationException("Login required.");
        using var request = UuMacHostProtocol.BuildRequest(State, HttpMethod.Post, "/api/v2/room/join/share/by_code",
            JsonSerializer.Serialize(new { connect_id = connectId, connect_code = connectCode }));
        using var response = await SendAsync(request, ct);
        try { return AssistanceRoom.Parse(response.RootElement); }
        catch
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await CancelAssistanceAsync(connectId, deadline.Token); } catch { }
            throw;
        }
    }
    public async Task CancelAssistanceAsync(string connectId, CancellationToken ct = default)
    {
        using var request = UuMacHostProtocol.BuildRequest(State, HttpMethod.Post, "/api/v2/room/share/cancel_remote_assist",
            JsonSerializer.Serialize(new { connect_id = connectId }));
        using var response = await SendAsync(request, ct);
    }

    public async Task LeaveDeviceAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var request = UuMacHostProtocol.BuildRequest(State, HttpMethod.Post,
            "/api/v1/room/clear/by_device/" + Uri.EscapeDataString(id), "");
        using var response = await SendAsync(request, ct);
    }

    public async Task<HostAssistanceInfo> GetAssistanceInfoAsync(CancellationToken ct = default)
    {
        RequireInitialized();
        using var request = UuMacHostProtocol.BuildRequest(State, HttpMethod.Get, "/api/v1/device/share/info", "");
        using var response = await SendAsync(request, ct);
        var id = response.RootElement.GetProperty("data").GetProperty("connect_id").GetString();
        if (id is null || id.Length != 9 || !id.All(char.IsAsciiDigit)) throw new InvalidDataException("Invalid assistance identity.");
        return new(id);
    }
    public async Task AnswerAssistanceAsync(string controlId, string salt, CancellationToken ct)
    {
        if (controlId.Length is < 1 or > 256 || salt.Length is < 1 or > 1024) throw new ArgumentException("Invalid challenge.");
        var signatures = HostAssistance.Signatures(State.DeviceId, salt);
        using var request = UuMacHostProtocol.BuildRequest(State, HttpMethod.Post, "/api/v2/room/share/upload_sign",
            JsonSerializer.Serialize(new { can_remote_control = signatures.Allowed, control_id = controlId,
                sign = signatures.Primary, backup_sign = signatures.Backup }));
        using var response = await SendAsync(request, ct);
    }

    public async Task SetLocalDeviceNameAsync(string name, CancellationToken ct = default)
    {
        RequireInitialized();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var request = UuMacHostProtocol.BuildRequest(State, HttpMethod.Put,
            "/api/v1/device/" + Uri.EscapeDataString(State.DeviceId), JsonSerializer.Serialize(new { alias = name }, Json));
        using var response = await SendAsync(request, ct);
    }

    public async Task<IReadOnlyList<UuDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        if (!State.IsAuthenticated) throw new InvalidOperationException("Login required.");
        using var request = UuMacHostProtocol.BuildRequest(State, HttpMethod.Get, "/api/v1/device/groups/of/my", "");
        using var response = await SendAsync(request, ct);
        return UuDeviceCatalog.Parse(response.RootElement, State.DeviceId);
    }

    public async Task<(bool Controllable, string Availability)> GetHostAvailabilityAsync(CancellationToken ct = default)
    {
        using var request = UuMacHostProtocol.BuildRequest(State, HttpMethod.Get, "/api/v1/device/list", "");
        using var response = await SendAsync(request, ct);
        var current = response.RootElement.GetProperty("data").GetProperty("current_device");
        return (current.GetProperty("controllable").GetBoolean(),
            current.GetProperty("publisher_availability_status").GetString() ?? "unknown");
    }

    // Endpoint is present in the official Mac binary; field shape is verified against live server status.
    public async Task SetControllableAsync(bool enabled, CancellationToken ct = default)
    {
        using var request = UuMacHostProtocol.BuildRequest(State, HttpMethod.Post, "/api/v1/device/mac_controllable",
            JsonSerializer.Serialize(new { controllable = enabled }));
        using var response = await SendAsync(request, ct);
    }

    public async Task SendLoginCodeAsync(string mobile, string countryCode = "86", CancellationToken ct = default)
    {
        RequireInitialized();
        ValidateMobile(mobile, countryCode);
        using var request = UuMacHostProtocol.BuildRequest(State, HttpMethod.Post, "/api/v1/security/mobile/code",
            JsonSerializer.Serialize(new { country_code = countryCode, mobile, type = "login" }, Json));
        using var response = await SendAsync(request, ct);
    }

    public async Task LoginAsync(string mobile, string code, string countryCode = "86", CancellationToken ct = default)
    {
        RequireInitialized();
        ValidateMobile(mobile, countryCode);
        if (code.Length is < 4 or > 10 || !code.All(char.IsAsciiDigit))
            throw new ArgumentException("Invalid verification code format.", nameof(code));
        using var request = UuMacHostProtocol.BuildRequest(State, HttpMethod.Post, "/api/v1/login/by_mobile",
            JsonSerializer.Serialize(new { country_code = countryCode, mobile, code }, Json));
        using var response = await SendAsync(request, ct);
        var data = response.RootElement.GetProperty("data");
        if (!data.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(token.GetString())
            || !data.TryGetProperty("user_id", out var user) || user.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(user.GetString()))
            throw new InvalidDataException("Login did not return account authorization.");
        State = State with { Token = token.GetString()!, UserId = user.GetString()! };
    }

    private void RequireInitialized()
    {
        if (string.IsNullOrWhiteSpace(State.ClientId) || string.IsNullOrWhiteSpace(State.DeviceId))
            throw new InvalidOperationException("Initialize this device before account login.");
    }

    private static void ValidateMobile(string mobile, string countryCode)
    {
        if (countryCode.Length is < 1 or > 3 || !countryCode.All(char.IsAsciiDigit)
            || mobile.Length is < 5 or > 15 || !mobile.All(char.IsAsciiDigit) || mobile.Length + countryCode.Length > 15)
            throw new ArgumentException("Phone and country code must contain valid digits only.");
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"UU HTTP {(int)response.StatusCode}");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var document = JsonDocument.Parse(bytes);
        if (!document.RootElement.TryGetProperty("code", out var code) || code.GetInt32() != 0)
        {
            var safeCode = code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) ? value.ToString() : "missing";
            document.Dispose();
            throw new InvalidOperationException("UU rejected host request (code " + safeCode + ").");
        }
        return document;
    }
    public void Dispose() => http.Dispose();
}
