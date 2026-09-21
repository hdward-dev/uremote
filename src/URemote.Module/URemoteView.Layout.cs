using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.Styling;
using URemote.Core;
using URemote.Host;
using URemote.Linux;

namespace URemote.Module;

public sealed partial class URemoteView
{
    private readonly TextBlock hostBadge = new() { Text = "●  正在准备", FontSize = 12 };
    private readonly TextBlock catalogHint = new() { Text = "登录后，同步你的 UU 账号设备", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock transferState = new() { Text = "接收和取出仅限此文件夹", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock accountState = new() { Text = "正在读取账号状态" };
    private readonly TextBlock catalogCount = new() { Text = "我的设备", FontSize = 22, FontWeight = FontWeight.SemiBold };
    private readonly TextBox search = new() { PlaceholderText = "搜索设备名称、平台或编号", MinWidth = 160 };
    private readonly ComboBox filter = new() { ItemsSource = new[] { "全部设备", "在线设备", "我的收藏", "电脑", "移动设备" }, SelectedIndex = 0, Width = 130 };
    private readonly Button refresh = new() { Content = "刷新设备" };
    private readonly StackPanel deviceRows = new() { Spacing = 10 };
    private readonly StackPanel deviceDetail = new() { Spacing = 10 };
    private readonly ContentControl page = new();
    private readonly StackPanel advancedSettings = new() { Spacing = 12 };
    private readonly List<Button> navigation = [];
    private string? selectedDeviceId;
    private readonly Dictionary<string, RemoteDesktopWindow> remoteWindows = [];
    private Control[] pages = [];
    private IReadOnlyList<UuDevice> devices = [];
    private HashSet<string> favorites = [];
    private string favoriteFile = "";
    private bool refreshing;
    private DispatcherTimer? deviceTimer;

    private static void ApplyTheme(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, new DynamicResourceExtension(key));
    private static TextBlock Text(string value, double size = 13, bool muted = false)
    {
        var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        ApplyTheme(text, TextBlock.ForegroundProperty, muted ? "AppMutedBrush" : "AppStrongTextBrush"); return text;
    }
    private static Border Card(Control child, int padding = 20)
    {
        var border = new Border { Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1) };
        ApplyTheme(border, Border.BackgroundProperty, "AppSurfaceBrush"); ApplyTheme(border, Border.BorderBrushProperty, "AppBorderBrush"); return border;
    }
    private static PathIcon DeviceIcon(bool mobile = false) => new()
    {
        Width = 22, Height = 22,
        Data = Geometry.Parse(mobile ? "M7,2 H17 V22 H7 Z M9,4 V18 H15 V4 Z M11,20 H13 V21 H11 Z" : "M2,3 H22 V17 H13 V20 H18 V22 H6 V20 H11 V17 H2 Z M4,5 V15 H20 V5 Z")
    };
    private void BuildInterface(string dataDirectory)
    {
        FontFamily = new FontFamily(OperatingSystem.IsLinux() ? "Noto Sans CJK SC" : "Segoe UI");
        Styles.Add(new Style(x => x.OfType<TextBlock>()) { Setters = { new Setter(TextBlock.FontFamilyProperty, FontFamily) } });
        favoriteFile = Path.Combine(dataDirectory, "device-favorites.json");
        try { if (File.Exists(favoriteFile)) favorites = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(favoriteFile)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException) { }
        ApplyTheme(this, BackgroundProperty, "AppPageBrush");
        ApplyTheme(status, TextBlock.ForegroundProperty, "AppStrongTextBrush"); status.FontSize = 19; status.TextWrapping = TextWrapping.Wrap;
        ApplyTheme(detail, TextBlock.ForegroundProperty, "AppMutedBrush"); detail.FontSize = 13;
        ApplyTheme(metrics, TextBlock.ForegroundProperty, "AppMutedBrush"); metrics.FontSize = 12; metrics.Text = "双屏桌面 · 远程终端";
        ApplyTheme(hostBadge, TextBlock.ForegroundProperty, "AppPositiveBrush");
        var brand = new StackPanel { Spacing = 3, Children = { Text("U远程", 26), Text("AsterDock · 远程工作空间", 12, true) } };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(brand); Grid.SetColumn(hostBadge, 1); header.Children.Add(hostBadge); hostBadge.VerticalAlignment = VerticalAlignment.Center;
        var heroContent = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 18 };
        heroContent.Children.Add(new StackPanel { Spacing = 10, Children = { Text("本机被控", 12, true), Text(LinuxDeviceProfile.DeviceName, 18), status, detail, metrics } });
        var hostActions = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children = { Text("允许被控", 13, true), hostSwitch } };
        Grid.SetColumn(hostActions, 1); heroContent.Children.Add(hostActions);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var title in new[] { "我的设备", "本机被控", "账号与设置" })
        {
            var index = navigation.Count; var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Text(new[] { "▤", "▣", "⚙" }[index], 17), Text(title, 14) } };
            var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 13), CornerRadius = new CornerRadius(9) };
            button.Click += (_, _) => ShowPage(index); navigation.Add(button); tabs.Children.Add(button);
        }
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10 };
        toolbar.Children.Add(search); Grid.SetColumn(filter, 1); toolbar.Children.Add(filter); Grid.SetColumn(refresh, 2); toolbar.Children.Add(refresh);
        ApplyTheme(catalogCount, TextBlock.ForegroundProperty, "AppStrongTextBrush"); ApplyTheme(catalogHint, TextBlock.ForegroundProperty, "AppMutedBrush"); catalogHint.FontSize = 12;
        var devicePage = new StackPanel { Spacing = 18, Children = { catalogCount, Text("管理同一 UU 账号下的电脑与移动设备", 13, true), toolbar, catalogHint, deviceDetail, deviceRows,
            Text("在线且允许被控的电脑可在独立窗口中连接。关闭远控窗口只断开对应设备。", 12, true) } };
        search.TextChanged += (_, _) => RenderDevices(); filter.SelectionChanged += (_, _) => RenderDevices();
        refresh.Click += async (_, _) => await RefreshDevicesAsync();
        duration.HorizontalAlignment = HorizontalAlignment.Stretch;
        settings.Children.Add(Text("共享哪些屏幕", 16)); settings.Children.Add(screens);
        settings.Children.Add(new Separator()); settings.Children.Add(Text("连接权限", 16));
        settings.Children.Add(input); settings.Children.Add(audio); settings.Children.Add(clipboard);
        settings.Children.Add(Text("保持被控的时长", 13, true)); settings.Children.Add(duration);
        var hostPage = new StackPanel { Spacing = 16, Children = {
            Text("本机被控", 22), Text($"在其他设备的 UU 官方客户端中，选择「{LinuxDeviceProfile.DeviceName}」连接。", 13, true), BuildAssistanceCard(), Card(settings), BuildFileTransferCard(),
            Card(new StackPanel { Spacing = 9, Children = {
                Text("连接与退出", 16), Text("主控退出后，本机继续等待连接。终端会话会保留，方便再次进入。", 13, true),
                Text("本机关闭被控开关，或退出 AsterDock，将结束共享与终端会话。修改共享权限前，请先关闭被控开关。", 13, true) } }) } };
        loginPanel.Children.Add(Text("登录 UU 账号", 18)); loginPanel.Children.Add(Text("使用与你其他设备相同的账号。", 13, true));
        var phoneRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 }; phoneRow.Children.Add(mobile); Grid.SetColumn(sendCode, 1); phoneRow.Children.Add(sendCode);
        loginPanel.Children.Add(phoneRow); loginPanel.Children.Add(code); loginPanel.Children.Add(completeLogin);
        advancedSettings.Children.Add(Text("本机登录文件", 13, true)); advancedSettings.Children.Add(identity);
        advancedSettings.Children.Add(Text("视频编码器", 13, true)); advancedSettings.Children.Add(encoder);
        advancedSettings.Children.Add(Text("路径在下次启动被控时保存。正常使用无需修改。", 12, true));
        var accountPage = new StackPanel { Spacing = 16, Children = { Text("账号与设置", 22),
            Card(new StackPanel { Spacing = 12, Children = { Text("UU 账号", 16), accountState, loginPanel } }),
            Card(new Expander { Header = "高级设置", HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = advancedSettings }),
            Card(new StackPanel { Spacing = 8, Children = { Text("功能状态", 16), Text("已实测：双屏桌面、键鼠控制、退出后重连、远程终端命令执行。", 13, true),
                Text("待验证：双向文本剪贴板、系统声音和画质切换效果。", 13, true) } }) } };
        pages = [devicePage, hostPage, accountPage];
        var body = new Grid { Margin = new Thickness(24), ColumnSpacing = 28, RowSpacing = 20 };
        var hero = Card(heroContent, 18);
        var scroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = page };
        body.Children.Add(header); body.Children.Add(hero); body.Children.Add(tabs); body.Children.Add(scroll);
        Content = body;
        bool? previousWide = null;
        void ArrangeWorkspace(double width)
        {
            var wide = width >= 1000;
            if (previousWide == wide) return;
            previousWide = wide;
            body.ColumnDefinitions = new ColumnDefinitions(wide ? "270,*" : "*");
            body.RowDefinitions = new RowDefinitions(wide ? "Auto,Auto,*" : "Auto,Auto,Auto,*");
            foreach (var child in body.Children) { Grid.SetColumn(child, 0); Grid.SetRowSpan(child, 1); }
            Grid.SetRow(header, 0); Grid.SetRow(tabs, wide ? 1 : 2); Grid.SetRow(hero, wide ? 2 : 1);
            hero.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(scroll, wide ? 1 : 0); Grid.SetRow(scroll, wide ? 0 : 3); Grid.SetRowSpan(scroll, wide ? 3 : 1);
            tabs.Orientation = wide ? Orientation.Vertical : Orientation.Horizontal;
            header.ColumnDefinitions = new ColumnDefinitions(wide ? "*" : "*,Auto");
            header.RowDefinitions = new RowDefinitions(wide ? "Auto,Auto" : "Auto");
            Grid.SetColumn(hostBadge, wide ? 0 : 1); Grid.SetRow(hostBadge, wide ? 1 : 0);
            hostBadge.Margin = new Thickness(0, wide ? 14 : 0, 0, 0);
            heroContent.ColumnDefinitions = new ColumnDefinitions(wide ? "*" : "*,Auto");
            Grid.SetColumn(hostActions, wide ? 0 : 1); Grid.SetRow(hostActions, wide ? 1 : 0);
            hostActions.Orientation = wide ? Orientation.Horizontal : Orientation.Vertical;

        }
        SizeChanged += (_, e) => ArrangeWorkspace(e.NewSize.Width);
        ArrangeWorkspace(Bounds.Width);
        ShowPage(0); RenderDevices();
        try { accountState.Text = DesktopHostSession.ReadIdentity(identity.Text ?? "").State.IsAuthenticated ? "已登录 · 与 UU 账号同步" : "尚未登录"; }
        catch { accountState.Text = "尚未登录"; }
        loginPanel.IsVisible = accountState.Text == "尚未登录";
        deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        deviceTimer.Tick += async (_, _) => { if (IsVisible && page.Content == pages[0] && Environment.GetEnvironmentVariable("UREMOTE_NO_AUTO_START") != "1") await RefreshDevicesAsync(); };
        deviceTimer.Start();
    }
    private Control BuildFileTransferCard()
    {
        var open = new Button { Content = "打开接收目录" };
        open.Click += (_, _) => {
            try {
                Directory.CreateDirectory(HostTransferPaths.SharedDirectory);
                var launch = new System.Diagnostics.ProcessStartInfo("xdg-open") { UseShellExecute = false };
                launch.ArgumentList.Add(HostTransferPaths.SharedDirectory); System.Diagnostics.Process.Start(launch)?.Dispose();
            } catch { transferState.Text = "无法打开目录，请手动打开下方路径。"; }
        };
        var add = new Button { Content = "添加待取文件" };
        add.Click += async (_, _) => {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
            var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = "选择供 UU 官方客户端取出的文件", AllowMultiple = true });
            if (files.Count == 0) return; add.IsEnabled = false;
            try {
                Directory.CreateDirectory(HostTransferPaths.SharedDirectory);
                foreach (var file in files) {
                    var name = Path.GetFileName(file.Name); var target = Path.Combine(HostTransferPaths.SharedDirectory, name);
                    if (File.Exists(target)) target = Path.Combine(HostTransferPaths.SharedDirectory, Path.GetFileNameWithoutExtension(name) + "-" + Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(name));
                    var temporary = Path.Combine(HostTransferPaths.SharedDirectory, ".uremote-" + Guid.NewGuid().ToString("N") + ".part");
                    try {
                        await using (var source = await file.OpenReadAsync())
                        await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                            await source.CopyToAsync(destination, lifetime.Token);
                        File.Move(temporary, target, false);
                    } finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                transferState.Text = $"已添加 {files.Count} 个文件，可在官方客户端刷新目录后取出。";
            } catch { transferState.Text = "添加未完成，请检查文件和磁盘空间。"; }
            finally { add.IsEnabled = true; }
        };
        ApplyTheme(transferState, TextBlock.ForegroundProperty, "AppMutedBrush");
        return Card(new StackPanel { Spacing = 12, Children = {
            Text("文件传输", 16), Text("通过 UU 官方客户端传入文件，或取出已放入此目录的文件。", 13, true),
            Text(HostTransferPaths.SharedDirectory, 12, true), transferState,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { open, add } }
        } });
    }
    private void ShowPage(int index)
    {
        if (pages.Length == 0) return;
        page.Content = pages[index];
        for (var i = 0; i < navigation.Count; i++)
        {
            ApplyTheme(navigation[i], Button.BackgroundProperty, i == index ? "AppAccentSurfaceBrush" : "AppSurfaceBrush");
            ApplyTheme(navigation[i], Button.ForegroundProperty, i == index ? "AppAccentBrush" : "AppMutedBrush");
            if (navigation[i].Content is StackPanel labels)
                foreach (var label in labels.Children.OfType<TextBlock>()) ApplyTheme(label, TextBlock.ForegroundProperty, i == index ? "AppAccentBrush" : "AppMutedBrush");
        }
    }
    private async Task RefreshDevicesAsync()
    {
        if (disposed || refreshing) return;
        refreshing = true; refresh.IsEnabled = false; refresh.Content = "正在同步…";
        try
        {
            var state = DesktopHostSession.ReadIdentity(identity.Text ?? "").State;
            if (!state.IsAuthenticated) throw new InvalidOperationException();
            using var api = new UuMacHostApi(state);
            var loaded = await api.GetDevicesAsync(lifetime.Token);
            if (disposed) return;
            devices = loaded; Console.WriteLine("device-catalog-loaded;count=" + devices.Count); accountState.Text = "已登录 · 与 UU 账号同步";
            catalogHint.Text = $"{devices.Count} 台设备 · {devices.Count(x => x.Online)} 台在线 · 更新于 {DateTime.Now:HH:mm:ss}";
            if (selectedDeviceId is { } selected && devices.FirstOrDefault(x => x.Id == selected) is { } updated) ShowDevice(updated);
            else { selectedDeviceId = null; deviceDetail.Children.Clear(); }
            RenderDevices();
            await RefreshAssistanceAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch
        {
            if (!disposed) catalogHint.Text = devices.Count > 0 ? "刷新失败，保留上次设备列表。请检查网络后重试。" : "暂时无法获取设备，请在“账号与设置”检查登录状态，或点击刷新重试。";
        }
        finally { refreshing = false; if (!disposed) { refresh.IsEnabled = true; refresh.Content = "刷新设备"; } }
    }
    private void RenderDevices()
    {
        deviceRows.Children.Clear();
        var query = search.Text?.Trim() ?? "";
        var matches = devices.Where(d => (query.Length == 0 || (d.Name + " " + d.PlatformName + " " + d.Id).Contains(query, StringComparison.OrdinalIgnoreCase))
            && (filter.SelectedIndex switch { 1 => d.Online, 2 => favorites.Contains(d.Id), 3 => d.Category == "desktop", 4 => d.Category == "mobile", _ => true }))
            .OrderByDescending(d => d.IsCurrent).ThenByDescending(d => d.Online).ThenBy(d => d.Name, StringComparer.CurrentCulture).ToArray();
        catalogCount.Text = devices.Count > 0 ? $"我的设备  {matches.Length}" : "我的设备";
        if (matches.Length == 0)
        {
            deviceRows.Children.Add(Card(new StackPanel { Spacing = 10, Margin = new Thickness(0, 20), Children = {
                Text(devices.Count == 0 ? "还没有加载设备" : "没有符合条件的设备", 18),
                Text(devices.Count == 0 ? "登录后点击刷新，即可查看同一 UU 账号下的电脑和移动设备。" : "试试其他关键词，或将筛选切换为“全部设备”。", 13, true) } })); return;
        }
        foreach (var d in matches)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("48,*,Auto,Auto,Auto,Auto"), ColumnSpacing = 12 };
            var icon = DeviceIcon(d.Category == "mobile"); ApplyTheme(icon, PathIcon.ForegroundProperty, "AppAccentBrush");
            var iconBox = new Border { Child = icon, CornerRadius = new CornerRadius(10), Width = 44, Height = 44 };
            ApplyTheme(iconBox, Border.BackgroundProperty, "AppAccentSurfaceBrush"); row.Children.Add(iconBox);
            var name = Text(d.Name + (d.IsCurrent ? " · 本机" : ""), 15); name.FontWeight = FontWeight.SemiBold; name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
            var info = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { name, Text(d.PlatformName + (d.Online && d.ControlledSupport && d.Controllable ? "  ·  允许被控" : ""), 12, true) } };
            Grid.SetColumn(info, 1); row.Children.Add(info);
            var availability = Text(d.StatusName, 12);
            ApplyTheme(availability, TextBlock.ForegroundProperty, d.Online ? "AppPositiveBrush" : "AppMutedBrush");
            var badge = new Border { Child = availability, Padding = new Thickness(10, 5), CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Center };
            ApplyTheme(badge, Border.BackgroundProperty, d.Online ? "AppPositiveSurfaceBrush" : "AppSubtleBrush");
            Grid.SetColumn(badge, 2); row.Children.Add(badge);
            var star = new Button { Content = favorites.Contains(d.Id) ? "★" : "☆", Width = 36, Height = 36, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(star, favorites.Contains(d.Id) ? "取消收藏" : "收藏设备");
            Avalonia.Automation.AutomationProperties.SetName(star, (favorites.Contains(d.Id) ? "取消收藏 " : "收藏 ") + d.Name);
            star.Click += (_, _) => { if (!favorites.Add(d.Id)) favorites.Remove(d.Id); SaveFavorites(); RenderDevices(); };
            Grid.SetColumn(star, 3); row.Children.Add(star);
            var inspect = new Button { Content = "详情", VerticalAlignment = VerticalAlignment.Center };
            inspect.Click += (_, _) => { ShowDevice(d); RenderDevices(); }; Grid.SetColumn(inspect, 4); row.Children.Add(inspect);
            var deviceCard = Card(row, 18);
            if (selectedDeviceId == d.Id) ApplyTheme(deviceCard, Border.BorderBrushProperty, "AppAccentBrush");
            if (!d.IsCurrent && d.Category == "desktop")
            {
                var connect = new Button { Content = "远程控制", IsEnabled = d.Online && d.ControlledSupport && d.Controllable, VerticalAlignment = VerticalAlignment.Center };
                ApplyTheme(connect, Button.BackgroundProperty, "AppAccentBrush"); ApplyTheme(connect, Button.ForegroundProperty, "AppOnAccentBrush");
                ToolTip.SetTip(connect, connect.IsEnabled ? "在独立窗口中连接" : "设备需在线并允许被控");
                connect.Click += (_, _) => OpenRemote(d); Grid.SetColumn(connect, 5); row.Children.Add(connect);
            }
            deviceRows.Children.Add(deviceCard);
        }
    }
    private void OpenRemote(UuDevice device)
    {
        if (remoteWindows.TryGetValue(device.Id, out var existing)) { existing.Activate(); return; }
        if (device.IsCurrent || !device.Online || !device.Controllable || !device.ControlledSupport) return;
        try
        {
            var state = DesktopHostSession.ReadIdentity(identity.Text ?? "").State;
            if (!state.IsAuthenticated || !File.Exists(encoder.Text)) throw new InvalidOperationException();
            var window = new RemoteDesktopWindow(device, state, encoder.Text!);
            remoteWindows.Add(device.Id, window);
            window.Closed += (_, _) => remoteWindows.Remove(device.Id);
            window.Show();
        }
        catch { catalogHint.Text = "无法打开远控窗口，请检查登录状态与视频解码器路径。"; }
    }
    private void SaveFavorites()
    {
        try { File.WriteAllText(favoriteFile, JsonSerializer.Serialize(favorites)); if (OperatingSystem.IsLinux()) File.SetUnixFileMode(favoriteFile, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (IOException) { catalogHint.Text = "收藏已暂存，但未能写入本机设置。"; }
    }
    private void ShowDevice(UuDevice device)
    {
        selectedDeviceId = device.Id;
        deviceDetail.Children.Clear();
        var copy = new Button { Content = "复制设备编号" };
        copy.Click += async (_, _) =>
        {
            try { if (TopLevel.GetTopLevel(this)?.Clipboard is { } board) { await board.SetTextAsync(device.Id); copy.Content = "已复制"; } }
            catch { copy.Content = "复制失败"; }
        };
        var close = new Button { Content = "收起" }; close.Click += (_, _) => { selectedDeviceId = null; deviceDetail.Children.Clear(); RenderDevices(); };
        deviceDetail.Children.Add(Card(new StackPanel { Spacing = 10, Children = {
            Text(device.Name, 18), Text($"{device.PlatformName} · {device.StatusName}"),
            Text("设备编号  " + device.Id, 12, true), Text("客户端版本  " + (device.Version.Length == 0 ? "未提供" : device.Version), 12, true),
            Text(device.IsCurrent ? "这是本机。可在其他设备的 UU 官方客户端中发起桌面或终端连接。" : "可通过设备列表中的远程控制，在独立窗口中连接。", 13, true),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { copy, close } } } }));
    }
}
