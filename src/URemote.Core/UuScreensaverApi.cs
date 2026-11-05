using System.Net;
using System.Text.Json;

namespace URemote.Core;

public sealed class ScreensaverSelection(string type, Uri? image)
{
    public string Type { get; } = type;
    public Uri? Image { get; } = image;
    public override string ToString() => "ScreensaverSelection(image URL omitted)";
}

public sealed class UuScreensaverApi(LoginState state, HttpMessageHandler? handler = null) : IDisposable
{
    private readonly HttpClient http = new(handler ?? new SocketsHttpHandler
    { AllowAutoRedirect = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate })
    { Timeout = TimeSpan.FromSeconds(12) };

    public async Task<ScreensaverSelection> GetAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(state.DeviceId)) throw new InvalidOperationException("Device initialization required.");
        // Official Mac ApiDefault.getScreensaver(deviceId:) appends this suffix to
        // /api/v1/device/{id}; UpdateScreenSaverRequest itself has no payload fields.
        using var request = UuMacHostProtocol.BuildRequest(state, HttpMethod.Get,
            "/api/v1/device/" + Uri.EscapeDataString(state.DeviceId) + "/screensaver/detail", "");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Screensaver configuration HTTP status " + (int)response.StatusCode);
        var bytes = await ReadAsync(response.Content, 1_048_576, ct);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (!root.TryGetProperty("code", out var code) || !code.TryGetInt32(out var value) || value != 0) throw new IOException("Screensaver configuration rejected.");
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) throw new FormatException("Missing screensaver configuration.");
            return Parse(data);
        }
        finally { Array.Clear(bytes); }
    }
    public static ScreensaverSelection Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("selected_type", out var selectedType) || selectedType.ValueKind != JsonValueKind.String
            || !data.TryGetProperty("selected_image_url", out var selectedUrl) || selectedUrl.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new FormatException("Missing screensaver selection.");
        var type = selectedType.GetString()!;
        if (type.Length > 64) throw new FormatException("Invalid screensaver selection type.");
        var url = selectedUrl.GetString() ?? "";
        if (url.Length == 0)
        {
            if (type is "" or "none") return new(type, null);
            throw new FormatException("Selected screensaver image is missing.");
        }
        return new(type, ImageUri(url));
    }
    public async Task<byte[]?> DownloadAsync(ScreensaverSelection selection, CancellationToken ct)
    {
        if (selection.Image is not { } url) return null;
        url = ImageUri(url.AbsoluteUri);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            // The device bearer token and signed API headers never go to image hosts.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (attempt == 3 || response.Headers.Location is not { } location) throw new IOException("Screensaver image redirect limit.");
                url = ImageUri(new Uri(url, location).AbsoluteUri); continue;
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("Screensaver image HTTP status " + (int)response.StatusCode);
            return await ReadAsync(response.Content, 16_777_216, ct);
        }
        throw new IOException("Screensaver image unavailable.");
    }
    private static Uri ImageUri(string value)
    {
        if (value.Length > 8192 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.IsLoopback)
            throw new FormatException("Invalid screensaver image URL.");
        return uri;
    }
    private static async Task<byte[]> ReadAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw new IOException("Screensaver response exceeds limit.");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[16_384];
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer, ct)) != 0)
            {
                if (output.Length + count > limit) throw new IOException("Screensaver response exceeds limit.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        finally { Array.Clear(buffer); }
    }
    public void Dispose() => http.Dispose();
}
