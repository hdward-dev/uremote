using Avalonia;
using Avalonia.Controls;
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
    private readonly WrapPanel localScreens = new() { Orientation = Orientation.Horizontal };
    private readonly List<WriteableBitmap> localBitmaps = [];
    private readonly Dictionary<int, TextBlock> localScreenLabels = [];
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
            {
                outputs.Clear(); screens.Children.Clear();
                foreach (var id in current)
                {
                    var check = new CheckBox { Content = "显示屏 " + (outputs.Count + 1), IsChecked = true, Margin = new Thickness(0, 0, 16, 8) };
                    outputs.Add((id, check)); screens.Children.Add(check);
                }
            }
            await RefreshLocalScreensAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { if (!disposed) detail.Text = "无法读取已授权的显示器，请重新开启被控。"; }
    }
    private readonly Dictionary<string, Bitmap> previewCache = [];
    private DateTimeOffset localWallpaperChecked;
    private readonly HttpClient previewHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };

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
                if (png is not null && !disposed && !loggingOut && generation == accountGeneration)
                {
                    using var stream = new MemoryStream(png);
                    var bitmap = Bitmap.DecodeToWidth(stream, 800);
                    if (previewCache.Remove(localWallpaperKey, out var old)) old.Dispose();
                    previewCache[localWallpaperKey] = bitmap;
                    RenderDevices();
                }
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
                bytes.Position = 0;
                var bitmap = Bitmap.DecodeToWidth(bytes, 800);
                if (disposed || loggingOut || generation != accountGeneration) { bitmap.Dispose(); return; }
                previewCache.Add(url, bitmap);
                RenderDevices();
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
            localScreens.Children.Clear(); localScreenLabels.Clear();
            foreach (var old in localBitmaps) old.Dispose(); localBitmaps.Clear();
            foreach (var (id, check) in outputs.Take(5).ToArray())
            {
                var image = new Image { Width = 112, Height = 63, Stretch = Stretch.Uniform };
                var screenIndex = outputs.FindIndex(x => x.Id == id) + 1;
                var label = Text($"显示屏 {screenIndex}" + (localScreenFps.TryGetValue(screenIndex, out var fps) ? $" · FPS {fps}" : ""), 11, true);
                localScreenLabels[screenIndex] = label;
                var tile = new Button { Content = new StackPanel { Spacing = 3, Children = { image, label } },
                    Padding = new Thickness(4), Margin = new Thickness(0, 0, 6, 2) };
                tile.Click += (_, _) => ShowPage(1); ToolTip.SetTip(tile, "本机屏幕快照 · 点击修改共享设置"); localScreens.Children.Add(tile);
                if (!hostEnabled || Environment.GetEnvironmentVariable("UREMOTE_NO_AUTO_START") == "1") continue;
                CapturedScreen? frame = null;
                try
                {
                    frame = await DesktopBackend.CaptureAsync(id, false, lifetime.Token);
                    if (disposed) return;
                    if (frame.ShmFormat is not (0 or 1)) continue;
                    var bitmap = new WriteableBitmap(new PixelSize(224, 126), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                    using (var target = bitmap.Lock())
                    {
                        var row = new byte[224 * 4];
                        for (var y = 0; y < 126; y++)
                        {
                            var sy = y * (int)frame.Height / 126;
                            if (frame.YInverted) sy = (int)frame.Height - 1 - sy;
                            for (var x = 0; x < 224; x++)
                            {
                                var at = sy * (int)frame.Stride + x * (int)frame.Width / 224 * 4;
                                Array.Copy(frame.Pixels, at, row, x * 4, 4); row[x * 4 + 3] = 255;
                            }
                            Marshal.Copy(row, 0, target.Address + y * target.RowBytes, row.Length);
                        }
                    }
                    localBitmaps.Add(bitmap); image.Source = bitmap;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                catch { image.IsVisible = false; }
                finally { if (frame is not null) Array.Clear(frame.Pixels); }
            }
        }
        finally { loadingScreens = false; }
    }

    private void UpdateScreenFps(string value)
    {
        var fields = value.Split(';').Select(x => x.Split('=', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
        if (!fields.TryGetValue("screen", out var screen) || !int.TryParse(screen, out var index)
            || !fields.TryGetValue("fps", out var fps)) return;
        var shared = outputs.Select((o, i) => (o, i)).Where(x => x.o.Check.IsChecked == true).Select(x => x.i + 1).ToArray();
        if (index > 0 && index <= shared.Length) index = shared[index - 1];
        localScreenFps[index] = fps;
        if (localScreenLabels.TryGetValue(index, out var label)) label.Text = $"显示屏 {index} · FPS {fps}";
    }

    private void ClearScreenFps()
    {
        localScreenFps.Clear();
        foreach (var (index, label) in localScreenLabels) label.Text = $"显示屏 {index}";
    }

    private DeviceCard BuildPreviewCard(UuDevice device)
    {
        var allowed = !device.IsCurrent && device.Online && device.Controllable && device.ControlledSupport && device.Category == "desktop";
        var wallpaperKey = device.IsCurrent && previewCache.ContainsKey("local-wallpaper") ? "local-wallpaper" : device.WallpaperUrl;
        previewCache.TryGetValue(wallpaperKey, out var wallpaper);
        return new DeviceCard { DataContext = new DeviceCardModel {
            Name = device.Name,
            StatusText = "●  " + device.StatusName,
            PlatformName = device.PlatformName,
            BadgeText = device.IsCurrent ? "本机" : device.StatusName,
            FavoriteLabel = favorites.Contains(device.Id) ? "取消收藏" : "收藏设备",
            Online = device.Online,
            HasWallpaper = wallpaper is not null,
            Wallpaper = wallpaper,
            WallpaperOpacity = device.Online ? 1 : .5,
            IsMobile = device.Category == "mobile",
            ShowBadge = device.IsCurrent || !device.Online,
            CanControl = allowed,
            ListView = listView,
            PreviewHeight = listView ? 120 : 190,
            IsSelected = selectedDeviceId == device.Id,
            ConnectTip = allowed ? "点击预览进入远程控制" : null,
            ConnectName = allowed ? "远程控制 " + device.Name : "",
            TransferTip = allowed ? "在独立窗口中浏览、上传和下载远端文件" : "设备需在线并允许被控",
            TerminalTip = allowed ? "打开远程终端命令窗口" : "设备需在线并允许被控",
            TransferName = "文件传输 " + device.Name,
            TerminalName = "终端 " + device.Name,
            PortName = "端口映射 " + device.Name,
            WakeName = "远程开机 " + device.Name,
            RequestRemote = () => OpenRemote(device),
            RequestFiles = () => OpenTool(device, false),
            RequestTerminal = () => OpenTool(device, true),
            RequestFavorite = () => { if (!favorites.Add(device.Id)) favorites.Remove(device.Id); SaveFavorites(); RenderDevices(); },
            RequestDetails = () => { ShowDevice(device); RenderDevices(); }
        } };
    }
}
