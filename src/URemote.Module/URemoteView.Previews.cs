using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using URemote.Core;
using URemote.Linux;
using Avalonia.Platform;
using System.Runtime.InteropServices;

namespace URemote.Module;

public sealed partial class URemoteView
{
    private readonly List<WriteableBitmap> localBitmaps = [];
    private readonly Dictionary<int, LocalScreenModel> localScreenLabels = [];
    private readonly Dictionary<int, string> localScreenFps = [];
    private bool loadingScreens;
    private async Task RefreshAuthorizedScreensAsync()
    {
        try
        {
            var globals = await DesktopBackend.DiscoverAsync(lifetime.Token);
            if (disposed) return;
            var current = globals.Where(g => g.Interface == "wl_output").Select(g => g.Name).Order().Take(5).ToArray();
            if (!outputs.Select(o => o.Id).SequenceEqual(current))
                ReplaceScreens(await DesktopBackend.ReadOutputsAsync(lifetime.Token), null);
            SaveSettings();
            await RefreshLocalScreensAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { if (!disposed) detail.Text = "无法读取已授权的显示器，请重新开启被控。"; }
    }
    private readonly Dictionary<string, Bitmap> previewCache = [];
    private DateTimeOffset localWallpaperChecked;
    private readonly HttpClient previewHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };

