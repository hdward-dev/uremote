using System.Net;
using System.Text.Json;
using URemote.Core;

internal static class ScreensaverApiTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var state = new LoginState(ClientId: "fixture-client", DeviceId: "fixture-device", Token: "fixture-token", UserId: "fixture-user");
        var count = 0;
        using var handler = new Handler(request =>
        {
            if (count++ == 0)
            {
                check(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/device/fixture-device/screensaver/detail",
                    "screensaver notification fetches this host's cloud configuration");
                check(request.Headers.Authorization?.Parameter == "fixture-token" && request.Headers.Contains("X-Param-SIGN"), "screensaver API uses existing signed host authorization");
                return new(HttpStatusCode.OK) { Content = new StringContent("""{"code":0,"data":{"selected_type":"custom","selected_image_url":"https://images.example.test/photo.png"}}""") };
            }
            check(request.Headers.Authorization is null && !request.Headers.Any(h => h.Key.StartsWith("X-Param-")), "image downloads never disclose host authorization headers");
            if (count == 2)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new Uri("/actual.png", UriKind.Relative); return response;
            }
            check(request.RequestUri!.AbsolutePath == "/actual.png", "HTTPS image redirects resolve against the current host");
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([137,80,78,71]) };
        });
        using var api = new UuScreensaverApi(state, handler);
        var selection = await api.GetAsync(default);
        check(selection.Type == "custom" && !selection.ToString().Contains("photo.png"), "custom selection parsed without exposing image URL in diagnostics");
        check((await api.DownloadAsync(selection, default))!.Length == 4, "bounded cloud image downloaded through a separate request");
        foreach (var url in new[] { "file:///etc/passwd", "http://images.example.test/x", "https://user:password@images.example.test/x", "https://localhost/x" })
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { selected_type = "preset", selected_image_url = url }));
            try { UuScreensaverApi.Parse(document.RootElement); throw new Exception("Unsafe URL accepted."); }
            catch (FormatException) { check(true, "non-image or credential-bearing screen saver URL rejected"); }
        }
        using (var empty = JsonDocument.Parse("""{"selected_type":"none","selected_image_url":""}"""))
            check(UuScreensaverApi.Parse(empty.RootElement).Image is null, "explicitly cleared cloud selection resets the image");
        using (var missing = JsonDocument.Parse("""{"selected_type":"preset","selected_image_url":""}"""))
        {
            try { UuScreensaverApi.Parse(missing.RootElement); throw new Exception("Missing image accepted."); }
            catch (FormatException) { check(true, "missing selected image is not mistaken for a successful update"); }
        }
        using var oversize = new UuScreensaverApi(state, new Handler(_ =>
        {
            var content = new ByteArrayContent([1]); content.Headers.ContentLength = 16_777_217;
            return new(HttpStatusCode.OK) { Content = content };
        }));
        try { await oversize.DownloadAsync(selection, default); throw new Exception("Oversize image accepted."); }
        catch (IOException) { check(true, "oversize image rejected before reading its body"); }
        var pixels = new byte[] { 100, 200, 50, 0, 100, 200, 50, 128 };
        using (var image = new PrivacyScreenImage(2, 1, pixels))
            check(image.Pixels.Span.SequenceEqual(new byte[] { 0,0,0,255,50,100,25,255 }), "transparent privacy images are composited over opaque black");
        check(pixels.All(b => b == 0), "disposed privacy image pixels are cleared");
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
