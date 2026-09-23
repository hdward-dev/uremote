using System.Net;
using System.Text.Json;
using URemote.Core;

static class HostApiTests
{
    public static async Task Run(Action<bool, string> check)
    {
        using var handler = new HostInitHandler();
        using var api = new UuMacHostApi(new(ClientId: "fixture-client", Channel: "gwqd"), handler);
        var profile = new HostDeviceProfile("fixture-host", "fixture-client", "fixture-system", "fixture-version",
            "X64", "1000", "fixture-model", "fixture-model", "", "Linux", "Linux", "", "", [], 96);
        await api.InitializeAsync(profile);
        check(handler.Requests == 1 && api.State.DeviceId == "fixture-device", "host initialization uses independent identity and parses server device id");
        check(!api.State.IsAuthenticated, "initialization is not mistaken for account login");
        try { await api.InitializeAsync(profile); check(false, "initialized host cannot be accidentally reinitialized"); }
        catch (InvalidOperationException) { check(handler.Requests == 1, "initialized host cannot be accidentally reinitialized"); }
        using var invalid = new UuMacHostApi(new(ClientId: "different-client"), handler);
        try { await invalid.InitializeAsync(profile); check(false, "mismatched initialization identity rejected"); }
        catch (ArgumentException) { check(handler.Requests == 1, "mismatched initialization identity rejected"); }
        using var loginHandler = new HostLoginHandler();
        using var loginApi = new UuMacHostApi(new(ClientId: "fixture-client", DeviceId: "fixture-device", Channel: "gwqd"), loginHandler);
        await loginApi.SendLoginCodeAsync("15500000000");
        check(loginHandler.Requests == 1 && !loginApi.State.IsAuthenticated, "SMS request uses initialized host identity without authenticating it");
        try { await loginApi.LoginAsync("15500000000", "not-a-code"); check(false, "invalid code rejected locally"); }
        catch (ArgumentException) { check(loginHandler.Requests == 1, "invalid code rejected locally"); }
        await loginApi.LoginAsync("15500000000", "123456");
        check(loginApi.State.IsAuthenticated && loginApi.State.Token == "fixture-token" && loginApi.State.DeviceId == "fixture-device",
            "mobile login retains independent device identity and saves returned authorization");
        using var availabilityHandler = new HostAvailabilityHandler();
        using var availabilityApi = new UuMacHostApi(loginApi.State, availabilityHandler);
        var initialAvailability = await availabilityApi.GetHostAvailabilityAsync();
        check(!initialAvailability.Controllable && initialAvailability.Availability == "control_off", "publisher online does not imply controllable");
        await availabilityApi.SetControllableAsync(true);
        check((await availabilityApi.GetHostAvailabilityAsync()).Controllable, "explicit host switch enables controllability");
        await availabilityApi.SetControllableAsync(false);
        check(!(await availabilityApi.GetHostAvailabilityAsync()).Controllable, "host switch can restore control-off after preview");
        using var refreshHandler = new HostInitHandler(refresh: true);
        using var refreshApi = new UuMacHostApi(loginApi.State, refreshHandler);
        await refreshApi.RefreshDeviceProfileAsync(profile);
        check(refreshApi.State == loginApi.State, "device metadata refresh retains account token and device identity");
        using var assistanceHandler = new AssistanceHandler();
        using var assistanceApi = new UuMacHostApi(loginApi.State, assistanceHandler);
        var device = loginApi.State.DeviceId;
        HostAssistance.SetEnabled(device, true);
        foreach (var displayCustom in new[] { false, true })
        {
            HostAssistance.Configure(device, "Fixture123", displayCustom);
            await assistanceApi.AnswerAssistanceAsync("fixture-control", "fixture-salt", default);
            check(assistanceHandler.Allowed && assistanceHandler.Primary == HostAssistance.Sign("fixture-salt", HostAssistance.Code(device))
                && assistanceHandler.Backup == HostAssistance.Sign("fixture-salt", "Fixture123"), "both codes are submitted regardless of displayed mode");
        }
        var oldTemporarySign = assistanceHandler.Primary;
        HostAssistance.RotateAfterConnection(device);
        await assistanceApi.AnswerAssistanceAsync("fixture-control", "fixture-salt", default);
        check(assistanceHandler.Primary != oldTemporarySign && assistanceHandler.Backup == HostAssistance.Sign("fixture-salt", "Fixture123"), "successful connection rotates only temporary signature");
        HostAssistance.SetEnabled(device, false);
        await assistanceApi.AnswerAssistanceAsync("fixture-control", "fixture-salt", default);
        check(!assistanceHandler.Allowed && assistanceHandler.Primary == "" && assistanceHandler.Backup == "", "disabling assistance withholds both signatures");
        HostAssistance.SetEnabled(device, true);
        HostAssistance.Configure(device, "", false);
        await assistanceApi.AnswerAssistanceAsync("fixture-control", "fixture-salt", default);
        check(assistanceHandler.Allowed && assistanceHandler.Primary.Length == 64 && assistanceHandler.Backup == "", "clearing custom code leaves only temporary authentication");
        using var uninitialized = new UuMacHostApi(new(ClientId: "fixture-client"), loginHandler);
        try { await uninitialized.SendLoginCodeAsync("15500000000"); check(false, "SMS requires host initialization"); }
        catch (InvalidOperationException) { check(loginHandler.Requests == 2, "SMS requires host initialization"); }
    }

