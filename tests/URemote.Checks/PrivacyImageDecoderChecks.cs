using System.Diagnostics;
using URemote.Media;
using URemote.Core;
using URemote.Host;
using System.Net;

internal static class PrivacyImageDecoderChecks
{
    internal static async Task RunAsync(string ffmpeg, Action<bool, string> check)
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP4z8DQwAAkgOA/GAAAOOAIefOCLwYAAAAASUVORK5CYII=");
        using (var image = await PrivacyImageDecoder.DecodeAsync(ffmpeg, png, default))
        {
            check(image.Width == 2 && image.Height == 2, "small screensaver PNG is decoded without unnecessary enlargement");
            check(image.Pixels.Span[..8].SequenceEqual(new byte[] { 0,0,128,255,0,0,0,255 }), "PNG transparency is composited over black and RGB is converted to BGRA");
        }
        async Task<byte[]> Generate(string codec, string size)
        {
            using var process = new Process { StartInfo = new(ffmpeg) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (var argument in new[] { "-v", "error", "-f", "lavfi", "-i", "color=c=0x5bad1d:s=" + size, "-frames:v", "1", "-threads", "1", "-c:v", codec, "-f", "image2pipe", "pipe:1" }) process.StartInfo.ArgumentList.Add(argument);
            process.Start(); var errors = process.StandardError.ReadToEndAsync(); using var output = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(output); await process.WaitForExitAsync(); await errors;
            if (process.ExitCode != 0) throw new IOException("Synthetic screensaver fixture generation failed.");
            return output.ToArray();
        }
        foreach (var codec in new[] { "mjpeg", "libwebp" })
        {
            var fixture = await Generate(codec, "64x32");
            using var image = await PrivacyImageDecoder.DecodeAsync(ffmpeg, fixture, default);
            check(image.Width == 64 && image.Height == 32 && image.Pixels.Span[3] == 255, "screensaver image decoder accepts " + codec);
        }
        var large = await Generate("png", "8192x16");
        using (var scaled = await PrivacyImageDecoder.DecodeAsync(ffmpeg, large, default))
            check(scaled.Width == 4096 && scaled.Height == 8, "large images are resized once while preserving aspect ratio");
        try { await PrivacyImageDecoder.DecodeAsync(ffmpeg, "file:///etc/passwd"u8.ToArray(), default); throw new Exception("Invalid image accepted."); }
        catch (FormatException) { check(true, "non-image payload cannot become an FFmpeg URL or filename"); }
        using var handler = new ImagesHandler(png);
        using var source = new HostScreenSaver(new(ClientId: "fixture-client", DeviceId: "fixture-device"), () => ffmpeg, handler);
        using var first = await source.LoadAsync(default);
        using var second = await source.LoadAsync(default);
        check(handler.Images == 1, "unchanged official preset is not downloaded and decoded twice");
        first!.Dispose();
        check(second!.Pixels.Span[3] == 255, "cached image copies have independent session lifetimes");
        handler.Type = "custom";
        using var custom = await source.LoadAsync(default);
        using var replaced = await source.LoadAsync(default);
        check(handler.Images == 3, "custom image updates reload even when the cloud URL stays the same");
    }
    private sealed class ImagesHandler(byte[] image) : HttpMessageHandler
    {
        internal string Type = "preset";
        internal int Images;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "api.nrd.nie.163.com")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":0,\"data\":{\"selected_type\":\"" + Type + "\",\"selected_image_url\":\"https://images.example.test/selected.png\"}}") });
            Images++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) });
        }
    }
}
