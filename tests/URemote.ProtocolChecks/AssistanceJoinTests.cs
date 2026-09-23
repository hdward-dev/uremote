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
        handler.InvalidRoom = true;
        try { await api.JoinAssistanceAsync("123456789", "Fixture123"); throw new Exception("Expected invalid room"); }
        catch (InvalidDataException) { check(handler.Cancels == 2, "malformed successful assistance join is cancelled"); }
    }
    private sealed class Handler(string fixture) : HttpMessageHandler
    {
        public int Joins, Cancels;
        public bool InvalidRoom;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (request.Method != HttpMethod.Post || json.RootElement.GetProperty("connect_id").GetString() != "123456789") throw new Exception("Invalid assistance request");
            var cancel = request.RequestUri!.AbsolutePath == "/api/v2/room/share/cancel_remote_assist";
            if (cancel) Cancels++;
            else
            {
                if (request.RequestUri.AbsolutePath != "/api/v2/room/join/share/by_code" || json.RootElement.GetProperty("connect_code").GetString() != "Fixture123") throw new Exception("Invalid join");
                Joins++;
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(cancel ? "{\"code\":0}" : InvalidRoom ? "{\"code\":0,\"data\":{}}" : fixture) };
        }
    }
}