    private sealed class AssistanceHandler : HttpMessageHandler
    {
        public bool Allowed;
        public string Primary = "", Backup = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/api/v2/room/share/upload_sign")
                throw new Exception("Unexpected assistance request.");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var value = body.RootElement;
            Allowed = value.GetProperty("can_remote_control").GetBoolean();
            Primary = value.GetProperty("sign").GetString()!;
            Backup = value.GetProperty("backup_sign").GetString()!;
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"code\":0,\"data\":{}}") };
        }
    }

    private sealed class HostAvailabilityHandler : HttpMessageHandler
    {
        public bool Enabled { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Headers.Authorization?.Parameter != "fixture-token") throw new Exception("Missing host authorization.");
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/v1/device/mac_controllable")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Enabled = body.RootElement.GetProperty("controllable").GetBoolean();
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"code\":0,\"data\":{}}") };
            }
            if (request.Method != HttpMethod.Get || request.RequestUri!.AbsolutePath != "/api/v1/device/list")
                throw new Exception("Unexpected availability request.");
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                code = 0, data = new { current_device = new { controllable = Enabled,
                    publisher_availability_status = Enabled ? "available" : "control_off" } }
            })) };
        }
    }

    private sealed class HostLoginHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var data = body.RootElement;
            if (request.Method != HttpMethod.Post || request.Headers.GetValues("X-Param-device-id").Single() != "fixture-device"
                || request.Headers.GetValues("X-Param-PLAT").Single() != "4" || data.GetProperty("mobile").GetString() != "15500000000"
                || data.GetProperty("country_code").GetString() != "86") throw new Exception("Invalid host login request.");
            var codeRequest = request.RequestUri!.AbsolutePath == "/api/v1/security/mobile/code";
            if (codeRequest && data.GetProperty("type").GetString() != "login") throw new Exception("Invalid SMS type.");
            if (!codeRequest && (request.RequestUri.AbsolutePath != "/api/v1/login/by_mobile" || data.GetProperty("code").GetString() != "123456"))
                throw new Exception("Invalid verification request.");
            return new(HttpStatusCode.OK) { Content = new StringContent(codeRequest ? "{\"code\":0,\"data\":{}}"
                : "{\"code\":0,\"data\":{\"token\":\"fixture-token\",\"user_id\":\"fixture-user\"}}") };
        }
    }

    private sealed class HostInitHandler(bool refresh = false) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/api/v1/device/macos/init"
                || (refresh ? request.Headers.Authorization?.Parameter != "fixture-token" : request.Headers.Authorization is not null) || request.Headers.GetValues("X-Param-PLAT").Single() != "4")
                throw new Exception("Invalid initialization request.");
            var body = await request.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.EnumerateObject().Count() != 15 || root.GetProperty("client_id").GetString() != "fixture-client"
                || root.GetProperty("system_id").GetString() != "fixture-system" || root.GetProperty("video").ValueKind != JsonValueKind.Array
                || root.GetProperty("dpi").GetInt32() != 96 || root.GetProperty("mac").GetString() != "")
                throw new Exception("Invalid DeviceInfo schema.");
            var headers = request.Headers.ToDictionary(x => x.Key, x => string.Join(",", x.Value));
            if (UuSigning.ComputeSignature(headers, "POST", request.RequestUri.AbsolutePath, body) != request.Headers.GetValues("X-Param-SIGN").Single())
                throw new Exception("Signature does not cover transmitted bytes.");
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"code\":0,\"data\":{\"device_id\":\"fixture-device\"}}") };
        }
    }
}
