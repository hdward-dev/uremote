using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using URemote.Core;
using URemote.Host;
using URemote.Linux;

namespace URemote.Module;

public sealed partial class URemoteView
{
    private void UpdateAccountDisplay(SavedHostIdentity? saved)
    {
        var authenticated = saved?.State.IsAuthenticated == true;
        accountState.Text = authenticated ? "●  已登录" : "○  尚未登录";
        ApplyTheme(accountState, TextBlock.ForegroundProperty, authenticated ? "AppPositiveBrush" : "AppMutedBrush");
        accountHeading.Text = authenticated ? "当前登录账号" : "登录 UU 账号";
        accountDescription.Text = authenticated ? "使用同一 UU 账号，即可连接你的其他设备。" : "登录后同步设备，并开启本机远程控制。";
        accountIdentity.Text = authenticated ? string.IsNullOrEmpty(saved!.LoginAccount)
            ? "UU 账号 ID：" + saved.State.UserId : "UU 账号：" + saved.LoginAccount : "";
        accountIdentity.IsVisible = authenticated;
        logoutHint.IsVisible = authenticated;
        logout.IsVisible = authenticated; loginPanel.IsVisible = !authenticated;
    }
    private int _hostPanelPlacement;
    private bool listView;
    private int deviceTab;
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
    private static void SetClass(Control control, string name, bool on)
    {
        if (on) { if (!control.Classes.Contains(name)) control.Classes.Add(name); }
        else control.Classes.Remove(name);
    }
    private void WireShell(string dataDirectory)
    {
        favoriteFile = Path.Combine(dataDirectory, "device-favorites.json");
        try { if (File.Exists(favoriteFile)) favorites = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(favoriteFile)) ?? []; }
        catch (Exception e) when (e is IOException or JsonException) { }
        metrics.FontSize = 12; metrics.Text = OperatingSystem.IsMacOS() ? "多屏桌面 · 键鼠控制" : "双屏桌面 · 远程终端";
        ApplyTheme(metrics, TextBlock.ForegroundProperty, "AppMutedBrush");
        detail.IsVisible = !string.IsNullOrWhiteSpace(detail.Text);
        detail.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) detail.IsVisible = !string.IsNullOrWhiteSpace(detail.Text); };
        status.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) UpdateHostBadge(); };
        screens.ItemsSource = outputs;
        localScreens.ItemsSource = localScreenItems;
        localHistoryRows.ItemsSource = historyItems;
        navigation.AddRange([navDevices, navHost, navAccount, navAssist, navFiles, navLocal]);
        pages = [devicePage, hostPage, accountPage, assistancePage, filePage, localPage];
        hostIntro.Text = $"在其他设备的 UU 官方客户端中，选择「{LinuxDeviceProfile.DeviceName}」连接。";
        footerText.Text = $"U远程 · 星栈远程控制插件  |  桌面环境：{DesktopEnvironmentInfo.Current.DisplayName}";
        desktopEnvironment.Text = DesktopEnvironmentInfo.Current.DisplayName;
        transferPath.Text = HostTransferPaths.SharedDirectory;
        search.TextChanged += (_, _) => RenderDevices();
        filter.SelectionChanged += (_, _) => RenderDevices();
        accountPage.SizeChanged += (_, e) =>
        {
            var compact = e.NewSize.Width < 440;
            accountHeadingGrid.ColumnDefinitions = new(compact ? "Auto,*" : "Auto,*,Auto");
            accountHeadingGrid.RowDefinitions = new(compact ? "Auto,Auto" : "Auto");
            Grid.SetColumn(logout, compact ? 1 : 2); Grid.SetRow(logout, compact ? 1 : 0);
            logout.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            logout.Margin = new Thickness(0, compact ? 12 : 0, 0, 0);
        };
        BuildEncoderSettings();
        WireAssistanceCard();
        WireConnectionCard();
        WireAssistanceConnect();
        InitializeLocalConnection(dataDirectory);
        SizeChanged += (_, e) =>
        {
            var wide = e.NewSize.Width >= 1150;
            var narrow = e.NewSize.Width < 850;
            body.ColumnDefinitions = new(narrow ? "0,*,130" : wide ? "300,*,190" : "270,*,150");
            page.Margin = new Thickness(wide && !narrow ? 24 : 14);
            hostBorder.IsVisible = !narrow;
            // hostPanel moves between two parents of this same window. Do it after the
            // current arrange, so the move is not inside the layout pass that raised SizeChanged.
            var generation = ++_hostPanelPlacement;
            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _hostPanelPlacement) return;
                if (narrow)
                {
                    hostPanel.Margin = new Thickness(0);
                    if (!hostPage.Children.Contains(hostPanel)) { hostScroll.Content = null; hostPage.Children.Insert(2, hostPanel); }
                }
                else
                {
                    if (hostPage.Children.Contains(hostPanel)) { hostPage.Children.Remove(hostPanel); hostScroll.Content = hostPanel; }
                    hostPanel.Margin = new Thickness(14, 12);
                }
            }, DispatcherPriority.Background);
        };
        ShowPage(0); RenderDevices();
        try { UpdateAccountDisplay(DesktopHostSession.ReadIdentity(identity.Text ?? "")); }
        catch { UpdateAccountDisplay(null); }
        deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        deviceTimer.Tick += async (_, _) => { if (IsVisible && page.Content == pages[0] && Environment.GetEnvironmentVariable("UREMOTE_NO_AUTO_START") != "1") await RefreshDevicesAsync(); };
        deviceTimer.Start();
    }
    private void Nav_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && int.TryParse(button.Tag?.ToString(), out var index)) ShowPage(index);
    }
    private void ScreenLink_Click(object? sender, RoutedEventArgs e) => ShowPage(1);
    private async void RefreshDevices_Click(object? sender, RoutedEventArgs e) => await RefreshDevicesAsync();
    private void ViewMode_Click(object? sender, RoutedEventArgs e)
    {
        listView = !listView; viewMode.Content = listView ? "网格" : "列表"; RenderDevices();
    }
    private void DeviceTab_Click(object? sender, RoutedEventArgs e)
    {
        deviceTab = ReferenceEquals(sender, deviceTabFav) ? 1 : 0;
        SetClass(deviceTabAll, "selected", deviceTab == 0); SetClass(deviceTabFav, "selected", deviceTab == 1);
        RenderDevices();
    }
    private void OpenTransfer_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(HostTransferPaths.SharedDirectory);
            var launch = new System.Diagnostics.ProcessStartInfo("xdg-open") { UseShellExecute = false };
            launch.ArgumentList.Add(HostTransferPaths.SharedDirectory); System.Diagnostics.Process.Start(launch)?.Dispose();
        }
        catch { transferState.Text = "无法打开目录，请手动打开下方路径。"; }
    }
    private async void AddTransfer_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = "选择供 UU 官方客户端取出的文件", AllowMultiple = true });
        if (files.Count == 0) return; addTransfer.IsEnabled = false;
        try
        {
            Directory.CreateDirectory(HostTransferPaths.SharedDirectory);
            foreach (var file in files)
            {
                var name = Path.GetFileName(file.Name); var target = Path.Combine(HostTransferPaths.SharedDirectory, name);
                if (File.Exists(target)) target = Path.Combine(HostTransferPaths.SharedDirectory, Path.GetFileNameWithoutExtension(name) + "-" + Guid.NewGuid().ToString("N")[..8] + Path.GetExtension(name));
                var temporary = Path.Combine(HostTransferPaths.SharedDirectory, ".uremote-" + Guid.NewGuid().ToString("N") + ".part");
                try
                {
                    await using (var source = await file.OpenReadAsync())
                    await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                        await source.CopyToAsync(destination, lifetime.Token);
                    File.Move(temporary, target, false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            transferState.Text = $"已添加 {files.Count} 个文件，可在官方客户端刷新目录后取出。";
        }
        catch { transferState.Text = "添加未完成，请检查文件和磁盘空间。"; }
        finally { addTransfer.IsEnabled = true; }
    }
    private static void Place(ContentControl host, Control control)
    {
        if (ReferenceEquals(host.Content, control)) return;
        if (control.Parent is Panel panel) panel.Children.Remove(control);
        host.Content = control;
    }

    private void ShowPage(int index)
    {
        if (pages.Length == 0) return;
        Place(page, pages[index]);
        for (var i = 0; i < navigation.Count; i++) SetClass(navigation[i], "selected", i == index);
    }
    private async Task RefreshDevicesAsync()
    {
        if (disposed || loggingOut || refreshing) return;
        var generation = accountGeneration;
        refreshing = true; refresh.IsEnabled = false; refresh.Content = "正在同步…";
        try
        {
            var saved = DesktopHostSession.ReadIdentity(identity.Text ?? "");
            var state = saved.State;
            if (!state.IsAuthenticated) throw new InvalidOperationException();
            using var api = new UuMacHostApi(state);
            var loaded = await api.GetDevicesAsync(lifetime.Token);
            if (disposed || loggingOut || generation != accountGeneration) return;
            devices = loaded; Console.WriteLine("device-catalog-loaded;count=" + devices.Count); UpdateAccountDisplay(saved);
            catalogHint.Text = $"{devices.Count} 台设备 · {devices.Count(x => x.Online)} 台在线 · 更新于 {DateTime.Now:HH:mm:ss}";
            if (selectedDeviceId is { } selected && devices.FirstOrDefault(x => x.Id == selected) is { } updated) ShowDevice(updated);
            else { selectedDeviceId = null; deviceDetail.Children.Clear(); deviceDetail.IsVisible = false; }
            RenderDevices();
            await Task.WhenAll(RefreshPreviewsAsync(), RefreshAssistanceAsync());
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch
        {
            if (!disposed && !loggingOut && generation == accountGeneration)
            {
                catalogHint.Text = devices.Count > 0 ? "刷新失败，保留上次设备列表。请检查网络后重试。" : "暂时无法获取设备，请在“账号与设置”检查登录状态，或点击刷新重试。";
                ReportFailure(catalogHint.Text, "设备列表");
            }
        }
        finally { refreshing = false; if (!disposed) { refresh.IsEnabled = true; refresh.Content = "刷新"; } }
    }
    private void RenderDevices()
    {
        deviceRows.Children.Clear();
        deviceRows.SingleColumn = listView;
        var query = search.Text?.Trim() ?? "";
        var matches = devices.Where(d => (query.Length == 0 || (d.Name + " " + d.PlatformName + " " + d.Id).Contains(query, StringComparison.OrdinalIgnoreCase))
            && (deviceTab == 0 || favorites.Contains(d.Id)) && (filter.SelectedIndex switch { 1 => d.Online, 2 => favorites.Contains(d.Id), 3 => d.Category == "desktop", 4 => d.Category == "mobile", _ => true }))
            .OrderByDescending(d => d.Online).ThenByDescending(d => d.IsCurrent).ThenBy(d => d.Name, StringComparer.CurrentCulture).ToArray();
        catalogCount.Text = devices.Count > 0 ? $"我的设备  {matches.Length}" : "我的设备";
        if (matches.Length == 0)
        {
            deviceRows.Children.Add(new CatalogMessageCard { DataContext = new CatalogMessageModel {
                Title = devices.Count == 0 ? "还没有加载设备" : "没有符合条件的设备",
                Message = devices.Count == 0 ? "登录后点击刷新，即可查看同一 UU 账号下的电脑和移动设备。" : "试试其他关键词，或将筛选切换为“全部设备”。" } });
            return;
        }
        foreach (var device in matches) deviceRows.Children.Add(BuildPreviewCard(device));
    }

    private DeviceCard BuildPreviewCard(UuDevice device)
    {
        var allowed = device.CanConnect && device.Category == "desktop";
        var wallpaperKey = device.IsCurrent && previewCache.ContainsKey("local-wallpaper") ? "local-wallpaper" : device.WallpaperUrl;
        previewCache.TryGetValue(wallpaperKey, out var wallpaper);
        return new DeviceCard { DataContext = new DeviceCardModel {
            Name = device.Name,
            StatusText = "●  " + device.ConnectionStatusName,
            PlatformName = device.PlatformName,
            BadgeText = device.IsCurrent ? "本机" : device.ConnectionStatusName,
            FavoriteLabel = favorites.Contains(device.Id) ? "取消收藏" : "收藏设备",
            Online = device.Online,
            HasWallpaper = wallpaper is not null,
            Wallpaper = wallpaper,
            WallpaperOpacity = device.Online ? 1 : .5,
            IsMobile = device.Category == "mobile",
            ShowBadge = device.IsCurrent || !device.Online || device.Busy,
            CanControl = allowed,
            CanOpenRemote = allowed || (device.Busy && !device.IsCurrent && device.Controllable && device.ControlledSupport && device.Category == "desktop"),
            ListView = listView,
            PreviewHeight = listView ? 120 : 190,
            IsSelected = selectedDeviceId == device.Id,
            ConnectTip = allowed ? "点击预览进入远程控制" : device.Busy ? "点击接管设备，将断开原连接并建立新连接" : device.ConnectionStatusName,
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
    private readonly HashSet<string> pendingTakeovers = [];
    private async void OpenRemote(UuDevice device)
    {
        if (toolWindows.ContainsKey(device.Id)) { catalogHint.Text = "请先关闭此设备的终端或文件窗口，再发起桌面连接。"; return; }
        if (remoteWindows.TryGetValue(device.Id, out var existing)) { existing.Activate(); return; }
        if (pendingTakeovers.Contains(device.Id)) return;
        var forceJoin = false;
        if (device.Busy && !device.IsCurrent && device.Online && device.Controllable && device.ControlledSupport)
        {
            pendingTakeovers.Add(device.Id);
            try { forceJoin = await ConfirmTakeoverAsync(device); }
            finally { pendingTakeovers.Remove(device.Id); }
            if (!forceJoin || disposed || loggingOut) return;
            if (remoteWindows.TryGetValue(device.Id, out existing)) { existing.Activate(); return; }
            if (toolWindows.ContainsKey(device.Id)) return;
        }
        else if (!device.CanConnect) { catalogHint.Text = "设备当前不可连接：" + device.ConnectionStatusName; return; }
        try
        {
            var state = DesktopHostSession.ReadIdentity(identity.Text ?? "").State;
            if (!state.IsAuthenticated || !File.Exists(appliedEncoder)) throw new InvalidOperationException();
            var window = new RemoteDesktopWindow(device, state, appliedEncoder, feedback: appContext?.Feedback, forceJoin: forceJoin);
            remoteWindows.Add(device.Id, window);
            window.Closed += async (_, _) => { try { await window.Completion; } finally { remoteWindows.Remove(device.Id); } };
            ShowManaged(window);
        }
        catch { catalogHint.Text = "无法打开远控窗口，请检查登录状态与视频解码器路径。"; ReportFailure(catalogHint.Text, "远程桌面"); }
    }
    private async Task<bool> ConfirmTakeoverAsync(UuDevice device)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        var dialog = new Window { Title = "接管设备", Width = 420, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var confirm = new Button { Content = "接管设备" };
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12 };
        actions.Children.Add(cancel); actions.Children.Add(confirm);
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 20 };
        panel.Children.Add(new TextBlock { Text = "设备「" + device.Name + "」正在被控。", FontSize = 16, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "接管设备会断开原连接，并建立新的远程连接。是否继续？", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(actions); dialog.Content = panel;
        return await dialog.ShowDialog<bool>(owner);
    }
    private void OpenTool(UuDevice device, bool terminal)
    {
        if (remoteWindows.ContainsKey(device.Id)) { catalogHint.Text = "请先断开此设备的桌面，再打开终端或文件传输。"; return; }
        if (toolWindows.TryGetValue(device.Id, out var existing)) { existing.Activate(); catalogHint.Text = "请先关闭该设备已有的工具窗口，再切换连接类型。"; return; }
        if (!device.CanConnect) { catalogHint.Text = device.Busy ? "设备正在被控，请等待现有连接结束后刷新。" : "设备当前不可连接：" + device.ConnectionStatusName; return; }
        try
        {
            var state = DesktopHostSession.ReadIdentity(identity.Text ?? "").State;
            if (!state.IsAuthenticated) throw new InvalidOperationException();
            var window = new RemoteToolsWindow(device, state, terminal, appContext?.Feedback); toolWindows.Add(device.Id, window);
            window.Closed += async (_, _) => { try { await window.Completion; } finally { toolWindows.Remove(device.Id); } };
            ShowManaged(window);
        }
        catch { catalogHint.Text = "无法打开工具窗口，请检查登录状态。"; ReportFailure(catalogHint.Text, "远程工具"); }
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
        var detailView = new DeviceDetailView { Feedback = appContext?.Feedback, DataContext = new DeviceDetailModel {
            Name = device.Name,
            Summary = $"{device.PlatformName} · {device.ConnectionStatusName}",
            Identifier = "设备编号  " + device.Id,
            Version = "客户端版本  " + (device.Version.Length == 0 ? "未提供" : device.Version),
            Description = device.IsCurrent ? "这是本机。可在其他设备的 UU 官方客户端中发起桌面或终端连接。" : "可通过设备列表中的远程控制，在独立窗口中连接。",
            DeviceId = device.Id
        } };
        detailView.Dismissed += (_, _) => { selectedDeviceId = null; deviceDetail.Children.Clear(); deviceDetail.IsVisible = false; RenderDevices(); };
        deviceDetail.Children.Add(detailView);
    }
}
