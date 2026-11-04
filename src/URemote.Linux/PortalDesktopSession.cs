using URemote.Core;

namespace URemote.Linux;

// C# owns Portal authorization, Unix descriptors, native capture and input for the complete host lifetime.
internal sealed class PortalDesktopSession : IAsyncDisposable
{
    private readonly PortalBus bus;
    private readonly SemaphoreSlim inputGate = new(1);
    private readonly CancellationTokenSource closed = new();
    private readonly object streamLock = new();
    // Video stays null until a viewer arms capture. Geometry and refresh stay for display-list and pointers.
    private readonly Dictionary<uint, (IPortalPipe? Video, int Width, int Height)> streams = [];
    private int pipesArmed;
    private readonly List<PortalStreamBox> layout = [];
    private readonly HashSet<(string Method, int Code)> held = [];
    private readonly Dictionary<uint, string> touchMappings = [];
    private SharedDesktopTouch? touch;
    private PortalTouchSession? inputSession;
    private string? session;
    private bool enableInput, disposed;
    internal bool IsDisposed => disposed;
    internal bool TouchAvailable => !disposed && inputSession?.Available == true;
    private readonly List<DesktopOutput> displayDetails = [];
    public IReadOnlyList<DesktopOutput> DisplayDetails => displayDetails.ToArray();
    public IReadOnlyList<WaylandGlobal> Outputs { get; private set; } = [];
    internal IReadOnlyList<PortalStreamBox> Layout => layout;
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
            using var position = properties.Get("position");
            var x = 0; var y = 0;
            if (position is not null)
            {
                position.Require("(ii)"); using var px = position.Child(0); using var py = position.Child(1); x = px.Int(); y = py.Int();
            }
            using var stable = properties.Get("id");
            using var mappingId = properties.Get("mapping_id");
            var name = stable?.Type == "s" ? stable.Text() : mappingId?.Type == "s" ? mappingId.Text() : "";
            var key = name.Length > 0 ? "portal:" + name : $"portal-session:{session}:{node}";
            displayDetails.Add(new(node, key, name.Length > 0 ? name : "显示器 " + (i + 1)));
            using var mapping = properties.Get("mapping_id");
            if (mapping is not null && !string.IsNullOrWhiteSpace(mapping.Text())) touchMappings.Add(node, mapping.Text());
            // Authorization and geometry only. pipewiresrc starts when a viewer arms capture.
            RememberOutput(node, x, y, width, height);
        }
        CheckAlive();
        PublishOutputs();
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
    private void RememberOutput(uint node, int x, int y, int width, int height)
    {
        lock (streamLock)
        {
            layout.Add(new(node, x, y, width, height));
            streams[node] = (null, width, height);
        }
    }

    // Test hook for the startup path: layout is recorded and no PipeWire node is created.
    internal void RememberOutputForTests(uint node, int x, int y, int width, int height) => RememberOutput(node, x, y, width, height);

    internal Func<uint, CancellationToken, Task<IPortalPipe>>? PipeOverrideForTests { get; set; }

    internal async Task OpenPipesAsync(IReadOnlyList<uint> nodes, CancellationToken ct)
    {
        Interlocked.Exchange(ref pipesArmed, 1);
        foreach (var requested in nodes.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref pipesArmed) == 0 || disposed) return;
            var node = DesktopBackend.ResolveOutput(requested);
            IPortalPipe? existing;
            int width, height;
            lock (streamLock)
            {
                var known = streams.TryGetValue(node, out var slot);
                if (!known && FindBox(node).Node != node)
                    continue;
                existing = known ? slot.Video : null;
                width = known ? slot.Width : 0;
                height = known ? slot.Height : 0;
                if (width == 0 || height == 0)
                {
                    var box = FindBox(node);
                    width = box.Width;
                    height = box.Height;
                }
            }
            if (existing is not null) continue;
            var opened = await OpenStreamAsync(node, ct).ConfigureAwait(false);
            var installed = false;
            try
            {
                lock (streamLock)
                {
                    if (!disposed && Volatile.Read(ref pipesArmed) != 0)
                    {
                        streams[node] = (opened, width, height);
                        installed = true;
                    }
                }
            }
            finally { if (!installed) await opened.DisposeAsync().ConfigureAwait(false); }
            if (!installed) return;
            if (opened is PortalVideoStream video)
                video.WatchFirstBuffer(() => DesktopBackend.NoteCaptureBuffer(requested));
        }
    }

    internal async Task<int> ClosePipesAsync()
    {
        if (disposed) return 0;
        Interlocked.Exchange(ref pipesArmed, 0);
        IPortalPipe[] ending;
        lock (streamLock)
        {
            ending = streams.Values.Select(slot => slot.Video).OfType<IPortalPipe>().ToArray();
            foreach (var node in streams.Keys.ToArray())
            {
                var slot = streams[node];
                streams[node] = (null, slot.Width, slot.Height);
            }
        }
        foreach (var stream in ending)
            await stream.DisposeAsync().ConfigureAwait(false);
        return ending.Length;
    }

    internal IReadOnlyDictionary<uint, int> ReadRefreshRates(IReadOnlyList<DesktopOutput> native)
    {
        var result = new Dictionary<uint, int>();
        foreach (var display in displayDetails)
        {
            var matches = native.Where(x => x.Name == display.Name).ToArray();
            var rate = matches.Length == 1 ? matches[0].RefreshMilliHz : 0;
            if (rate <= 0)
            {
                lock (streamLock)
                    if (streams.TryGetValue(display.Id, out var slot) && slot.Video is PortalVideoStream video) rate = video.RefreshMilliHz;
            }
            if (rate > 0) result[display.Id] = rate;
        }
        return result;
    }
    private void CheckAlive()
    {
        if (disposed || closed.IsCancellationRequested || bus is { IsClosed: true } || inputSession is { Connected: false })
            throw new DesktopSessionClosedException("桌面共享授权已结束，请重新开启被控并授权。");
    }
    public Task CheckAliveAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); CheckAlive(); return Task.CompletedTask; }
    public async Task<CapturedScreen> CaptureAsync(uint output, CancellationToken ct)
    {
        CheckAlive();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, closed.Token);
        for (var attempt = 0; ; attempt++)
        {
            IPortalPipe pipeline;
            lock (streamLock)
            {
                if (!streams.TryGetValue(output, out var stream) || stream.Video is not { } ready)
                {
                    var box = FindBox(output);
                    if (box.Width >= 2 && box.Height >= 2)
                        return CapturePlaceholder.Create(output, box.Width, box.Height);
                    throw new ArgumentException("所选显示器不可用。");
                }
                pipeline = ready;
            }
            try { return await pipeline.CaptureAsync(stop.Token, FirstFrameRetry.Probe); }
            catch (TimeoutException)
            {
                // No buffer yet. The caller keeps this screen pending instead of failing the host.
                int width, height;
                lock (streamLock)
                {
                    if (!streams.TryGetValue(output, out var known)) throw;
                    width = known.Width;
                    height = known.Height;
                }
                if (width < 2 || height < 2) throw;
                return CapturePlaceholder.Create(output, width, height);
            }
            catch (ObjectDisposedException) when (attempt == 0)
            {
                lock (streamLock)
                {
                    if (!streams.TryGetValue(output, out var stream) || ReferenceEquals(stream.Video, pipeline)) throw;
                }
            }
            catch (OperationCanceledException) when (closed.IsCancellationRequested && !ct.IsCancellationRequested)
            { throw new DesktopSessionClosedException("桌面共享授权已结束。"); }
        }
    }
    // Replace one PipeWire pipeline. The portal session, restore token and the other screens stay up.
    // The old pipeline reaches NULL before the new connection is opened.
    internal async Task RebuildStreamAsync(uint node, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CheckAlive();
        // Idle means no pipeline. Locked means the live pipeline stays up. Neither path opens or rebuilds here.
        if (Volatile.Read(ref pipesArmed) == 0 || SessionLock.IsLocked == true) return;
        IPortalPipe? previous;
        int width, height;
        lock (streamLock)
        {
            if (streams.TryGetValue(node, out var existing))
            {
                previous = existing.Video;
                width = existing.Width;
                height = existing.Height;
            }
            else
            {
                var box = FindBox(node);
                if (box.Node != node) throw new ArgumentException("所选显示器不可用。");
                previous = null;
                width = box.Width;
                height = box.Height;
            }
        }
        if (previous is not null) await previous.DisposeAsync();
        var created = await OpenStreamAsync(node, ct);
        var replaced = false;
        try
        {
            lock (streamLock)
            {
                if (disposed) return;
                streams[node] = (created, width, height);
                replaced = true;
            }
        }
        finally { if (!replaced) await created.DisposeAsync(); }
    }
    // A new portal socket per pipeline. dup() of one fd is one PipeWire connection, and
    // tearing one screen down frees nodes the other screen's data thread is still using.
    private async Task<IPortalPipe> OpenStreamAsync(uint node, CancellationToken ct)
    {
        if (PipeOverrideForTests is { } factory) return await factory(node, ct).ConfigureAwait(false);
        if (session is null) throw new InvalidOperationException("桌面授权会话不存在。");
        var handle = await bus.OpenPipeWireAsync(session, ct).ConfigureAwait(false);
        var descriptor = (int)handle.DangerousGetHandle();
        handle.SetHandleAsInvalid();
        return new PortalVideoStream(node, descriptor);
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
    internal bool SupportsCommittedText => !disposed && inputSession?.SupportsText == true;
    public Task TypeTextAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); CheckAlive();
        if (!enableInput || inputSession is null) throw new InvalidOperationException("文字输入未授权。");
        // libei rejects UTF-8 frames longer than 254 bytes. Each chunk is its own frame.
        foreach (var chunk in CommittedText.EisChunks(text))
        {
            ct.ThrowIfCancellationRequested();
            if (!inputSession.Text(chunk)) throw new NotSupportedException("当前桌面未提供 EIS 文字输入，请使用被控端输入法或复制粘贴。");
        }
        return Task.CompletedTask;
    }
    internal bool TryApplyTouch(uint output, HostTouchEvent input)
    {
        if (disposed || inputSession is null) return false;
        return inputSession.Apply(DesktopBackend.ResolveOutput(output), input);
    }
    internal void ResetTouch() { if (!disposed) inputSession?.Reset(); }
    private static async Task<PortalDesktopSession> LiveAsync(PortalDesktopSession fallback, CancellationToken ct)
    {
        if (DesktopBackend.CaptureRefreshActive)
            await DesktopBackend.WaitCaptureReadyAsync(ct).ConfigureAwait(false);
        var live = DesktopBackend.CurrentPortal;
        if (live is { IsDisposed: false }) return live;
        if (DesktopBackend.CaptureRefreshActive || fallback.IsDisposed)
            await DesktopBackend.WaitCaptureReadyAsync(ct).ConfigureAwait(false);
        live = DesktopBackend.CurrentPortal;
        if (live is { IsDisposed: false }) return live;
        fallback.CheckAlive();
        return fallback;
    }
    public IDesktopPointer CreatePointer(uint output) => CreatePointer(output, allowUnbound: false);

    // A pending or locked output is still a screen: layout keeps its node and geometry when the
    // pipeline has no buffer. The pointer follows that node and starts sending once a stream is bound.
    internal IDesktopPointer CreatePointer(uint output, bool allowUnbound)
        => new Pointer(this, BindOutput(output, allowUnbound));

    private uint BindOutput(uint output, bool allowUnbound)
    {
        var resolved = DesktopBackend.ResolveOutput(output);
        lock (streamLock)
        {
            if (Known(resolved)) return output;
        }
        if (allowUnbound) return output;
        throw new ArgumentException("所选显示器不可用。");
    }

    private bool Known(uint node) => streams.ContainsKey(node) || layout.Any(box => box.Node == node);

    private PortalStreamBox FindBox(uint node)
    {
        foreach (var box in layout)
            if (box.Node == node) return box;
        return default;
    }

    internal void RememberRefresh(IReadOnlyDictionary<uint, int> mapped)
    {
        lock (streamLock)
        {
            for (var i = 0; i < layout.Count; i++)
            {
                var box = layout[i];
                if (!mapped.TryGetValue(box.Node, out var hz) || hz <= 0 || hz == box.RefreshMilliHz) continue;
                layout[i] = box with { RefreshMilliHz = hz };
            }
        }
    }

    // Hardware mode from the wl_output matched to this stream. Capture and the display list
    // use it; the portal logical size stays on Width/Height for pointer layout.
    internal void RememberModes(IReadOnlyDictionary<uint, PortalDisplayBinding> bound)
    {
        lock (streamLock)
        {
            for (var i = 0; i < layout.Count; i++)
            {
                var box = layout[i];
                if (!bound.TryGetValue(box.Node, out var mode)) continue;
                var hz = mode.RefreshMilliHz > 0 ? mode.RefreshMilliHz : box.RefreshMilliHz;
                var width = mode.Width >= 2 ? mode.Width : box.ModeWidth;
                var height = mode.Height >= 2 ? mode.Height : box.ModeHeight;
                if (hz == box.RefreshMilliHz && width == box.ModeWidth && height == box.ModeHeight) continue;
                layout[i] = box with { RefreshMilliHz = hz, ModeWidth = width, ModeHeight = height };
            }
        }
    }

    private void PublishOutputs()
    {
        lock (streamLock)
        {
            layout.Sort(static (a, b) =>
            {
                var compare = a.X.CompareTo(b.X);
                if (compare != 0) return compare;
                compare = a.Y.CompareTo(b.Y);
                return compare != 0 ? compare : a.Node.CompareTo(b.Node);
            });
            Outputs = layout.Select(box => new WaylandGlobal(box.Node, "wl_output", 1)).ToArray();
        }
    }

    internal PortalStreamBox[] CopyLayout()
    {
        lock (streamLock) return layout.ToArray();
    }

    internal int RefreshOf(uint output)
    {
        var resolved = DesktopBackend.ResolveOutput(output);
        lock (streamLock)
        {
            foreach (var box in layout)
                if (box.Node == resolved && box.RefreshMilliHz > 0)
                    return box.RefreshMilliHz;
        }
        return 0;
    }

    internal static PortalDesktopSession CreateForTests() => new(null!);

    internal void AddPendingOutputForTests(uint node, int x, int y, int width, int height, int refreshMilliHz)
    {
        lock (streamLock)
        {
            layout.RemoveAll(box => box.Node == node);
            layout.Add(new PortalStreamBox(node, x, y, width, height, refreshMilliHz));
            streams.Remove(node);
        }
        PublishOutputs();
    }

    internal bool HasPipelineForTests(uint node)
    {
        lock (streamLock) return streams.TryGetValue(node, out var stream) && stream.Video is not null;
    }

    internal ScreenRect[] ScreenRectsForTests()
    {
        lock (streamLock)
            return layout.Where(box => box.Width > 0 && box.Height > 0)
                .Select(box => new ScreenRect(box.Node, box.X, box.Y, box.Width, box.Height)).ToArray();
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
            for (var attempt = 0; ; attempt++)
            try
            {
            var live = await LiveAsync(owner, ct).ConfigureAwait(false);
            var id = DesktopBackend.ResolveOutput(output);
            if (message.Action == MouseAction.MoveAbsolute)
            {
                WaylandVirtualPointer.AbsolutePayload(0, message.X, message.Y, coordinates, width, height);
                var x = coordinates == PointerCoordinates.MacNormalized ? message.X : message.X / width;
                var y = coordinates == PointerCoordinates.MacNormalized ? message.Y : message.Y / height;
                ct.ThrowIfCancellationRequested(); live.CheckAlive();
                if (live.inputSession?.Point(id, x, y) != true) throw new IOException("EIS 指针暂不可用。");
            }
            else if (message.Action == MouseAction.Scroll)
            {
                WaylandVirtualPointer.AxisPayload(0, 0, message.Y); WaylandVirtualPointer.AxisPayload(0, 1, message.X);
                ct.ThrowIfCancellationRequested(); live.CheckAlive();
                if (live.inputSession?.Scroll(message.X, -message.Y) != true) throw new IOException("EIS 滚轮暂不可用。");
            }
            else if (message.Action is MouseAction.Press or MouseAction.Release or MouseAction.Click)
            {
                var code = checked((int)WaylandVirtualPointer.LinuxButton(message.Button));
                if (message.Action == MouseAction.Click && held.Contains(code)) return;
                if (message.Action != MouseAction.Release) { held.Add(code); await live.KeyAsync("button", code, true, ct); }
                if (message.Action != MouseAction.Press) { await live.KeyAsync("button", code, false, ct); held.Remove(code); }
            }
            else throw new ArgumentException("不支持的鼠标操作。");
            return;
            }
            catch (Exception e) when (attempt == 0 && e is (DesktopSessionClosedException or IOException or ObjectDisposedException)
                && (DesktopBackend.CaptureRefreshActive || DesktopBackend.CurrentPortal != owner))
            { await DesktopBackend.WaitCaptureReadyAsync(ct).ConfigureAwait(false); }
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
            for (var attempt = 0; ; attempt++)
            try
            {
            var live = await LiveAsync(owner, ct).ConfigureAwait(false);
            if (message.Action == KeyAction.Click && held.Contains(code)) return;
            if (message.Action != KeyAction.Release && !held.Contains(code))
            { held.Add(code); await live.KeyAsync("key", code, true, ct); }
            if (message.Action != KeyAction.Press && held.Contains(code))
            { await live.KeyAsync("key", code, false, ct); held.Remove(code); }
            return;
            }
            catch (Exception e) when (attempt == 0 && e is (DesktopSessionClosedException or IOException or ObjectDisposedException)
                && (DesktopBackend.CaptureRefreshActive || DesktopBackend.CurrentPortal != owner))
            { await DesktopBackend.WaitCaptureReadyAsync(ct).ConfigureAwait(false); }
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
            IPortalPipe[] ending;
            lock (streamLock) { ending = streams.Values.Select(s => s.Video).OfType<IPortalPipe>().ToArray(); streams.Clear(); }
            foreach (var stream in ending) await stream.DisposeAsync();
            if (bus is not null) await bus.DisposeAsync();
            closed.Dispose();
        }
    }
}
