using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using URemote.Core;

namespace URemote.Module;

public sealed class LocalDesktopWindow : Window
{
    private readonly CancellationTokenSource stop = new();
    private readonly ILocalDesktopSession session;
    private readonly LocalDesktopProtocol protocol;
    private readonly Image desktop = new() { Stretch = Stretch.Uniform, Focusable = true };
    private readonly TextBlock status = new() { Text = "正在连接…", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer paintTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Dictionary<Key, uint> keys = [];
    private readonly object frameGate = new();
    private LocalDesktopFrame? latest, painted;
    private WriteableBitmap? bitmap;
    private int buttons, mouseX, mouseY;
    private volatile bool closed;
    public Task Completion { get; private set; } = Task.CompletedTask;

    public LocalDesktopWindow(LocalDesktopOptions options)
    {
        options.Validate(); protocol = options.Protocol;
        session = protocol == LocalDesktopProtocol.Vnc ? new VncDesktopSession() : new RdpDesktopSession(VerifyCertificateAsync);
        Title = $"{options.Host}:{options.Port} · {protocol.ToString().ToUpperInvariant()} · U远程";
        Width = 1280; Height = 800; MinWidth = 640; MinHeight = 440; Background = Brushes.Black;
        var disconnect = new Button { Content = "断开连接" }; disconnect.Click += (_, _) => Close();
        var full = new Button { Content = "全屏" };
        full.Click += (_, _) => { WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen; desktop.Focus(); };
        var bar = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 12, Margin = new Thickness(16, 10) };
        bar.Children.Add(status); Grid.SetColumn(full, 1); bar.Children.Add(full); Grid.SetColumn(disconnect, 2); bar.Children.Add(disconnect);
        var body = new Grid { RowDefinitions = new("Auto,*,Auto") }; body.Children.Add(bar);
        Grid.SetRow(desktop, 1); body.Children.Add(desktop);
        var hint = new TextBlock { Text = "点击画面控制 · Ctrl + Alt + Esc 释放键鼠 · 关闭窗口断开连接", Foreground = Brushes.LightGray, FontSize = 12, Margin = new Thickness(16, 8) };
        Grid.SetRow(hint, 2); body.Children.Add(hint); Content = body;
        session.Frame += frame => { lock (frameGate) { if (!closed) latest = frame; } };
        session.Status += value => Dispatcher.UIThread.Post(() => { if (!closed) status.Text = value; });
        paintTimer.Tick += (_, _) => Paint();
        desktop.PointerPressed += (_, e) =>
        {
            if (!Move(e.GetPosition(desktop))) return;
            desktop.Focus(); e.Pointer.Capture(desktop);
            buttons |= Button(e.GetCurrentPoint(desktop).Properties.PointerUpdateKind);
            Send(() => session.Pointer(mouseX, mouseY, buttons)); e.Handled = true;
        };
        desktop.PointerReleased += (_, e) =>
        {
            Move(e.GetPosition(desktop), true);
            buttons &= ~Button(e.GetCurrentPoint(desktop).Properties.PointerUpdateKind);
            Send(() => session.Pointer(mouseX, mouseY, buttons));
            if (buttons == 0) e.Pointer.Capture(null); e.Handled = true;
        };
        desktop.PointerMoved += (_, e) => { if (Move(e.GetPosition(desktop), buttons != 0)) Send(() => session.Pointer(mouseX, mouseY, buttons)); };
        desktop.PointerWheelChanged += (_, e) =>
        {
            if (Move(e.GetPosition(desktop))) Send(() => session.Pointer(mouseX, mouseY, buttons, Math.Sign(e.Delta.Y)));
            e.Handled = true;
        };
        desktop.PointerCaptureLost += (_, _) => ReleaseInputs();
        desktop.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            { ReleaseInputs(); disconnect.Focus(); e.Handled = true; return; }
            var physical = e.PhysicalKey == PhysicalKey.None ? e.Key : e.PhysicalKey.ToQwertyKey();
            var symbol = keys.TryGetValue(physical, out var existing) ? existing : LocalDesktopKeys.Map(physical, e.KeyModifiers, protocol);
            if (symbol is { } code) { keys[physical] = code; Send(() => session.Key(code, true)); e.Handled = true; }
        };
        desktop.KeyUp += (_, e) =>
        {
            var physical = e.PhysicalKey == PhysicalKey.None ? e.Key : e.PhysicalKey.ToQwertyKey();
            if (keys.Remove(physical, out var code)) { Send(() => session.Key(code, false)); e.Handled = true; }
        };
        desktop.LostFocus += (_, _) => ReleaseInputs(); Deactivated += (_, _) => ReleaseInputs();
        EventHandler? opened = null;
        opened = (_, _) => { Opened -= opened; paintTimer.Start(); Completion = ConnectAsync(options); };
        Opened += opened;
        Closed += (_, _) =>
        {
            ReleaseInputs(); closed = true; stop.Cancel(); paintTimer.Stop(); desktop.Source = null; bitmap?.Dispose(); bitmap = null;
            lock (frameGate) latest = null; painted = null;
            _ = Completion.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
        };
    }

    private async Task ConnectAsync(LocalDesktopOptions options)
    {
        try { await session.RunAsync(options, stop.Token); if (!closed) status.Text = "连接已结束"; }
        catch (Exception e) when (stop.IsCancellationRequested && e is OperationCanceledException or ObjectDisposedException or IOException or System.Net.Sockets.SocketException) { }
        catch (Exception e)
        {
            if (!closed) status.Text = e is ArgumentException or InvalidOperationException or NotSupportedException or InvalidDataException ? e.Message : "连接失败或已中断，请检查服务端及网络。";
        }
        finally { if (!closed) { ReleaseInputs(); paintTimer.Stop(); } }
    }

    private void Send(Action action)
    {
        if (closed || stop.IsCancellationRequested) return;
        try { action(); }
        catch (IOException) { status.Text = "输入发送失败，连接已断开。"; stop.Cancel(); }
    }
    private void ReleaseInputs()
    {
        foreach (var code in keys.Values) Send(() => session.Key(code, false)); keys.Clear(); buttons = 0;
        Send(() => session.Pointer(mouseX, mouseY, 0));
    }
    private static int Button(PointerUpdateKind kind) => kind switch {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => 1,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => 2,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => 4, _ => 0
    };
    private bool Move(Point point, bool clamp = false)
    {
        if (painted is null || desktop.Bounds.Width <= 0 || desktop.Bounds.Height <= 0) return false;
        var scale = Math.Min(desktop.Bounds.Width / painted.Width, desktop.Bounds.Height / painted.Height);
        var x = (point.X - (desktop.Bounds.Width - painted.Width * scale) / 2) / scale;
        var y = (point.Y - (desktop.Bounds.Height - painted.Height * scale) / 2) / scale;
        if (!clamp && (x < 0 || y < 0 || x >= painted.Width || y >= painted.Height)) return false;
        mouseX = Math.Clamp((int)x, 0, painted.Width - 1); mouseY = Math.Clamp((int)y, 0, painted.Height - 1); return true;
    }
    private void Paint()
    {
        LocalDesktopFrame? frame; lock (frameGate) { frame = latest; latest = null; }
        if (frame is null || closed) return;
        if (bitmap is null || bitmap.PixelSize.Width != frame.Width || bitmap.PixelSize.Height != frame.Height)
        {
            desktop.Source = null; bitmap?.Dispose();
            bitmap = new WriteableBitmap(new(frame.Width, frame.Height), new(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            desktop.Source = bitmap;
        }
        using (var buffer = bitmap.Lock())
            for (var row = 0; row < frame.Height; row++) Marshal.Copy(frame.Pixels, row * frame.Width * 4, IntPtr.Add(buffer.Address, row * buffer.RowBytes), frame.Width * 4);
        painted = frame; desktop.InvalidateVisual();
    }

    private async Task<bool> VerifyCertificateAsync(DesktopCertificate certificate, CancellationToken token)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (closed || token.IsCancellationRequested) { result.TrySetResult(false); return; }
            var dialog = new Window { Title = "确认 RDP 服务器证书", Width = 560, Height = 430, CanResize = true };
            var accept = new Button { Content = "仅本次信任并连接" };
            var reject = new Button { Content = "取消连接" };
            accept.Click += (_, _) => { result.TrySetResult(true); dialog.Close(); };
            reject.Click += (_, _) => dialog.Close();
            dialog.Closed += (_, _) => result.TrySetResult(false);
            var text = new TextBlock { Text = $"{(certificate.Changed ? "服务器证书已变化" : "服务器证书未受信任")}\n\n服务器：{certificate.Host}\n主体：{certificate.Subject}\n颁发者：{certificate.Issuer}\n指纹：{certificate.Fingerprint}", TextWrapping = TextWrapping.Wrap };
            dialog.Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(24), Spacing = 20,
                Children = { text, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { reject, accept } } } } };
            _ = dialog.ShowDialog(this);
            _ = result.Task.ContinueWith(_ => Dispatcher.UIThread.Post(dialog.Close), TaskScheduler.Default);
        });
        using var cancel = token.Register(() => result.TrySetResult(false));
        return await result.Task;
    }
}
