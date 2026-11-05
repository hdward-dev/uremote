using URemote.Core;
using URemote.Media;

namespace URemote.Host;

internal sealed class HostScreenSaver : IDisposable
{
    private readonly UuScreensaverApi api;
    private readonly Func<string> ffmpeg;
    private readonly SemaphoreSlim gate = new(1);
    private PrivacyScreenImage? cached;
    private Uri? cachedUrl;
    private bool initialized;
    internal HostScreenSaver(LoginState state, Func<string> ffmpeg, HttpMessageHandler? handler = null)
    { api = new(state, handler); this.ffmpeg = ffmpeg; }
    internal async Task<PrivacyScreenImage?> LoadAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        await gate.WaitAsync(token);
        try
        {
            var selection = await api.GetAsync(token);
            // Native clients notify again just after connecting. Check the latest
            // cloud selection each time, but avoid decoding the same 12K preset twice.
            // Custom uploads may replace bytes without changing the URL. Only
            // immutable preset choices reuse decoded pixels; custom updates reload.
            if (initialized && selection.Type == "preset" && selection.Image == cachedUrl)
                return cached is null ? null : new(cached.Width, cached.Height, cached.Pixels.ToArray());
            var bytes = await api.DownloadAsync(selection, token);
            PrivacyScreenImage? image = null;
            try
            {
                if (bytes is not null) image = await PrivacyImageDecoder.DecodeAsync(ffmpeg(), bytes, token);
                cached?.Dispose(); cached = image; cachedUrl = selection.Image; initialized = true;
                return image is null ? null : new(image.Width, image.Height, image.Pixels.ToArray());
            }
            finally { if (bytes is not null) Array.Clear(bytes); }
        }
        finally { gate.Release(); }
    }
    public void Dispose() { cached?.Dispose(); api.Dispose(); gate.Dispose(); }
}
