using Microsoft.Win32.SafeHandles;
using URemote.Core;

namespace URemote.Linux;

// C# owns Portal authorization, Unix descriptors, native capture and input for the complete host lifetime.
internal sealed class PortalDesktopSession : IAsyncDisposable
{
    private readonly PortalBus bus;
    private readonly SemaphoreSlim inputGate = new(1);
    private readonly CancellationTokenSource closed = new();
    private readonly Dictionary<uint, (PortalVideoStream Video, int Width, int Height)> streams = [];
    private readonly HashSet<(string Method, int Code)> held = [];
    private readonly Dictionary<uint, string> touchMappings = [];
    private SharedDesktopTouch? touch;
    private PortalTouchSession? inputSession;
    private SafeFileHandle? remote;
    private string? session;
    private bool enableInput, disposed;
    public IReadOnlyList<WaylandGlobal> Outputs { get; private set; } = [];
    private PortalDesktopSession(PortalBus bus) => this.bus = bus;

    public static async Task ProbeAsync(CancellationToken ct)
    {
        try
        {
            NativeGst.Probe();
            await using var bus = await PortalBus.ConnectAsync(ct);
            if ((await bus.PropertyAsync(PortalBus.ScreenCast, "AvailableSourceTypes", ct) & 1) == 0)
                throw new NotSupportedException("桌面 Portal 不支持显示器共享。");
        }
        catch (DllNotFoundException e) { throw MissingLibraries(e); }
        catch (EntryPointNotFoundException e) { throw MissingLibraries(e); }
    }
    private static NotSupportedException MissingLibraries(Exception e) => new(
        "Portal 后端需要 GLib/GIO、GStreamer、gst-app、gst-video 和 PipeWire 原生插件，以及对应桌面的 xdg-desktop-portal 服务；不需要 Python。", e);

