using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using URemote.Core;
using URemote.Host;
using URemote.Media;
namespace URemote.Module;

public sealed class RemoteDesktopWindow : Window
{
    private readonly DesktopControllerSession controller = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Image video = new() { Stretch = Stretch.Uniform, Focusable = true };
    private readonly TextBlock status = new() { Text = "正在连接…", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox display = new() { MinWidth = 130, PlaceholderText = "显示屏", IsEnabled = false };
    private readonly HashSet<int> keys = [];
    private readonly HashSet<int> buttons = [];
    private readonly Dictionary<int, ControllerFrame> latest = [];
    private readonly object frameGate = new();
    private readonly DispatcherTimer paintTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private WriteableBitmap? bitmap;
    private ControllerFrame? painted;
    private int[] displayIds = [];
    private int activeDisplay;
    private bool closed;
    private int inputSent;
    private bool inputFailed;
    private bool mac;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public RemoteDesktopWindow(UuDevice device, LoginState state, string ffmpeg, AssistanceRequest? assistance = null)
    {
        mac = device.Platform == 4;
        controller.TargetPlatform += platform => mac = platform == 4;
        Title = device.Name + " · U远程"; Width = 1280; Height = 800; MinWidth = 640; MinHeight = 440;
        Background = Brushes.Black;
        var disconnect = new Button { Content = "断开连接" }; disconnect.Click += (_, _) => Close();
        var full = new Button { Content = "全屏" }; full.Click += (_, _) => { WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen; video.Focus(); };
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 12, Margin = new Thickness(16, 10) };
        bar.Children.Add(status); Grid.SetColumn(display, 1); bar.Children.Add(display); Grid.SetColumn(full, 2); bar.Children.Add(full); Grid.SetColumn(disconnect, 3); bar.Children.Add(disconnect);
        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") }; body.Children.Add(bar);
        Grid.SetRow(video, 1); body.Children.Add(video);
        var hint = new TextBlock { Text = "点击画面控制 · Ctrl + Alt + Esc 释放键鼠 · 关闭窗口断开连接", Foreground = Brushes.LightGray, Margin = new Thickness(16, 8), FontSize = 12 };
        Grid.SetRow(hint, 2); body.Children.Add(hint); Content = body;
        display.SelectionChanged += (_, _) => {
            ReleaseInputs(); if (display.SelectedIndex >= 0 && display.SelectedIndex < displayIds.Length) {
                activeDisplay = displayIds[display.SelectedIndex]; controller.SelectDisplay(activeDisplay);
                video.Source = null; painted = null; status.Text = "正在切换显示屏…";
            }
        };
        controller.Status += value => Console.WriteLine("controller-" + value);
        controller.Status += value => Dispatcher.UIThread.Post(() => { if (!closed) status.Text = value switch {
            "joining" => "正在请求连接…", "signaling" => "正在连接设备…", "negotiating" or "answer-received" => "正在建立画面通道…",
            "ice-checking" => "正在尝试直连或中转…", "ice-failed" or "peer-failed" => "网络通道建立失败，请检查双方网络后重试。",
            "control-ready" or "peer-connected" => "已连接，等待画面…", "peer-disconnected" => "连接中断…", "closed" => "连接已结束", _ => status.Text }; });
        controller.Displays += ids => Dispatcher.UIThread.Post(() => {
            if (closed) return; displayIds = ids; display.ItemsSource = ids.Select(x => "显示屏 " + (x + 1)).ToArray();
            display.IsEnabled = ids.Length > 1; display.SelectedIndex = Math.Max(0, Array.IndexOf(ids, activeDisplay));
        });
        controller.Frame += (index, frame) => { lock (frameGate) latest[index] = frame; };
        paintTimer.Tick += (_, _) => Paint();
        video.PointerPressed += (_, e) => {
            if (painted is null) return; video.Focus(); Move(e.GetPosition(video));
            var button = e.GetCurrentPoint(video).Properties.PointerUpdateKind switch { PointerUpdateKind.LeftButtonPressed => 1, PointerUpdateKind.RightButtonPressed => 2, PointerUpdateKind.MiddleButtonPressed => 4, _ => 0 };
            if (button != 0 && Send(new { action = "mouse_press", button })) { buttons.Add(button); e.Pointer.Capture(video); } e.Handled = true;
        };
        video.PointerReleased += (_, e) => {
            var button = e.GetCurrentPoint(video).Properties.PointerUpdateKind switch { PointerUpdateKind.LeftButtonReleased => 1, PointerUpdateKind.RightButtonReleased => 2, PointerUpdateKind.MiddleButtonReleased => 4, _ => 0 };
            if (buttons.Remove(button)) Send(new { action = "mouse_release", button }); if (buttons.Count == 0) e.Pointer.Capture(null); e.Handled = true;
        };
        video.PointerMoved += (_, e) => { if (painted is not null) Move(e.GetPosition(video)); };
        video.PointerWheelChanged += (_, e) => { Send(new { action = "mouse_scroll", delta_x = (int)(e.Delta.X * 120), delta_y = (int)(e.Delta.Y * 120) }); e.Handled = true; };
        video.PointerCaptureLost += (_, _) => ReleaseInputs();
        video.KeyDown += (_, e) => {
            if (e.Key == Key.Escape && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { ReleaseInputs(); disconnect.Focus(); e.Handled = true; return; }
            if (ControllerKeys.Map(e.PhysicalKey, e.Key, mac) is { } key) { if (SendKey(key, true)) keys.Add(key); e.Handled = true; }
        };
        video.KeyUp += (_, e) => { if (ControllerKeys.Map(e.PhysicalKey, e.Key, mac) is { } key) { if (keys.Remove(key)) SendKey(key, false); e.Handled = true; } };
        video.LostFocus += (_, _) => ReleaseInputs(); Deactivated += (_, _) => ReleaseInputs();
        Opened += (_, _) => { paintTimer.Start(); Completion = RunAsync(state, device.Id, ffmpeg, assistance); };
        Closed += (_, _) => { closed = true; ReleaseInputs(); stop.Cancel(); paintTimer.Stop(); video.Source = null; bitmap?.Dispose(); lock (frameGate) latest.Clear(); };
    }
    private bool Send(object value)
    {
        var sent = controller.SendInput(JsonSerializer.Serialize(value));
        if (sent && (++inputSent == 1 || inputSent % 100 == 0)) Console.WriteLine("controller-input-sent=" + inputSent);
        if (!sent && !inputFailed) Console.WriteLine("controller-input-send-failed");
        inputFailed = !sent;
        return sent;
    }
    private bool SendKey(int key, bool down) => mac ? Send(new { action = down ? "kbd_press" : "kbd_release", key })
        : Send(new { action = down ? "kbd_press" : "kbd_release", key, interrept = true });
    private void ReleaseInputs()
    {
        foreach (var key in keys) SendKey(key, false); keys.Clear();
        foreach (var button in buttons) Send(new { action = "mouse_release", button }); buttons.Clear();
    }
    private void Move(Point p)
    {
        if (painted is not { } frame) return;
        var scale = Math.Min(video.Bounds.Width / frame.Width, video.Bounds.Height / frame.Height);
        if (scale <= 0) return;
        var x = Math.Clamp((p.X - (video.Bounds.Width - frame.Width * scale) / 2) / scale, 0, frame.Width - 1);
        var y = Math.Clamp((p.Y - (video.Bounds.Height - frame.Height * scale) / 2) / scale, 0, frame.Height - 1);
        Send(new { action = "mouse_move_absolute", abs_x = x / frame.Width, abs_y = y / frame.Height });
    }
    private void Paint()
    {
        ControllerFrame? frame; lock (frameGate) latest.TryGetValue(activeDisplay, out frame);
        if (closed || frame is null || ReferenceEquals(painted, frame)) return;
        if (bitmap is null || bitmap.PixelSize != new PixelSize(frame.Width, frame.Height)) {
            video.Source = null; bitmap?.Dispose(); bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque); video.Source = bitmap;
        }
        using (var fb = bitmap.Lock()) for (int y = 0; y < frame.Height; y++) Marshal.Copy(frame.Pixels, y * frame.Width * 4, fb.Address + y * fb.RowBytes, frame.Width * 4);
        painted = frame; video.InvalidateVisual(); status.Text = $"已连接 · {frame.Width} × {frame.Height}";
    }
    private async Task RunAsync(LoginState state, string target, string ffmpeg, AssistanceRequest? assistance = null)
    {
        try { await controller.RunAsync(state, target, ffmpeg, stop.Token, assistance: assistance); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) {
            Console.WriteLine("controller-failed;type=" + e.GetType().Name);
            if (!closed) status.Text = "连接失败，请确认设备在线、允许被控；协助连接还需检查协助码和验证码，或对方是否要求确认。关闭窗口后可重试。";
        }
        finally { if (!closed) { paintTimer.Stop(); ReleaseInputs(); } }
    }
}
