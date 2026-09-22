using Avalonia;
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
    private readonly WrapPanel localScreens = new() { Orientation = Orientation.Horizontal };
    private readonly List<WriteableBitmap> localBitmaps = [];
    private bool loadingScreens;
    private readonly Dictionary<string, Bitmap> previewCache = [];
    private readonly HttpClient previewHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };

    private async Task RefreshPreviewsAsync()
    {
        var activeUrls = devices.Select(d => d.WallpaperUrl).ToHashSet();
        foreach (var old in previewCache.Keys.Where(k => !activeUrls.Contains(k)).ToArray())
        { previewCache[old].Dispose(); previewCache.Remove(old); }
        // No login headers or identity values are sent to image hosts. Keep previews in memory only.
        foreach (var url in devices.Select(d => d.WallpaperUrl).Where(u => u.Length > 0).Distinct().Take(32))
        {
            if (disposed || previewCache.Count >= 32) break;
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
                if (disposed) { bitmap.Dispose(); return; }
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
            localScreens.Children.Clear();
            foreach (var old in localBitmaps) old.Dispose(); localBitmaps.Clear();
            foreach (var (id, check) in outputs.Take(5).ToArray())
            {
                var image = new Image { Width = 112, Height = 63, Stretch = Stretch.Uniform };
                var tile = new Button { Content = new StackPanel { Spacing = 5, Children = { image, Text(check.Content?.ToString() ?? "显示器", 11, true) } },
                    Padding = new Thickness(4), Margin = new Thickness(0, 0, 6, 6) };
                tile.Click += (_, _) => ShowPage(1); ToolTip.SetTip(tile, "本机屏幕快照 · 点击修改共享设置"); localScreens.Children.Add(tile);
                if (!hostEnabled || Environment.GetEnvironmentVariable("UREMOTE_NO_AUTO_START") == "1") continue;
                CapturedScreen? frame = null;
                try
                {
                    frame = await WaylandScreenCapture.CaptureAsync(id, false, lifetime.Token);
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

    private Control BuildPreviewCard(UuDevice device)
    {
        var visual = new Grid { Height = listView ? 120 : 190, ClipToBounds = true };
        ApplyTheme(visual, Panel.BackgroundProperty, "AppSubtleBrush");
        if (previewCache.TryGetValue(device.WallpaperUrl, out var bitmap))
            visual.Children.Add(new Image { Source = bitmap, Stretch = Stretch.UniformToFill, Opacity = device.Online ? 1 : .5 });
        else
            visual.Children.Add(new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                Children = { DeviceIcon(device.Category == "mobile"), Text("暂无桌面预览", 12, true) } });
        if (!listView) visual.SizeChanged += (_, e) => { var height = Math.Clamp(e.NewSize.Width * 9 / 16, 130, 220); if (Math.Abs(visual.Height - height) > 1) visual.Height = height; };
        if (device.IsCurrent || !device.Online)
        {
            var badge = new Border { Background = new SolidColorBrush(Color.Parse("#BB263442")), Padding = new Thickness(9, 5), CornerRadius = new CornerRadius(6), Margin = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = device.IsCurrent ? "本机" : device.StatusName, Foreground = Brushes.White, FontSize = 12 } };
            visual.Children.Add(badge);
        }
        var name = Text(device.Name, 16); name.FontWeight = FontWeight.SemiBold; name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
        var online = Text("●  " + device.StatusName, 12);
        ApplyTheme(online, TextBlock.ForegroundProperty, device.Online ? "AppPositiveBrush" : "AppMutedBrush");
        var info = new StackPanel { Spacing = 7, Margin = new Thickness(12), Children = { name, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { online, Text(device.PlatformName, 12, true) } } } };
        var allowed = !device.IsCurrent && device.Online && device.Controllable && device.ControlledSupport && device.Category == "desktop";
        var actions = new Grid { ColumnDefinitions = new("*,Auto,*,Auto,*,Auto,*,Auto,Auto"), ColumnSpacing = 4, Margin = new Thickness(8, 5) };
        var labels = new List<TextBlock>();
        Button ActionButton(string label, string path, bool enabled, string tip, Action action)
        {
            var caption = Text(label, 12); labels.Add(caption);
            var icon = new Avalonia.Controls.Shapes.Path { Width = 15, Height = 15, Data = Geometry.Parse(path), StrokeThickness = 1.5, Stretch = Stretch.Uniform };
            ApplyTheme(icon, Avalonia.Controls.Shapes.Shape.StrokeProperty, "AppStrongTextBrush");
            var button = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { icon, caption } },
                Padding = new Thickness(4, 7), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, IsEnabled = enabled };
            button.Opacity = enabled ? 1 : .35;
            button.Template = new FuncControlTemplate<Button>((b, _) => new ContentPresenter {
                Content = b.Content, Padding = b.Padding, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center });
            ToolTip.SetTip(button, tip); Avalonia.Automation.AutomationProperties.SetName(button, label + " " + device.Name);
            button.Click += (_, _) => action(); return button;
        }
        var fileButton = ActionButton("文件传输", "M2,5 H9 L11,8 H22 V21 H2 Z M5,12 H19 M15,9 L19,12 L15,15", allowed, allowed ? "在独立窗口中浏览、上传和下载远端文件" : "设备需在线并允许被控", () => OpenTool(device, false));
        var terminalButton = ActionButton("终端", "M2,4 H22 V20 H2 Z M6,9 L10,12 L6,15 M12,15 H18", allowed, allowed ? "打开远程终端命令窗口" : "设备需在线并允许被控", () => OpenTool(device, true));
        var portButton = ActionButton("端口映射", "M3,3 H9 V21 H3 Z M15,3 H21 V21 H15 Z M9,8 H15 M9,16 H15", false, "端口映射尚未实现", () => { });
        var wakeButton = ActionButton("远程开机", "M11,2 H13 V12 H11 Z M6,5 L7,7 A7,7 0 1 0 17,7 L18,5 A9,9 0 1 1 6,5", false, "远程开机尚未实现", () => { });
        var buttons = new[] { fileButton, terminalButton, portButton, wakeButton };
        for (var i = 0; i < buttons.Length; i++)
        {
            Grid.SetColumn(buttons[i], i * 2); actions.Children.Add(buttons[i]);
            var separator = new Border { Width = 1, Height = 16, VerticalAlignment = VerticalAlignment.Center }; ApplyTheme(separator, Border.BackgroundProperty, "AppBorderBrush");
            Grid.SetColumn(separator, i * 2 + 1); actions.Children.Add(separator);
        }
        var star = new Button { Content = favorites.Contains(device.Id) ? "取消收藏" : "收藏设备", HorizontalAlignment = HorizontalAlignment.Stretch };
        star.Click += (_, _) => { if (!favorites.Add(device.Id)) favorites.Remove(device.Id); SaveFavorites(); RenderDevices(); };
        var inspect = new Button { Content = "设备详情", HorizontalAlignment = HorizontalAlignment.Stretch };
        inspect.Click += (_, _) => { ShowDevice(device); RenderDevices(); };
        var menu = new Button { Content = "▦", Padding = new Thickness(5, 7), Background = Brushes.Transparent,
            Flyout = new Flyout { Content = new StackPanel { Spacing = 6, Children = { star, inspect } } } };
        ToolTip.SetTip(menu, "更多操作"); Grid.SetColumn(menu, 8); actions.Children.Add(menu);
        actions.SizeChanged += (_, e) => { foreach (var label in labels) label.IsVisible = e.NewSize.Width >= 430; };
        Control preview = visual;
        if (allowed)
        {
            var connect = new Button { Content = visual, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ToolTip.SetTip(connect, "点击预览进入远程控制"); Avalonia.Automation.AutomationProperties.SetName(connect, "远程控制 " + device.Name);
            connect.Click += (_, _) => OpenRemote(device); preview = connect;
        }
        var content = new Grid { RowDefinitions = new("Auto,Auto,Auto") };
        content.Children.Add(preview); Grid.SetRow(info, 1); content.Children.Add(info);
        var actionBar = new Border { Child = actions, BorderThickness = new Thickness(0, 1, 0, 0) };
        ApplyTheme(actionBar, Border.BorderBrushProperty, "AppBorderBrush"); Grid.SetRow(actionBar, 2); content.Children.Add(actionBar);
        var card = Card(content, 4); card.ClipToBounds = true;
        if (selectedDeviceId == device.Id) ApplyTheme(card, Border.BorderBrushProperty, "AppAccentBrush");
        return card;
    }
}