    // Avalonia bitmaps and controls are owned by the UI thread. Decoding and property changes off that thread surface as "a different thread owns it".
    private static Task<T> OnUiAsync<T>(Func<T> action)
    {
        if (Dispatcher.UIThread.CheckAccess()) return Task.FromResult(action());
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try { done.TrySetResult(action()); }
            catch (Exception e) { done.TrySetException(e); }
        });
        return done.Task;
    }
    private static Task OnUiAsync(Action action) => OnUiAsync(() => { action(); return true; });

    private async Task RefreshPreviewsAsync()
    {
        if (loggingOut) return;
        var generation = accountGeneration;
        var activeUrls = devices.Select(d => d.WallpaperUrl).ToHashSet();
        const string localWallpaperKey = "local-wallpaper";
        if (devices.Any(d => d.IsCurrent)) activeUrls.Add(localWallpaperKey);
        foreach (var old in previewCache.Keys.Where(k => !activeUrls.Contains(k)).ToArray())
        { previewCache[old].Dispose(); previewCache.Remove(old); }
        if (devices.Any(d => d.IsCurrent) && DateTimeOffset.UtcNow - localWallpaperChecked > TimeSpan.FromSeconds(30))
        {
            localWallpaperChecked = DateTimeOffset.UtcNow;
            try
            {
                var png = await LinuxWallpaper.RenderAsync(appliedEncoder, lifetime.Token);
                if (disposed || loggingOut || generation != accountGeneration) return;
                var bytes = png;
                await OnUiAsync(() =>
                {
                    if (disposed || loggingOut || generation != accountGeneration) return;
                    if (bytes is null)
                    {
                        if (previewCache.Remove(localWallpaperKey, out var stale)) { stale.Dispose(); RenderDevices(); }
                        return;
                    }
                    if (previewCache.Remove(localWallpaperKey, out var old)) old.Dispose();
                    using var stream = new MemoryStream(bytes);
                    previewCache[localWallpaperKey] = Bitmap.DecodeToWidth(stream, 800);
                    RenderDevices();
                });
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch { /* Wallpaper availability must not affect the device list. */ }
        }
        // No login headers or identity values are sent to image hosts. Keep previews in memory only.
        foreach (var url in devices.Select(d => d.WallpaperUrl).Where(u => u.Length > 0).Distinct().Take(32))
        {
            if (disposed || loggingOut || generation != accountGeneration || previewCache.Count >= 32) break;
            if (previewCache.ContainsKey(url)) continue;
            try
            {
                using var response = await previewHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 5_000_000) continue;
                await using var stream = await response.Content.ReadAsStreamAsync(lifetime.Token);
                using var bytes = new MemoryStream();
                var chunk = new byte[16384]; int read;
                while ((read = await stream.ReadAsync(chunk, lifetime.Token)) > 0)
                {
                    if (bytes.Length + read > 5_000_000) throw new IOException("Preview too large.");
                    bytes.Write(chunk, 0, read);
                }
                var copy = bytes.ToArray();
                await OnUiAsync(() =>
                {
                    if (disposed || loggingOut || generation != accountGeneration || previewCache.ContainsKey(url)) return;
                    using var image = new MemoryStream(copy);
                    previewCache.Add(url, Bitmap.DecodeToWidth(image, 800));
                    RenderDevices();
                });
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch { /* A missing thumbnail must never prevent remote control. */ }
        }
    }

    private async Task RefreshLocalScreensAsync()
    {
        if (loadingScreens || disposed) return;
        loadingScreens = true;
        try
        {
            var shares = outputs.Take(5).ToArray();
            var built = new List<(int Index, string Caption, byte[]? Pixels, string? Error)>();
            foreach (var share in shares)
            {
                var screenIndex = outputs.Select((item, index) => (item, index)).First(x => x.item.Id == share.Id).index + 1;
                var caption = $"显示屏 {screenIndex}" + (localScreenFps.TryGetValue(screenIndex, out var fps) ? $" · FPS {fps}" : "");
                if (!hostEnabled || Environment.GetEnvironmentVariable("UREMOTE_NO_AUTO_START") == "1")
                {
                    built.Add((screenIndex, caption, null, null));
                    continue;
                }
                CapturedScreen? frame = null;
                try
                {
                    frame = await DesktopBackend.CaptureAsync(share.Id, false, lifetime.Token);
                    if (disposed) return;
                    var pixels = frame.ShmFormat is 0 or 1 ? ScalePreview(frame, 224, 126) : null;
                    built.Add((screenIndex, caption, pixels, null));
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch (Exception e) { built.Add((screenIndex, caption, null, e.GetType().Name)); }
                finally { if (frame is not null) Array.Clear(frame.Pixels); }
            }
            await OnUiAsync(() =>
            {
                if (disposed) return;
                localScreenItems.Clear(); localScreenLabels.Clear();
                foreach (var old in localBitmaps) old.Dispose();
                localBitmaps.Clear();
                foreach (var item in built)
                {
                    var tile = new LocalScreenModel {
                        Caption = item.Error is null ? item.Caption : $"显示屏 {item.Index} · {item.Error}",
                        ShowSnapshot = item.Pixels is not null,
                        OpenSettings = new AsyncCommand(() => { ShowPage(1); return Task.CompletedTask; }) };
                    if (item.Pixels is { } pixels)
                    {
                        var image = new WriteableBitmap(new PixelSize(224, 126), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                        using (var target = image.Lock())
                        {
                            for (var y = 0; y < 126; y++)
                                Marshal.Copy(pixels, y * 224 * 4, target.Address + y * target.RowBytes, 224 * 4);
                        }
                        localBitmaps.Add(image);
                        tile.Snapshot = image;
                    }
                    localScreenLabels[item.Index] = tile;
                    localScreenItems.Add(tile);
                }
            });
        }
        finally { loadingScreens = false; }
    }

    private static byte[] ScalePreview(CapturedScreen frame, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sy = y * (int)frame.Height / height;
            if (frame.YInverted) sy = (int)frame.Height - 1 - sy;
            for (var x = 0; x < width; x++)
            {
                var at = sy * (int)frame.Stride + x * (int)frame.Width / width * 4;
                var to = (y * width + x) * 4;
                if ((uint)at > frame.Pixels.Length - 4) continue;
                pixels[to] = frame.Pixels[at]; pixels[to + 1] = frame.Pixels[at + 1]; pixels[to + 2] = frame.Pixels[at + 2]; pixels[to + 3] = 255;
            }
        }
        return pixels;
    }

    private void UpdateScreenFps(string value)
    {
        var fields = value.Split(';').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
        if (!fields.TryGetValue("screen", out var screen) || !int.TryParse(screen, out var index)
            || !fields.TryGetValue("fps", out var fps)) return;
        var shared = outputs.Select((o, i) => (o, i)).Where(x => x.o.IsChecked == true).Select(x => x.i + 1).ToArray();
        if (index > 0 && index <= shared.Length) index = shared[index - 1];
        localScreenFps[index] = fps;
        var caption = $"显示屏 {index} · FPS {fps}";
        if (localScreenLabels.TryGetValue(index, out var label))
            Dispatcher.UIThread.Post(() => { if (!disposed) label.Caption = caption; });
    }

    private void ClearScreenFps()
    {
        localScreenFps.Clear();
        var labels = localScreenLabels.ToArray();
        Dispatcher.UIThread.Post(() =>
        {
            if (disposed) return;
            foreach (var (index, label) in labels) label.Caption = $"显示屏 {index}";
        });
    }
}
