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
    private readonly Grid deviceRows = new() { ColumnSpacing = 16, RowSpacing = 16 };
    private int deviceColumns = 2;
    private bool listView;
    private int deviceTab;
    private readonly StackPanel deviceDetail = new() { Spacing = 10, IsVisible = false };
    private readonly ContentControl page = new();
    private readonly StackPanel advancedSettings = new() { Spacing = 12 };
    private readonly List<Button> navigation = [];
    private string? selectedDeviceId;
    private readonly Dictionary<string, RemoteDesktopWindow> remoteWindows = [];
    private readonly Dictionary<string, RemoteToolsWindow> toolWindows = [];
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
        Resources.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary {
            ["AppAccentBrush"] = new SolidColorBrush(Color.Parse("#008D82")),
            ["AppAccentSurfaceBrush"] = new SolidColorBrush(Color.Parse("#E1F2EF")) };
        Resources.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary {
            ["AppAccentBrush"] = new SolidColorBrush(Color.Parse("#43CEB9")),
            ["AppAccentSurfaceBrush"] = new SolidColorBrush(Color.Parse("#173F3A")) };
        FontFamily = new FontFamily(OperatingSystem.IsLinux() ? "Noto Sans CJK SC" : "Segoe UI");
        Styles.Add(new Style(x => x.OfType<TextBlock>()) { Setters = { new Setter(TextBlock.FontFamilyProperty, FontFamily) } });
        favoriteFile = Path.Combine(dataDirectory, "device-favorites.json");
        try { if (File.Exists(favoriteFile)) favorites = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(favoriteFile)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException) { }
        ApplyTheme(this, BackgroundProperty, "AppPageBrush");
        ApplyTheme(status, TextBlock.ForegroundProperty, "AppStrongTextBrush"); status.FontSize = 19; status.TextWrapping = TextWrapping.Wrap;
        ApplyTheme(detail, TextBlock.ForegroundProperty, "AppMutedBrush"); detail.FontSize = 13;
        detail.IsVisible = !string.IsNullOrWhiteSpace(detail.Text);
        detail.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) detail.IsVisible = !string.IsNullOrWhiteSpace(detail.Text); };
        ApplyTheme(metrics, TextBlock.ForegroundProperty, "AppMutedBrush"); metrics.FontSize = 12; metrics.Text = "双屏桌面 · 远程终端";
        ApplyTheme(hostBadge, TextBlock.ForegroundProperty, "AppPositiveBrush");
        status.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) UpdateHostBadge(); };
        var brand = new StackPanel { Spacing = 5, Margin = new Thickness(14, 12, 0, 30), Children = { Text("U远程", 25), Text("远程工作空间", 12, true) } };
        var tabs = new StackPanel { Spacing = 8 };
        foreach (var item in new[] { ("我的设备", "▤", 0), ("本机被控", "▣", 1), ("账号与设置", "⚙", 2), ("远程协助", "♧", 3), ("文件传输", "⇄", 4) })
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Text(item.Item2, 18), Text(item.Item1, 14) } };
            var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 13), CornerRadius = new CornerRadius(9) };
            button.Click += (_, _) => ShowPage(item.Item3); navigation.Add(button);
        }
        foreach (var i in new[] { 0, 1, 3, 4 }) tabs.Children.Add(navigation[i]);
        var sidebar = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);
        DockPanel.SetDock(navigation[2], Dock.Bottom); sidebar.Children.Add(navigation[2]); sidebar.Children.Add(tabs);
        var toolbar = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        search.PlaceholderText = "搜索设备名称"; filter.Width = 112; refresh.Content = "刷新";
        toolbar.Children.Add(search); Grid.SetColumn(filter, 1); toolbar.Children.Add(filter); Grid.SetColumn(refresh, 2); toolbar.Children.Add(refresh);
        var viewMode = new Button { Content = "列表", MinWidth = 50 };
        viewMode.Click += (_, _) => { listView = !listView; viewMode.Content = listView ? "网格" : "列表"; RenderDevices(); };
        Grid.SetColumn(viewMode, 3); toolbar.Children.Add(viewMode);
        ApplyTheme(catalogCount, TextBlock.ForegroundProperty, "AppStrongTextBrush"); catalogCount.FontSize = 28;
        ApplyTheme(catalogHint, TextBlock.ForegroundProperty, "AppMutedBrush"); catalogHint.FontSize = 12;
        var deviceTabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (title, index) in new[] { ("全部设备", 0), ("收藏", 1) })
        {
            var button = new Button { Content = title, Padding = new Thickness(16, 8), CornerRadius = new CornerRadius(18) };
            button.Click += (_, _) => { deviceTab = index; foreach (var child in deviceTabs.Children.OfType<Button>()) ApplyTheme(child, Button.BackgroundProperty, child == button ? "AppAccentSurfaceBrush" : "AppSurfaceBrush"); RenderDevices(); };
            ApplyTheme(button, Button.BackgroundProperty, index == 0 ? "AppAccentSurfaceBrush" : "AppSurfaceBrush"); deviceTabs.Children.Add(button);
        }
        var devicePage = new StackPanel { Spacing = 16, Children = { catalogCount, catalogHint, toolbar, deviceTabs, deviceDetail, deviceRows,
            Text("预览为设备上传的桌面壁纸，非实时画面。远程控制将在独立窗口中打开。", 12, true) } };
        search.TextChanged += (_, _) => RenderDevices(); filter.SelectionChanged += (_, _) => RenderDevices();
        refresh.Click += async (_, _) => await RefreshDevicesAsync();
        duration.HorizontalAlignment = HorizontalAlignment.Stretch;
        settings.Children.Add(Text("共享哪些屏幕", 16)); settings.Children.Add(screens);
        settings.Children.Add(new Separator()); settings.Children.Add(Text("连接权限", 16));
        settings.Children.Add(input); settings.Children.Add(audio); settings.Children.Add(clipboard);
        settings.Children.Add(Text("保持被控的时长", 13, true)); settings.Children.Add(duration);
        var hostPage = new StackPanel { Spacing = 16, Children = {
            Text("本机被控", 22), Text($"在其他设备的 UU 官方客户端中，选择「{LinuxDeviceProfile.DeviceName}」连接。", 13, true), Card(settings),
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
        var assistancePage = BuildConnectAssistancePage();
        pages = [devicePage, hostPage, accountPage, assistancePage, new StackPanel { Spacing = 16, Children = { Text("文件传输", 26), BuildFileTransferCard() } }];
        var hostActions = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new Thickness(0, 8) };
        hostActions.Children.Add(Text("允许本机被控", 14)); Grid.SetColumn(hostSwitch, 1); hostActions.Children.Add(hostSwitch);
        hostSwitch.MinWidth = 0;
        var screenLink = new Button { Content = "显示器与共享设置", HorizontalAlignment = HorizontalAlignment.Stretch };
        screenLink.Click += (_, _) => ShowPage(1);
        var hostHeading = new Grid { ColumnDefinitions = new("Auto,*") };
        hostHeading.Children.Add(Text("本机被控", 20));
        hostBadge.HorizontalAlignment = HorizontalAlignment.Right;
        hostBadge.VerticalAlignment = VerticalAlignment.Center;
        hostBadge.Margin = new Thickness(8, 0, 0, 0);
        hostBadge.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(hostBadge, 1); hostHeading.Children.Add(hostBadge);
        var right = new StackPanel { Spacing = 18, Children = { hostHeading, hostActions,
            localScreens, detail, screenLink, new Separator(), Text("让他人协助我", 20), BuildAssistanceCard(), BuildConnectionCard() } };
        status.FontSize = 14; detail.FontSize = 12;
        var body = new Grid { ColumnDefinitions = new("190,*,300"), RowDefinitions = new("*,Auto") };
        var leftBorder = new Border { Child = sidebar, BorderThickness = new Thickness(0, 0, 1, 0) };
        ApplyTheme(leftBorder, Border.BorderBrushProperty, "AppBorderBrush");
        var scroll = new ScrollViewer { Margin = new Thickness(24), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = page };
        var rightBorder = new Border { Padding = new Thickness(20), BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { Content = right, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } };
        ApplyTheme(rightBorder, Border.BorderBrushProperty, "AppBorderBrush"); ApplyTheme(rightBorder, Border.BackgroundProperty, "AppSurfaceBrush");
        body.Children.Add(leftBorder); Grid.SetColumn(scroll, 1); body.Children.Add(scroll); Grid.SetColumn(rightBorder, 2); body.Children.Add(rightBorder);
        var footer = new Border { Padding = new Thickness(18, 8), BorderThickness = new Thickness(0, 1, 0, 0), Child = Text("U远程 · 星栈远程控制插件", 11, true) };
        ApplyTheme(footer, Border.BorderBrushProperty, "AppBorderBrush"); Grid.SetRow(footer, 1); Grid.SetColumnSpan(footer, 3); body.Children.Add(footer);
        Content = body;
        SizeChanged += (_, e) =>
        {
            var wide = e.NewSize.Width >= 1150;
            body.ColumnDefinitions = new(wide ? "190,*,300" : "150,*,270");
            scroll.Margin = new Thickness(wide ? 24 : 14);
            if (e.NewSize.Width < 850)
            {
                body.ColumnDefinitions = new("130,*,0"); rightBorder.IsVisible = false;
                if (!hostPage.Children.Contains(right)) { if (rightBorder.Child is ScrollViewer rs) rs.Content = null; rightBorder.Child = null; hostPage.Children.Insert(2, right); }
            }
            else
            {
                if (hostPage.Children.Contains(right)) { hostPage.Children.Remove(right); rightBorder.Child = new ScrollViewer { Content = right, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }; }
                rightBorder.IsVisible = true;
            }
            var columns = e.NewSize.Width >= 1150 ? 2 : 1;
            if (columns != deviceColumns) { deviceColumns = columns; RenderDevices(); }
        };
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
            else { selectedDeviceId = null; deviceDetail.Children.Clear(); deviceDetail.IsVisible = false; }
            RenderDevices();
            await Task.WhenAll(RefreshPreviewsAsync(), RefreshAssistanceAsync());
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch
        {
            if (!disposed) catalogHint.Text = devices.Count > 0 ? "刷新失败，保留上次设备列表。请检查网络后重试。" : "暂时无法获取设备，请在“账号与设置”检查登录状态，或点击刷新重试。";
        }
        finally { refreshing = false; if (!disposed) { refresh.IsEnabled = true; refresh.Content = "刷新"; } }
    }
    private void RenderDevices()
    {
        deviceRows.Children.Clear();
        deviceRows.ColumnDefinitions = new("*"); deviceRows.RowDefinitions = new("Auto");
        var query = search.Text?.Trim() ?? "";
        var matches = devices.Where(d => (query.Length == 0 || (d.Name + " " + d.PlatformName + " " + d.Id).Contains(query, StringComparison.OrdinalIgnoreCase))
            && (deviceTab == 0 || favorites.Contains(d.Id)) && (filter.SelectedIndex switch { 1 => d.Online, 2 => favorites.Contains(d.Id), 3 => d.Category == "desktop", 4 => d.Category == "mobile", _ => true }))
            .OrderByDescending(d => d.IsCurrent).ThenByDescending(d => d.Online).ThenBy(d => d.Name, StringComparer.CurrentCulture).ToArray();
        catalogCount.Text = devices.Count > 0 ? $"我的设备  {matches.Length}" : "我的设备";
        if (matches.Length == 0)
        {
            deviceRows.Children.Add(Card(new StackPanel { Spacing = 10, Margin = new Thickness(0, 20), Children = {
                Text(devices.Count == 0 ? "还没有加载设备" : "没有符合条件的设备", 18),
                Text(devices.Count == 0 ? "登录后点击刷新，即可查看同一 UU 账号下的电脑和移动设备。" : "试试其他关键词，或将筛选切换为“全部设备”。", 13, true) } })); return;
        }
        var columns = listView ? 1 : deviceColumns;
        deviceRows.ColumnDefinitions = new ColumnDefinitions(columns == 2 ? "*,*" : "*");
        deviceRows.RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", (matches.Length + columns - 1) / columns)));
        for (var i = 0; i < matches.Length; i++)
        {
            var card = BuildPreviewCard(matches[i]);
            Grid.SetColumn(card, i % columns); Grid.SetRow(card, i / columns); deviceRows.Children.Add(card);
        }
    }
    private void OpenRemote(UuDevice device)
    {
        if (toolWindows.ContainsKey(device.Id)) { catalogHint.Text = "请先关闭此设备的终端或文件窗口，再发起桌面连接。"; return; }
        if (remoteWindows.TryGetValue(device.Id, out var existing)) { existing.Activate(); return; }
        if (device.IsCurrent || !device.Online || !device.Controllable || !device.ControlledSupport) return;
        try
        {
            var state = DesktopHostSession.ReadIdentity(identity.Text ?? "").State;
            if (!state.IsAuthenticated || !File.Exists(encoder.Text)) throw new InvalidOperationException();
            var window = new RemoteDesktopWindow(device, state, encoder.Text!);
            remoteWindows.Add(device.Id, window);
            window.Closed += async (_, _) => { try { await window.Completion; } finally { remoteWindows.Remove(device.Id); } };
            window.Show();
        }
        catch { catalogHint.Text = "无法打开远控窗口，请检查登录状态与视频解码器路径。"; }
    }
    private void OpenTool(UuDevice device, bool terminal)
    {
        if (remoteWindows.ContainsKey(device.Id)) { catalogHint.Text = "请先断开此设备的桌面，再打开终端或文件传输。"; return; }
        if (toolWindows.TryGetValue(device.Id, out var existing)) { existing.Activate(); catalogHint.Text = "请先关闭该设备已有的工具窗口，再切换连接类型。"; return; }
        if (device.IsCurrent || !device.Online || !device.Controllable || !device.ControlledSupport) return;
        try
        {
            var state = DesktopHostSession.ReadIdentity(identity.Text ?? "").State;
            if (!state.IsAuthenticated) throw new InvalidOperationException();
            var window = new RemoteToolsWindow(device, state, terminal); toolWindows.Add(device.Id, window);
            window.Closed += async (_, _) => { try { await window.Completion; } finally { toolWindows.Remove(device.Id); } };
            window.Show();
        }
        catch { catalogHint.Text = "无法打开工具窗口，请检查登录状态。"; }
    }
    private void SaveFavorites()
    {
        try { File.WriteAllText(favoriteFile, JsonSerializer.Serialize(favorites)); if (OperatingSystem.IsLinux()) File.SetUnixFileMode(favoriteFile, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (IOException) { catalogHint.Text = "收藏已暂存，但未能写入本机设置。"; }
    }
    private void ShowDevice(UuDevice device)
    {
        selectedDeviceId = device.Id;
        deviceDetail.IsVisible = true;
        deviceDetail.Children.Clear();
        var copy = new Button { Content = "复制设备编号" };
        copy.Click += async (_, _) =>
        {
            try { if (TopLevel.GetTopLevel(this)?.Clipboard is { } board) { await board.SetTextAsync(device.Id); copy.Content = "已复制"; } }
            catch { copy.Content = "复制失败"; }
        };
        var close = new Button { Content = "收起" }; close.Click += (_, _) => { selectedDeviceId = null; deviceDetail.Children.Clear(); deviceDetail.IsVisible = false; RenderDevices(); };
        deviceDetail.Children.Add(Card(new StackPanel { Spacing = 10, Children = {
            Text(device.Name, 18), Text($"{device.PlatformName} · {device.StatusName}"),
            Text("设备编号  " + device.Id, 12, true), Text("客户端版本  " + (device.Version.Length == 0 ? "未提供" : device.Version), 12, true),
            Text(device.IsCurrent ? "这是本机。可在其他设备的 UU 官方客户端中发起桌面或终端连接。" : "可通过设备列表中的远程控制，在独立窗口中连接。", 13, true),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { copy, close } } } }));
    }
}
