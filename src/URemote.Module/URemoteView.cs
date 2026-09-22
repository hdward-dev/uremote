using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using URemote.Host;
using URemote.Linux;

namespace URemote.Module;
public sealed partial class URemoteView : UserControl, IDisposable
{
    private readonly TextBlock status = new() { Text = "准备启用被控", FontSize = 24, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock detail = new() { Text = "正在读取登录状态和显示器…", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock metrics = new();
    private readonly TextBox identity = new() { PlaceholderText = "本机登录文件路径" };
    private readonly TextBox encoder = new() { PlaceholderText = "FFmpeg 可执行文件路径" };
    private readonly WrapPanel screens = new() { Orientation = Orientation.Horizontal };
    private readonly CheckBox input = new() { Content = "允许键鼠控制和远程终端", IsChecked = true };
    private readonly CheckBox audio = new() { Content = "共享系统声音（不采集麦克风）", IsChecked = true };
    private readonly CheckBox clipboard = new() { Content = "同步文本剪贴板", IsChecked = true };
    private readonly ComboBox duration = new() { ItemsSource = new[] { "保持开启，直到停止", "5 分钟", "30 分钟", "1 小时" }, SelectedIndex = 0 };
    private readonly ToggleSwitch hostSwitch = new() { OnContent = null, OffContent = null, IsEnabled = false };
    private bool updatingHostSwitch;
    private void SetHostSwitch(bool enabled)
    {
        updatingHostSwitch = true;
        try { hostSwitch.IsChecked = enabled; }
        finally { updatingHostSwitch = false; }
    }
    private readonly StackPanel settings = new() { Spacing = 14 };
    private readonly List<(uint Id, CheckBox Check)> outputs = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly string settingsFile;
    private Task? session;
    private DesktopHostLogin? login;
    private bool loginBusy;
    private readonly TextBox mobile = new() { PlaceholderText = "手机号（+86）", MaxLength = 11 };
    private readonly TextBox code = new() { PlaceholderText = "短信验证码", MaxLength = 10, PasswordChar = '●' };
    private readonly Button sendCode = new() { Content = "发送验证码" };
    private readonly Button completeLogin = new() { Content = "登录并启用被控" };
    private readonly StackPanel loginPanel = new() { Spacing = 10, IsVisible = false };
    private CancellationTokenSource? sessionStop;
    private bool disposed, restoreFailed, starting;
    private bool hostEnabled = true;
    private readonly System.Runtime.InteropServices.PosixSignalRegistration? termination;
    private readonly Task initialize;
    private sealed record Settings(string Identity, string Encoder, bool AllowAssistance = true, bool HostEnabled = true);

    public URemoteView(string dataDirectory)
    {
        if (OperatingSystem.IsLinux()) termination = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                lifetime.Cancel();
                _ = Task.Run(async () => { try { if (session is not null) await session; } finally { Environment.Exit(0); } });
            });
        settingsFile = Path.Combine(dataDirectory, "settings.json");
        identity.Text = Environment.GetEnvironmentVariable("UREMOTE_IDENTITY") ?? Path.Combine(dataDirectory, "identity.json");
        encoder.Text = Environment.GetEnvironmentVariable("UREMOTE_FFMPEG") ?? FindExecutable("ffmpeg");
        try { if (File.Exists(settingsFile) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsFile)) is { } saved)
            { identity.Text = saved.Identity; encoder.Text = saved.Encoder; allowAssistance = saved.AllowAssistance; hostEnabled = saved.HostEnabled; } } catch (IOException) { } catch (JsonException) { }
        try { URemote.Core.HostAssistance.SetEnabled(DesktopHostSession.ReadIdentity(identity.Text!).State.DeviceId, allowAssistance); } catch { }
        BuildInterface(dataDirectory);
        sendCode.Click += async (_, _) => await LoginAsync(false);
        completeLogin.Click += async (_, _) => await LoginAsync(true);
        Avalonia.Automation.AutomationProperties.SetName(hostSwitch, "允许本机被远程控制");
        hostSwitch.PropertyChanged += (_, e) =>
        {
            if (e.Property != ToggleSwitch.IsCheckedProperty || updatingHostSwitch) return;
            if (hostSwitch.IsChecked == true) Start(); else Stop();
        };
        initialize = InitializeAsync();
    }
    private static string FindExecutable(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists) ?? "";
    private async Task InitializeAsync()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("UREMOTE_NO_AUTO_START") != "1") _ = RefreshDevicesAsync();
            if (!OperatingSystem.IsLinux()) { status.Text = "当前被控后端仅支持 Linux / Wayland"; hostSwitch.IsEnabled = false; return; }
            var globals = await WaylandCapabilities.DiscoverAsync(lifetime.Token);
            if (disposed) return;
            foreach (var id in globals.Where(g => g.Interface == "wl_output").Select(g => g.Name).Order().Take(5))
            {
                var check = new CheckBox { Content = "显示屏 " + (outputs.Count + 1), IsChecked = true, Margin = new Thickness(0, 0, 16, 8) };
                outputs.Add((id, check)); screens.Children.Add(check);
            }
            _ = RefreshLocalScreensAsync();
            hostSwitch.IsEnabled = true;
            if (hostEnabled && Environment.GetEnvironmentVariable("UREMOTE_NO_AUTO_START") != "1") Start();
            else { hostBadge.Text = "○  被控已关闭"; ApplyTheme(hostBadge, TextBlock.ForegroundProperty, "AppMutedBrush"); status.Text = "被控已关闭"; detail.Text = "可手动开启被控。"; }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { if (!disposed) { hostBadge.Text = "○  被控不可用"; ApplyTheme(hostBadge, TextBlock.ForegroundProperty, "AppMutedBrush"); status.Text = "无法访问显示器"; detail.Text = "需要在支持 screencopy 的 Wayland 桌面会话中打开。"; } }
    }
    private async void Start()
    {
        if (disposed || starting || session is { IsCompleted: false }) return;
        starting = true;
        hostSwitch.IsEnabled = false;
        try
        {
            var globals = await WaylandCapabilities.DiscoverAsync(lifetime.Token);
            if (disposed) return;
            var current = globals.Where(g => g.Interface == "wl_output").Select(g => g.Name).Order().Take(5).ToArray();
            if (!outputs.Select(o => o.Id).SequenceEqual(current))
            {
                var allSelected = outputs.All(o => o.Check.IsChecked == true);
                var selections = outputs.Select(o => o.Check.IsChecked == true).ToArray();
                outputs.Clear(); screens.Children.Clear();
                for (var i = 0; i < current.Length; i++)
                {
                    var check = new CheckBox { Content = "显示屏 " + (i + 1),
                        IsChecked = allSelected || (i < selections.Length && selections[i]),
                        Margin = new Thickness(0, 0, 16, 8) };
                    outputs.Add((current[i], check)); screens.Children.Add(check);
                }
                Report("display-list-refreshed;count=" + current.Length);
            }
            _ = RefreshLocalScreensAsync();
            StartCore();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e)
        {
            Console.WriteLine("host-display-discovery-failed;type=" + e.GetType().Name);
            if (!disposed) { SetHostSwitch(false); status.Text = "无法访问显示器"; detail.Text = "请确认显示器已连接，并在 Wayland 桌面中重新开启被控。"; }
        }
        finally { starting = false; if (!disposed) hostSwitch.IsEnabled = true; }
    }
    private void StartCore()
    {
        if (disposed || session is { IsCompleted: false }) return;
        var selected = outputs.Where(x => x.Check.IsChecked == true).Select(x => x.Id).ToArray();
        var path = identity.Text ?? ""; var ffmpeg = encoder.Text ?? "";
        try
        {
            if (!DesktopHostSession.ReadIdentity(path).State.IsAuthenticated) throw new InvalidOperationException();
            loginPanel.IsVisible = false;
            if ((outputs.Count > 0 && selected.Length == 0) || !File.Exists(ffmpeg) || !Path.IsPathFullyQualified(ffmpeg)) throw new InvalidOperationException();
            File.WriteAllText(settingsFile, JsonSerializer.Serialize(new Settings(path, ffmpeg, allowAssistance, true)));
            hostEnabled = true;
        }
        catch
        {
            bool authenticated;
            try { authenticated = DesktopHostSession.ReadIdentity(path).State.IsAuthenticated; } catch { authenticated = false; }
            loginPanel.IsVisible = !authenticated;
            SetHostSwitch(false);
            if (!authenticated) ShowPage(2);
            hostBadge.Text = "○  尚未就绪"; ApplyTheme(hostBadge, TextBlock.ForegroundProperty, "AppMutedBrush");
            status.Text = "尚未就绪"; detail.Text = authenticated ? "请选择屏幕并检查编码器路径。" : "请先登录 UU 账号，验证码仅在点击发送后请求。"; return;
        }
        var indices = outputs.Count == 0 ? null : outputs.Select((o, i) => (o, i)).Where(x => x.o.Check.IsChecked == true).Select(x => x.i).ToArray();
        var minutes = duration.SelectedIndex switch { 1 => 5, 2 => 30, 3 => 60, _ => 0 };
        var limit = minutes == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(minutes);
        var enabled = input.IsChecked == true; var sound = audio.IsChecked == true; var sync = clipboard.IsChecked == true;
        sessionStop?.Dispose(); sessionStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = sessionStop.Token; restoreFailed = false;
        SetHostSwitch(true); hostSwitch.IsEnabled = true; settings.IsEnabled = false; advancedSettings.IsEnabled = false;
        hostBadge.Text = "●  正在上线"; ApplyTheme(hostBadge, TextBlock.ForegroundProperty, "AppWarningBrush");
        status.Text = "正在上线"; detail.Text = "正在连接 UU 服务…";
        session = Task.Run(async () =>
        {
            try { await DesktopHostSession.RunAsync(path, ffmpeg, selected, enabled, limit, Report, token, sound, sync, indices); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception e)
            {
                Console.WriteLine("host-start-failed;type=" + e.GetType().Name + ";site=" + e.TargetSite?.Name);
                var displayChanged = e is InvalidOperationException && e.Message == "显示器已变化，请重新选择。";
                Post(() => { if (!restoreFailed) { status.Text = displayChanged ? "显示器已变化" : "连接已停止";
                    detail.Text = displayChanged ? "请重新开启被控，将自动读取当前显示器。" : "请检查网络、登录状态或是否已有另一被控实例运行。"; } });
            }
            finally { Post(() => { session = null; SetHostSwitch(false); hostSwitch.IsEnabled = true; hostBadge.Text = "○  被控已关闭"; ApplyTheme(hostBadge, TextBlock.ForegroundProperty, "AppMutedBrush"); settings.IsEnabled = true; advancedSettings.IsEnabled = true; }); }
        });
    }
    private async Task LoginAsync(bool complete)
    {
        if (disposed || loginBusy || session is { IsCompleted: false }) return;
        loginBusy = true; sendCode.IsEnabled = completeLogin.IsEnabled = false; hostSwitch.IsEnabled = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            login ??= DesktopHostLogin.Open(identity.Text ?? "");
            if (complete)
            {
                var value = code.Text ?? ""; code.Text = "";
                await login.CompleteAsync(value, deadline.Token);
                await login.DisposeAsync(); login = null;
                mobile.Text = ""; loginPanel.IsVisible = false;
                _ = RefreshDevicesAsync(); ShowPage(0);
                detail.Text = "登录完成，正在启用被控。";
                Start();
            }
            else
            {
                await login.SendCodeAsync(mobile.Text?.Trim() ?? "", deadline.Token);
                detail.Text = "验证码请求已发送。请输入收到的验证码；不会自动重发。";
            }
        }
        catch { if (!disposed) detail.Text = "登录操作未完成，请检查手机号、验证码和网络；重新发送至少间隔 60 秒。"; }
        finally { loginBusy = false; if (!disposed) { sendCode.IsEnabled = completeLogin.IsEnabled = true; hostSwitch.IsEnabled = true; } }
    }
    private void Report(string value)
    {
        // Only structural event names/counts are emitted by the host; never clipboard or input values.
        Console.WriteLine(DateTimeOffset.UtcNow.ToString("HH:mm:ss") + " " + value);
        Post(() =>
    {
        switch (value)
        {
            case "file-transfer-received": transferState.Text = "文件已接收，已保存到接收目录。"; break;
            case "file-transfer-sent": transferState.Text = "文件已发送，对方已确认收到。"; break;
            case "file-transfer-send-failed": transferState.Text = "发送未完成，请在官方客户端重新尝试。"; break;
            case "device-profile-refreshed": _ = RefreshDevicesAsync(); break;
            case "audio-streaming": metrics.Text = "系统声音正在传输"; break;
            case "audio-unavailable": detail.Text = "音频未能启动，视频与键鼠仍可使用。"; break;
            case "clipboard-unavailable": detail.Text = "剪贴板不可用，请检查 wl-clipboard。"; break;
            case "clipboard-received": metrics.Text = "已接收文本剪贴板"; break;
            case "clipboard-sent": metrics.Text = "已发送文本剪贴板"; break;
            case "waiting-for-display": status.Text = "等待显示器恢复"; detail.Text = "被控保持开启，显示器恢复后会自动重连。"; break;
            case "publisher-reconnecting": status.Text = "正在恢复连接"; detail.Text = "控制端断开后，本机会继续等待新的连接。"; break;
            case "capture-settings-applied": metrics.Text = "已应用客户端画质设置"; break;
            case "terminal-opened": status.Text = "远程终端已连接"; detail.Text = "以当前用户运行；停止被控会关闭终端会话。"; break;
            case "terminal-detached": metrics.Text = "终端会话已保留，可重新连接"; break;
            case "terminal-closed": metrics.Text = "终端会话已结束"; break;
            case "ready": hostBadge.Text = "●  被控已开启"; ApplyTheme(hostBadge, TextBlock.ForegroundProperty, "AppPositiveBrush"); status.Text = "等待连接"; detail.Text = "已上线，可通过 UU 官方客户端连接。"; break;
            case "viewer-released": status.Text = "等待连接"; detail.Text = "控制端已断开，本机仍允许被控。"; break;
            case "stopping": status.Text = "正在停止"; break;
            case "stopped": hostBadge.Text = "○  被控已关闭"; ApplyTheme(hostBadge, TextBlock.ForegroundProperty, "AppMutedBrush"); if (!restoreFailed) { status.Text = "被控已关闭"; detail.Text = "远程连接已断开，键鼠与终端会话已释放。"; } break;
            case "restore-failed": restoreFailed = true; status.Text = "连接已断开，服务器开关恢复失败"; break;
            default: if (value.StartsWith("file-transfer-progress;", StringComparison.Ordinal)) {
                    var fields = value.Split(';'); transferState.Text = "正在传输 · " + string.Join(" / ", fields.Skip(1).Select(x => x.Split('=').Last())) + " 字节";
                }
                if (value.StartsWith("video-frames-sent=", StringComparison.Ordinal)) { status.Text = "正在共享屏幕"; detail.Text = "可随时关闭被控开关。"; var fields = value.Split(';');
                    metrics.Text = string.Join(" · ", fields.Skip(1).Select(f => f.Replace("screen=", "显示屏 ").Replace("fps=", "FPS "))); } break;
        }
    });
    }
    private void Post(Action action) => Dispatcher.UIThread.Post(() => { if (!disposed) action(); });
    private void Stop()
    {
        try
        {
            File.WriteAllText(settingsFile, JsonSerializer.Serialize(new Settings(identity.Text ?? "", encoder.Text ?? "", allowAssistance, false)));
            hostEnabled = false;
        }
        catch { detail.Text = "停止设置未能保存，应用重启后可能再次开启被控。"; }

        if (session is not { IsCompleted: false }) { SetHostSwitch(false); return; }
        hostSwitch.IsEnabled = false;
        status.Text = "正在停止";
        sessionStop?.Cancel();
    }
    public void Dispose()
    {
        if (disposed) return;
        foreach (var window in remoteWindows.Values.ToArray()) window.Close();
        foreach (var window in toolWindows.Values.ToArray()) window.Close();
        disposed = true; termination?.Dispose(); lifetime.Cancel(); sessionStop?.Cancel();
        // Cleanup does not depend on the UI dispatcher, so the host can safely unload afterwards.
        try { session?.GetAwaiter().GetResult(); } catch { }
        deviceTimer?.Stop();
        previewHttp.Dispose();
        foreach (var bitmap in previewCache.Values) bitmap.Dispose();
        previewCache.Clear();
        foreach (var bitmap in localBitmaps) bitmap.Dispose();
        localBitmaps.Clear();
        sessionStop?.Dispose();
        if (login is not null) login.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _ = initialize.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default);
    }
}