    public static async Task<PortalDesktopSession> OpenAsync(bool enableInput, CancellationToken ct)
    {
        await ProbeAsync(ct);
        var owner = new PortalDesktopSession(await PortalBus.ConnectAsync(ct));
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(130));
            await owner.StartAsync(enableInput, deadline.Token);
            return owner;
        }
        catch { await owner.DisposeAsync(); throw; }
    }
    private async Task StartAsync(bool input, CancellationToken ct)
    {
        var iface = input ? PortalBus.RemoteDesktop : PortalBus.ScreenCast;
        var handleToken = "u" + Guid.NewGuid().ToString("N");
        session = bus.SessionPath(handleToken); // Also close it if cancellation races CreateSession response.
        using var sessionToken = PortalValue.String(handleToken);
        using (var created = await bus.RequestAsync(iface, "CreateSession", null, false, [("session_handle_token", sessionToken)], ct))
        {
            using var path = created.Get("session_handle") ?? throw new FormatException("缺少桌面授权会话。");
            session = path.Text();
            if (!session.StartsWith(PortalBus.Desktop + "/session/", StringComparison.Ordinal)) throw new FormatException("无效的桌面授权会话。");
        }
        await bus.SubscribeAsync(PortalBus.InterfacePrefix + ".Session", "Closed", session, _ => { closed.Cancel(); touch?.Reset(); });
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, closed.Token);
        var token = lifetime.Token;
        using var started = await PortalAuthorization.StartAsync(bus, session!, input, token, new PortalRestoreStore(input));
        uint grantedTypes = 0;
        if (input)
        {
            using var devices = started.Get("devices");
            if (devices is null || (devices.UInt() & 3) != 3) throw new InvalidOperationException("请允许键盘和鼠标控制后重试。");
            grantedTypes = devices.UInt();
        }
        enableInput = input;
        using var outputs = started.Get("streams") ?? throw new FormatException("未选择共享显示器。");
        outputs.Require("a(ua{sv})");
        if (outputs.Count is < 1 or > 5) throw new FormatException("请选择 1 至 5 个显示器。");
        remote = await bus.OpenPipeWireAsync(session, token);
        for (var i = 0; i < outputs.Count; i++)
        {
            using var stream = outputs.Child(i); using var id = stream.Child(0); using var properties = stream.Child(1);
            var node = id.UInt();
            if (node == 0 || streams.ContainsKey(node)) throw new FormatException("无效的共享显示器编号。");
            using var size = properties.Get("logical_size") ?? properties.Get("size");
            int width = 0, height = 0;
            if (size is not null)
            {
                size.Require("(ii)"); using var w = size.Child(0); using var h = size.Child(1); width = w.Int(); height = h.Int();
                if (width <= 0 || height <= 0) throw new FormatException("无效的桌面逻辑尺寸。");
            }
            if (input && (width == 0 || height == 0)) throw new NotSupportedException("桌面未提供指针定位所需的逻辑尺寸。");
            using var mapping = properties.Get("mapping_id");
            if (mapping is not null && !string.IsNullOrWhiteSpace(mapping.Text())) touchMappings.Add(node, mapping.Text());
            streams.Add(node, (new PortalVideoStream(node, checked((int)remote.DangerousGetHandle())), width, height));
        }
        CheckAlive();
        Outputs = streams.Keys.Select(id => new WaylandGlobal(id, "wl_output", 1)).ToArray();
        if (input)
        {
            if (touchMappings.Count != streams.Count || touchMappings.Values.Distinct().Count() != streams.Count)
                throw new NotSupportedException("桌面未提供 EIS 输入定位所需的显示器映射。");
            inputSession = await PortalTouchSession.ConnectAsync(bus, session!, touchMappings, (grantedTypes & 4) != 0, token);
            touch = new SharedDesktopTouch(inputSession, () => !disposed && !closed.IsCancellationRequested && !bus.IsClosed);
            Console.WriteLine("portal-input=eis;authorization=combined;text=" + inputSession.SupportsText);
        }
        CheckAlive();
    }
    private void CheckAlive()
    {
        if (disposed || closed.IsCancellationRequested || bus.IsClosed || inputSession is { Connected: false })
            throw new DesktopSessionClosedException("桌面共享授权已结束，请重新开启被控并授权。");
    }
    public Task CheckAliveAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); CheckAlive(); return Task.CompletedTask; }
    public async Task<CapturedScreen> CaptureAsync(uint output, CancellationToken ct)
    {
        CheckAlive();
        if (!streams.TryGetValue(output, out var stream)) throw new ArgumentException("所选显示器不可用。");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, closed.Token);
        try { return await stream.Video.CaptureAsync(stop.Token); }
        catch (OperationCanceledException) when (closed.IsCancellationRequested && !ct.IsCancellationRequested)
        { throw new DesktopSessionClosedException("桌面共享授权已结束。"); }
    }
    private async Task KeyAsync(string method, int code, bool down, CancellationToken ct)
    {
        await inputGate.WaitAsync(ct);
        try
        {
            if (down) held.Add((method, code)); // If cancellation races the reply, cleanup must still release it.
            CheckAlive();
            if (!enableInput || inputSession is null) throw new InvalidOperationException("键鼠控制未授权。");
            if (!inputSession.Key(checked((uint)code), down, method == "button")) throw new IOException("EIS 输入设备暂不可用。");
            if (!down) held.Remove((method, code));
        }
        finally { inputGate.Release(); }
    }
    private async Task ReleaseKeyAsync(string method, int code)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await KeyAsync(method, code, false, timeout.Token); }
        catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException or DesktopSessionClosedException) { }
    }
    public Task TypeTextAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); CheckAlive(); HostControlInput.ValidateText(text);
        if (!enableInput || inputSession is null) throw new InvalidOperationException("文字输入未授权。");
        if (!inputSession.Text(text)) throw new NotSupportedException("当前桌面未提供 EIS 文字输入，请使用被控端输入法或复制粘贴。");
        return Task.CompletedTask;
    }
    public IDesktopPointer CreatePointer(uint output)
    {
        if (!streams.ContainsKey(output)) throw new ArgumentException("所选显示器不可用。");
        return new Pointer(this, output);
    }
    public Task<IDesktopTouch?> CreateTouchAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); CheckAlive();
        return Task.FromResult(touch?.Acquire());
    }
    public IDesktopKeyboard CreateKeyboard() => new Keyboard(this);
    private sealed class Pointer(PortalDesktopSession owner, uint output) : IDesktopPointer
    {
        private readonly HashSet<int> held = [];
        public async Task ApplyAsync(HostMouseMessage message, PointerCoordinates coordinates, uint width, uint height, CancellationToken ct = default)
        {
            if (message.ScreenId is not null) throw new ArgumentException("请先解析显示器编号。");
            if (message.Action == MouseAction.MoveAbsolute)
            {
                WaylandVirtualPointer.AbsolutePayload(0, message.X, message.Y, coordinates, width, height);
                var x = coordinates == PointerCoordinates.MacNormalized ? message.X : message.X / width;
                var y = coordinates == PointerCoordinates.MacNormalized ? message.Y : message.Y / height;
                ct.ThrowIfCancellationRequested(); owner.CheckAlive();
                if (owner.inputSession?.Point(output, x, y) != true) throw new IOException("EIS 指针暂不可用。");
            }
            else if (message.Action == MouseAction.Scroll)
            {
                WaylandVirtualPointer.AxisPayload(0, 0, message.Y); WaylandVirtualPointer.AxisPayload(0, 1, message.X);
                ct.ThrowIfCancellationRequested(); owner.CheckAlive();
                if (owner.inputSession?.Scroll(message.X, -message.Y) != true) throw new IOException("EIS 滚轮暂不可用。");
            }
            else if (message.Action is MouseAction.Press or MouseAction.Release or MouseAction.Click)
            {
                var code = checked((int)WaylandVirtualPointer.LinuxButton(message.Button));
                if (message.Action == MouseAction.Click && held.Contains(code)) return;
                if (message.Action != MouseAction.Release) { held.Add(code); await owner.KeyAsync("button", code, true, ct); }
                if (message.Action != MouseAction.Press) { await owner.KeyAsync("button", code, false, ct); held.Remove(code); }
            }
            else throw new ArgumentException("不支持的鼠标操作。");
        }
        public async ValueTask DisposeAsync() { foreach (var key in held) await owner.ReleaseKeyAsync("button", key); held.Clear(); }
    }
    private sealed class Keyboard(PortalDesktopSession owner) : IDesktopKeyboard
    {
        private readonly HashSet<int> held = [];
        public async Task ApplyAsync(HostKeyMessage message, CancellationToken ct = default)
        {
            if (WaylandVirtualKeyboard.LinuxKey(message.MacKey) is not { } value) return;
            var code = checked((int)value);
            if (message.Action == KeyAction.Click && held.Contains(code)) return;
            if (message.Action != KeyAction.Release && !held.Contains(code))
            { held.Add(code); await owner.KeyAsync("key", code, true, ct); }
            if (message.Action != KeyAction.Press && held.Contains(code))
            { await owner.KeyAsync("key", code, false, ct); held.Remove(code); }
        }
        public async ValueTask DisposeAsync() { foreach (var key in held) await owner.ReleaseKeyAsync("key", key); held.Clear(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        foreach (var key in held.ToArray()) await ReleaseKeyAsync(key.Method, key.Code);
        disposed = true;
        closed.Cancel();
        try
        {
            if (touch is not null) await touch.DisposeAsync();
            if (session is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var args = PortalValue.Tuple();
                try { using var result = await bus.CallAsync(PortalBus.InterfacePrefix + ".Session", "Close", args, timeout.Token, session); }
                catch (Exception e) when (e is IOException or OperationCanceledException) { }
            }
        }
        finally
        {
            foreach (var stream in streams.Values) await stream.Video.DisposeAsync();
            streams.Clear(); remote?.Dispose();
            await bus.DisposeAsync(); closed.Dispose();
        }
    }
}
