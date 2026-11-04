using System.Net;
using System.Text.Json;
using URemote.Core;
static class AssistanceJoinTests
{
    public static async Task Run(Action<bool, string> check, string fixture)
    {
        using var handler = new Handler(fixture);
        using var api = new UuMacHostApi(new(Token: "fixture-token", UserId: "fixture-user", DeviceId: "fixture-controller"), handler);
        var room = await api.JoinAssistanceAsync("123456789", "Fixture123");
        check(room.Platform == 4 && handler.Joins == 1, "assistance join sends partner id and code to the dedicated endpoint");
        await api.CancelAssistanceAsync("123456789");
        check(handler.Cancels == 1, "assistance closes with cancellation instead of device-room clearing");
        await api.JoinDeviceAsync("fixture-device");
        check(handler.LastForceJoin == false, "normal device join does not replace an existing connection");
        await api.JoinDeviceAsync("fixture-device", forceJoin: true);
        check(handler.LastForceJoin == true, "confirmed takeover sends force_join true");
        handler.HttpError = true;
        try { await api.JoinAssistanceAsync("123456789", "Fixture123"); throw new Exception("Expected HTTP error"); }
        catch (HttpRequestException ex) { check(ex.StatusCode == HttpStatusCode.BadRequest && ex.Message.Contains("code=40901") && ex.Message.Contains("fixture busy"), "HTTP 400 preserves server business code and reason"); }
        handler.HttpError = false;
        handler.InvalidRoom = true;
        try { await api.JoinAssistanceAsync("123456789", "Fixture123"); throw new Exception("Expected invalid room"); }
        catch (InvalidDataException) { check(handler.Cancels == 2, "malformed successful assistance join is cancelled"); }
    }
    private sealed class Handler(string fixture) : HttpMessageHandler
    {
        public int Joins, Cancels;
        public bool InvalidRoom, HttpError;
        public bool? LastForceJoin;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (request.RequestUri!.AbsolutePath == "/api/v1/room/join/by_device/fixture-device")
            {
                LastForceJoin = json.RootElement.GetProperty("force_join").GetBoolean();
                return new(HttpStatusCode.OK) { Content = new StringContent(fixture) };
            }
            if (request.Method != HttpMethod.Post || json.RootElement.GetProperty("connect_id").GetString() != "123456789") throw new Exception("Invalid assistance request");
            var cancel = request.RequestUri!.AbsolutePath == "/api/v2/room/share/cancel_remote_assist";
            if (cancel) Cancels++;
            else
            {
                if (request.RequestUri.AbsolutePath != "/api/v2/room/join/share/by_code" || json.RootElement.GetProperty("connect_code").GetString() != "Fixture123") throw new Exception("Invalid join");
                Joins++;
            }
            if (HttpError) return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"code\":40901,\"message\":\"fixture busy\"}") };
            return new(HttpStatusCode.OK) { Content = new StringContent(cancel ? "{\"code\":0}" : InvalidRoom ? "{\"code\":0,\"data\":{}}" : fixture) };
        }
    }
}
