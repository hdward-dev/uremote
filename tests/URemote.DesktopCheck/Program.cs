using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AsterDock.Host;
using AsterDock.Host.Views;

var preview = args.FirstOrDefault();
var moduleUi = args.Contains("--module-ui");
if (moduleUi) Environment.SetEnvironmentVariable("UREMOTE_NO_AUTO_START", "1");
AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().AfterSetup(builder =>
{
    if (args.Contains("--controller-window")) DispatcherTimer.RunOnce(async () =>
    {
        if (builder.Instance?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime life) return;
        var state = URemote.Host.DesktopHostSession.ReadIdentity(Environment.GetEnvironmentVariable("UREMOTE_IDENTITY")!).State;
        using var api = new URemote.Core.UuMacHostApi(state);
        var device = (await api.GetDevicesAsync()).First(x => !x.IsCurrent && x.Online && x.Controllable && x.ControlledSupport && x.Platform == 1);
        var window = new URemote.Module.RemoteDesktopWindow(device, state, Environment.GetEnvironmentVariable("UREMOTE_FFMPEG")!);
        window.Show();
        await Task.Delay(10000);
        var bitmap = typeof(URemote.Module.RemoteDesktopWindow).GetField("bitmap", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window);
        Console.WriteLine(bitmap is null ? "FAIL: remote window did not paint video" : "PASS: independent native remote window painted real remote video");
        window.Close(); await window.Completion;
        if (life.MainWindow is MainWindow main) main.PrepareForShutdown();
        life.Shutdown(bitmap is null ? 1 : 0);
    }, TimeSpan.FromSeconds(2));
    if (moduleUi && !args.Contains("--controller-window")) DispatcherTimer.RunOnce(() =>
    {
        if (builder.Instance?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime life || life.MainWindow is not MainWindow win) return;
        var dir = Path.Combine(Path.GetTempPath(), "uremote-ui-review"); Directory.CreateDirectory(dir);
        var view = new URemote.Module.URemoteView(dir);
        win.MinWidth = 0; win.MinHeight = 0; win.Content = view; win.Width = args.Contains("--narrow") ? 640 : 1440; win.Height = 880;
        if (args.Contains("--dark")) builder.Instance.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        var type = typeof(URemote.Module.URemoteView);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        if (!args.Contains("--empty"))
        {
            type.GetField("devices", flags)!.SetValue(view, new URemote.Core.UuDevice[] {
                // Offline current first in source order must not float above other online devices.
                new("fixture-mac", "MacBook Pro", "desktop", 4, "DISCONNECTED", true, true, "4.41.0", true),
                new("fixture-win", "工作室 Windows", "desktop", 1, "CONNECTED", true, true, "4.41.0", false),
                new("fixture-linux", "aster-nixos", "desktop", 4, "CONNECTED", true, true, "4.41.0", false),
                new("fixture-ios", "iPhone", "mobile", 3, "DISCONNECTED", false, false, "4.41.0", false) });
            type.GetMethod("RenderDevices", flags)!.Invoke(view, null);
            var filter = (Avalonia.Controls.ComboBox)type.GetField("filter", flags)!.GetValue(view)!;
            var rows = (URemote.Module.DeviceCardWrap)type.GetField("deviceRows", flags)!.GetValue(view)!;
            var onlineOrder = rows.Children.OfType<URemote.Module.DeviceCard>()
                .Select(card => ((URemote.Module.DeviceCardModel)card.DataContext!).Online).ToArray();
            if (onlineOrder is not [true, true, false, false])
                throw new Exception("Online devices must sort before offline; got " + string.Join(",", onlineOrder));
            filter.SelectedIndex = 1;
            if (rows.Children.Count != 2) throw new Exception("Online filter failed.");
            filter.SelectedIndex = 0;
            var search = (Avalonia.Controls.TextBox)type.GetField("search", flags)!.GetValue(view)!;
            search.Text = "MacBook";
            type.GetMethod("RenderDevices", flags)!.Invoke(view, null);
            if (rows.Children.Count != 1) throw new Exception("Device search failed.");
            search.Text = "";
            type.GetMethod("RenderDevices", flags)!.Invoke(view, null);
            ((Avalonia.Controls.TextBlock)type.GetField("catalogHint", flags)!.GetValue(view)!).Text = "演示设备 · 4 台设备 · 2 台在线";
            Console.WriteLine("PASS: native device list online-first sort, online filter and name search");
        }
        var selected = args.Contains("--local") ? 5 : args.Contains("--settings") ? 2 : args.Contains("--host") ? 1 : 0;
        if (args.Contains("--local"))
        {
            type.GetMethod("ShowPage", flags)!.Invoke(view, [5]);
            var page = (Avalonia.Controls.ContentControl)type.GetField("page", flags)!.GetValue(view)!;
            var local = (Avalonia.Controls.StackPanel)page.Content!;
            var tabs = (Avalonia.Controls.StackPanel)local.Children[2];
            var content = (Avalonia.Controls.ContentControl)local.Children[3];
            var rdp = (Avalonia.Controls.StackPanel)((Avalonia.Controls.Border)content.Content!).Child!;
            if (((Avalonia.Controls.TextBlock)rdp.Children[2]).Text != "域" || ((Avalonia.Controls.TextBlock)rdp.Children[4]).Text != "用户名")
                throw new Exception("RDP domain must precede username.");
            var endpoint = (Avalonia.Controls.Grid)rdp.Children[1];
            var address = (Avalonia.Controls.TextBox)endpoint.Children[0]; address.Text = "desktop.local";
            if (((Avalonia.Controls.NumericUpDown)endpoint.Children[1]).Value != 3389) throw new Exception("RDP port failed.");
            ((Avalonia.Controls.Button)tabs.Children[1]).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            var vnc = (Avalonia.Controls.StackPanel)((Avalonia.Controls.Border)content.Content!).Child!;
            if (ReferenceEquals(vnc, rdp) || ((Avalonia.Controls.NumericUpDown)((Avalonia.Controls.Grid)vnc.Children[1]).Children[1]).Value != 5900)
                throw new Exception("VNC page switch failed.");
            ((Avalonia.Controls.Button)tabs.Children[0]).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            if (!ReferenceEquals(((Avalonia.Controls.Border)content.Content!).Child, rdp) || address.Text != "desktop.local") throw new Exception("RDP form retention failed.");
            if (args.Contains("--vnc")) ((Avalonia.Controls.Button)tabs.Children[1]).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            Console.WriteLine("PASS: local connection page switches RDP/VNC, sets correct ports and retains form input without UU login");
        }
        if (args.Contains("--sms-cooldown"))
        {
            var owner = URemote.Host.DesktopHostLogin.Open(Path.Combine(dir, "identity.json"));
            typeof(URemote.Host.DesktopHostLogin).GetField("sentAt", flags)!.SetValue(owner, DateTimeOffset.UtcNow.AddSeconds(-58));
            type.GetField("login", flags)!.SetValue(view, owner);
            type.GetMethod("UpdateSendCodeState", flags)!.Invoke(view, null);
            var send = (Avalonia.Controls.Button)type.GetField("sendCode", flags)!.GetValue(view)!;
            if (send.IsEnabled || !send.Content!.ToString()!.Contains("重新发送")) throw new Exception("SMS cooldown did not disable the button.");
            DispatcherTimer.RunOnce(() =>
            {
                if (!send.IsEnabled || send.Content?.ToString() != "发送验证码") throw new Exception("SMS cooldown did not restore the button.");
                Console.WriteLine("PASS: SMS countdown disables resending and restores the button on expiry; no SMS was sent.");
            }, TimeSpan.FromSeconds(3));
        }
        if (args.Contains("--account-signed-in"))
        {
            var profile = new URemote.Core.HostDeviceProfile("fixture", "client", "system", "", "", "", "", "", "", "", "", "", "", [], 96);
            var saved = new URemote.Host.SavedHostIdentity(new(Token: "fixture-token", UserId: "fixture-uu-user-0001", ClientId: "client", DeviceId: "fixture-device"),
                profile, "logged-in", 1, args.Contains("--account-id-only") ? "" : "138****1234");
            type.GetMethod("UpdateAccountDisplay", flags)!.Invoke(view, [saved]);
        }
        type.GetMethod("ShowPage", flags)!.Invoke(view, [selected]);
    }, TimeSpan.FromSeconds(2));
    if (!args.Contains("--controller-window")) DispatcherTimer.RunOnce(() =>
    {
        if (builder.Instance?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime || lifetime.MainWindow is not MainWindow window) return;
        if (preview is not null)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height));
            bitmap.Render(window); bitmap.Save(preview, PngBitmapEncoderOptions.Default);
            Console.WriteLine("AsterDock window rendered.");
            window.PrepareForShutdown(); lifetime.Shutdown();
        }
    }, TimeSpan.FromSeconds(14));
}).StartWithClassicDesktopLifetime([]);
