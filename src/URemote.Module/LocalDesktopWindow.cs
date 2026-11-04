using System.Runtime.InteropServices;
using AsterDock.Contracts;
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

public sealed partial class LocalDesktopWindow : Window
{
    private readonly CancellationTokenSource stop = new();
    private readonly ILocalDesktopSession session;
    private readonly LocalDesktopProtocol protocol;
    private readonly DispatcherTimer paintTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Dictionary<Key, uint> keys = [];
    private readonly object frameGate = new();
    private LocalDesktopFrame? latest, painted;
    private WriteableBitmap? bitmap;
    private int buttons, mouseX, mouseY;
    private volatile bool closed;
    private IUserFeedbackService? feedback;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public event Action? Connected;
    private bool connected;
    internal bool IsConnected => connected && !closed && !Completion.IsCompleted;

    public LocalDesktopWindow()
    {
        InitializeComponent();
        protocol = LocalDesktopProtocol.Vnc;
        session = new VncDesktopSession();
        feedback = null;
    }

    public LocalDesktopWindow(LocalDesktopOptions options, IUserFeedbackService? feedback = null) : this()
    {
        options.Validate(); protocol = options.Protocol; this.feedback = feedback;
        session = protocol == LocalDesktopProtocol.Vnc ? new VncDesktopSession() : new RdpDesktopSession(VerifyCertificateAsync);
        Title = $"{options.Host}:{options.Port} · {protocol.ToString().ToUpperInvariant()} · U远程";
        status.Text = $"{options.Host}:{options.Port}";
        disconnect.Click += (_, _) => Close();
        fullScreen.Click += (_, _) => { WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen; desktop.Focus(); };
        session.Frame += frame => { lock (frameGate) { if (!closed) latest = frame; } };
        session.Status += value => Dispatcher.UIThread.Post(() =>
        {
            if (closed) return;
            if (!connected && value.StartsWith("已连接", StringComparison.Ordinal))
            {
                connected = true; connectingOverlay.IsLoading = false; status.Text = value; Connected?.Invoke(); return;
            }
            if (!connected && painted is null) { connectingOverlay.IsLoading = true; connectingOverlay.Text = value; }
            else connectingOverlay.IsLoading = false;
            status.Text = value;
        });
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
        try { await session.RunAsync(options, stop.Token); if (!closed) { connectingOverlay.IsLoading = false; status.Text = "连接已结束"; } }
        catch (Exception e) when (stop.IsCancellationRequested && e is OperationCanceledException or ObjectDisposedException or IOException or System.Net.Sockets.SocketException) { }
        catch (Exception e)
        {
            if (!closed)
            {
                connectingOverlay.IsLoading = false;
                status.Text = e is ArgumentException or InvalidOperationException or NotSupportedException or InvalidDataException ? e.Message : "连接失败或已中断，请检查服务端及网络。";
                feedback.Report("连接失败", e);
            }
        }
        finally { if (!closed) { ReleaseInputs(); paintTimer.Stop(); } }
    }

    private void Send(Action action)
    {
        if (closed || stop.IsCancellationRequested) return;
        try { action(); }
        catch (IOException) { status.Text = "输入发送失败，连接已断开。"; feedback.Notify(FeedbackKind.Error, status.Text, "远程桌面"); stop.Cancel(); }
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
        painted = frame; desktop.InvalidateVisual(); connectingOverlay.IsLoading = false;
    }

    private async Task<bool> VerifyCertificateAsync(DesktopCertificate certificate, CancellationToken token)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (closed || token.IsCancellationRequested) { result.TrySetResult(false); return; }
            var dialog = new RdpCertificateWindow(certificate, result);
            _ = dialog.ShowDialog(this);
            _ = result.Task.ContinueWith(_ => Dispatcher.UIThread.Post(dialog.Close), TaskScheduler.Default);
        });
        using var cancel = token.Register(() => result.TrySetResult(false));
        return await result.Task;
    }
}
