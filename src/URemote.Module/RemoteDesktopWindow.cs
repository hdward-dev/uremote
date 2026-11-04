using System.Runtime.InteropServices;
using AsterDock.Contracts;
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

public sealed partial class RemoteDesktopWindow : Window
{
    private readonly DesktopControllerSession controller = new();
    private readonly CancellationTokenSource stop = new();
    private readonly HashSet<int> keys = [];
    private readonly HashSet<int> buttons = [];
    private readonly Dictionary<int, ControllerFrame> latest = [];
    private readonly object frameGate = new();
    private readonly DispatcherTimer paintTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private WriteableBitmap? bitmap;
    private ControllerFrame? painted;
    private RemoteScreen[] screens = [];
    private int activeDisplay;
    private int activeTrack;
    private bool updatingDisplays;
    private bool awaitingSwitch;
    private bool closed;
    private int inputSent;
    private bool inputFailed;
    private bool mac;
    private IUserFeedbackService? feedback;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public RemoteDesktopWindow()
    {
        InitializeComponent();
    }

    public RemoteDesktopWindow(UuDevice device, LoginState state, string ffmpeg, AssistanceRequest? assistance = null, IUserFeedbackService? feedback = null, bool forceJoin = false) : this()
    {
        this.feedback = feedback;
        mac = device.Platform == 4;
        controller.TargetPlatform += platform => mac = platform == 4;
        Title = device.Name + " · U远程";
        status.Text = device.Name;
        disconnect.Click += (_, _) => Close();
        quality.SelectionChanged += (_, _) => ApplyQuality();
        ApplyQuality();
        fullScreen.Click += (_, _) => { WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen; video.Focus(); };
        display.SelectionChanged += (_, _) => {
            if (updatingDisplays || display.SelectedIndex < 0 || display.SelectedIndex >= screens.Length) return;
            var selected = screens[display.SelectedIndex];
            var trackChanged = selected.VideoTrack != activeTrack;
            ReleaseInputs();
            activeDisplay = selected.Id; activeTrack = selected.VideoTrack; awaitingSwitch = true;
            controller.SelectDisplay(activeDisplay);
            status.Text = "正在切换显示屏…";
            if (!trackChanged) return;
            painted = null;
            if (HasFrame(activeTrack)) return;
            video.Source = null; connectingOverlay.Text = "正在切换显示屏…"; connectingOverlay.IsLoading = true;
        };
        controller.Status += value => Console.WriteLine("controller-" + value);
        controller.Status += value => Dispatcher.UIThread.Post(() => { if (!closed) ApplyConnectionStatus(value switch {
            "joining" => "正在请求连接…", "signaling" => "正在连接设备…", "negotiating" or "answer-received" => "正在建立画面通道…",
            "ice-checking" => "正在尝试直连或中转…", "ice-failed" or "peer-failed" => FailChannel(),
            "control-ready" or "peer-connected" => Connected(), "peer-disconnected" => "连接中断…", "closed" => "连接已结束", _ => status.Text },
            value is "ice-failed" or "peer-failed" or "closed"); });
        controller.Displays += list => Dispatcher.UIThread.Post(() => ApplyDisplays(list));
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
        Opened += (_, _) => { paintTimer.Start(); Completion = RunAsync(state, device.Id, ffmpeg, assistance, forceJoin); };
        Closed += (_, _) => { closed = true; ReleaseInputs(); stop.Cancel(); paintTimer.Stop(); video.Source = null; bitmap?.Dispose(); lock (frameGate) latest.Clear(); };
    }
    // Combo order is HostVideoSettings 1–5: fluent, HD, ultra, original, automatic.
    private void ApplyQuality()
    {
        var level = quality.SelectedIndex + 1;
        if (level is >= 1 and <= 5) controller.SetQuality(level);
    }
    private string Connected()
    {
        ApplyQuality();
        return "已连接，等待画面…";
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
    private void ApplyDisplays(IReadOnlyList<RemoteScreen> list)
    {
        if (closed || list.Count is < 1 or > 5) return;
        updatingDisplays = true;
        try
        {
            screens = list.ToArray();
            display.ItemsSource = screens.Select(screen => "显示屏 " + (screen.Id + 1)).ToArray();
            display.IsEnabled = screens.Length > 1;
            var index = Array.FindIndex(screens, screen => screen.Id == activeDisplay);
            if (index < 0) index = 0;
            var selected = screens[index];
            var changed = selected.Id != activeDisplay || selected.VideoTrack != activeTrack;
            activeDisplay = selected.Id; activeTrack = selected.VideoTrack;
            display.SelectedIndex = index;
            if (!changed) return;
            painted = null; awaitingSwitch = true; controller.SelectDisplay(activeDisplay);
            if (HasFrame(activeTrack)) return;
            video.Source = null; status.Text = "正在切换显示屏…";
            connectingOverlay.Text = "正在切换显示屏…"; connectingOverlay.IsLoading = true;
        }
        finally { updatingDisplays = false; }
    }
    private bool HasFrame(int track) { lock (frameGate) return latest.ContainsKey(track); }
    private void Paint()
    {
        ControllerFrame? frame; lock (frameGate) latest.TryGetValue(activeTrack, out frame);
        if (closed || frame is null || ReferenceEquals(painted, frame)) return;
        if (bitmap is null || bitmap.PixelSize != new PixelSize(frame.Width, frame.Height)) {
            video.Source = null; bitmap?.Dispose(); bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque); video.Source = bitmap;
        }
        using (var fb = bitmap.Lock())
        {
            if (fb.RowBytes == frame.Width * 4) Marshal.Copy(frame.Pixels, 0, fb.Address, frame.Pixels.Length);
            else for (int y = 0; y < frame.Height; y++) Marshal.Copy(frame.Pixels, y * frame.Width * 4, fb.Address + y * fb.RowBytes, frame.Width * 4);
        }
        if (awaitingSwitch || painted is null || painted.Width != frame.Width || painted.Height != frame.Height)
            status.Text = $"已连接 · {frame.Width} × {frame.Height}";
        awaitingSwitch = false; painted = frame; video.InvalidateVisual(); connectingOverlay.IsLoading = false;
    }
    private string FailChannel()
    {
        const string message = "网络通道建立失败，请检查双方网络后重试。";
        feedback.Notify(FeedbackKind.Error, message, "远程桌面");
        return message;
    }
    private void ApplyConnectionStatus(string label, bool terminal)
    {
        if (terminal || painted is not null) { connectingOverlay.IsLoading = false; status.Text = label; return; }
        connectingOverlay.IsLoading = true;
        connectingOverlay.Text = label;
        status.Text = label;
    }
    private async Task RunAsync(LoginState state, string target, string ffmpeg, AssistanceRequest? assistance = null, bool forceJoin = false)
    {
        try { await controller.RunAsync(state, target, ffmpeg, stop.Token, assistance: assistance, forceJoin: forceJoin); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) {
            Console.WriteLine("controller-failed;type=" + e.GetType().Name);
            if (!closed)
            {
                connectingOverlay.IsLoading = false;
                status.Text = "连接失败，请确认设备在线、允许被控；协助连接还需检查协助码和验证码，或对方是否要求确认。关闭窗口后可重试。";
                feedback.Report("连接失败", e);
            }
        }
        finally { if (!closed) { paintTimer.Stop(); ReleaseInputs(); } }
    }
}
