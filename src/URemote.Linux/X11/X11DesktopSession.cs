using System.Diagnostics;
using URemote.Core;

namespace URemote.Linux;

/// <summary>
/// Plain X11 capture and input. Wayland portal sessions never construct this type.
/// Target fps is min(refresh, requested). Damage keeps that rate; a quiet screen may drop to the static floor.
/// </summary>
internal sealed class X11DesktopSession : IAsyncDisposable
{
    private readonly X11Connection connection;
    private readonly Dictionary<uint, Slot> slots = [];
    private readonly object gate = new();
    private X11Monitor[] monitors;
    private int armedScreens;
    private bool captureArmed, disposed;

    private X11DesktopSession(X11Connection connection)
    {
        this.connection = connection;
        monitors = connection.Monitors(refresh: true).ToArray();
        var shm = connection.HasShm ? 1 : 0;
        var damage = connection.HasDamage ? 1 : 0;
        var xtest = connection.HasXTest ? 1 : 0;
        Console.WriteLine($"x11-backend;shm={shm};damage={damage};xtest={xtest};screens={monitors.Length}");
        for (var i = 0; i < monitors.Length; i++)
        {
            var monitor = monitors[i];
            Console.WriteLine($"x11-display;index={i + 1};output={monitor.Id};name={monitor.Name};x={monitor.X};y={monitor.Y};width={monitor.Width};height={monitor.Height};refresh-millihz={monitor.MilliHz}");
        }
        _ = Task.Run(ProbeLockAsync);
    }

    internal static Task<X11DesktopSession> OpenAsync(bool enableInput, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var opened = X11Connection.Open(null);
        X11DesktopSession session;
        try { session = new X11DesktopSession(opened); }
        catch { opened.Dispose(); throw; }
        if (enableInput && !opened.HasXTest)
            Console.WriteLine("x11-input=unavailable;extension=XTEST");
        return Task.FromResult(session);
    }

    internal static IReadOnlyList<WaylandGlobal> Discover(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var connection = X11Connection.Open(null);
        return connection.Monitors(refresh: true).Select(monitor => monitor.Global).ToArray();
    }

    internal static Task<CapturedScreen> CaptureOneShotAsync(uint output, bool includeCursor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            using var connection = X11Connection.Open(null);
            var monitor = connection.Monitors(refresh: true).FirstOrDefault(item => item.Id == output)
                ?? throw new ArgumentException("所选显示器不可用。");
            return Frame(monitor, connection.Grab(monitor, includeCursor));
        }, ct);
    }

    internal IReadOnlyList<WaylandGlobal> Outputs()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            monitors = connection.Monitors(refresh: true).ToArray();
            return monitors.Select(monitor => monitor.Global).ToArray();
        }
    }

    internal IReadOnlyDictionary<uint, int> RefreshRates()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (monitors.Length == 0) monitors = connection.Monitors(refresh: true).ToArray();
            return monitors.ToDictionary(monitor => monitor.Id, monitor => monitor.MilliHz);
        }
    }

    internal bool DamageSubscribed => connection.DamageSubscribed;
    internal bool DamageSupported => connection.DamageSupported;
    internal int ShmBuffers => connection.ShmBufferCount;

    internal (int Width, int Height) SizeOf(uint output)
    {
        try
        {
            var monitor = Find(output);
            return (monitor.Width, monitor.Height);
        }
        catch (ArgumentException) { return (0, 0); }
    }

    internal void ArmCapture(IReadOnlyList<uint> screens)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            armedScreens = screens.Count;
            captureArmed = true;
        }
        connection.SubscribeDamage();
    }

    internal int ReleaseCapture()
    {
        int released;
        lock (gate)
        {
            if (disposed) return 0;
            released = captureArmed ? armedScreens : 0;
            captureArmed = false;
            armedScreens = 0;
            foreach (var slot in slots.Values)
                if (slot.Pixels is not null) Array.Clear(slot.Pixels);
            slots.Clear();
        }
        connection.ReleaseCaptureResources();
        return released;
    }

    internal async Task<CapturedScreen> CaptureAsync(uint output, bool includeCursor, CancellationToken ct)
    {
        if (!captureArmed)
        {
            var idle = SizeOf(output);
            var width = idle.Width >= 2 ? idle.Width : 1280;
            var height = idle.Height >= 2 ? idle.Height : 720;
            return CapturePlaceholder.Create(output, width, height);
        }
        var monitor = Find(output);
        var slot = SlotFor(monitor);
        var requested = X11FrameRateNotes.Requested(output);
        var target = X11FramePace.TargetFps(X11FramePace.RefreshHz(monitor.MilliHz), requested);
        var since = slot.Last == 0 ? TimeSpan.FromHours(1) : Stopwatch.GetElapsedTime(slot.Last);
        var changed = slot.Pixels is null || slot.Width != monitor.Width || slot.Height != monitor.Height || connection.ConsumeDamage(output);
        var delay = X11FramePace.Delay(since, changed, target);
        var started = Stopwatch.GetTimestamp();
        while (delay > TimeSpan.Zero)
        {
            ct.ThrowIfCancellationRequested();
            var slice = delay > TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : delay;
            var woke = await connection.WaitForEventAsync(slice, ct).ConfigureAwait(false);
            var elapsed = since + Stopwatch.GetElapsedTime(started);
            if (woke && connection.ConsumeDamage(output)) changed = true;
            delay = X11FramePace.Delay(elapsed, changed, target);
        }
        // Anchor the clock when the frame is due, before the copy. Capture time then counts
        // toward the next period, so a tight caller stays at the target instead of target-plus-copy.
        slot.Last = Stopwatch.GetTimestamp();
        byte[] pixels;
        if (changed || slot.Pixels is null)
        {
            pixels = connection.Grab(monitor, includeCursor);
            lock (gate)
            {
                if (!captureArmed)
                {
                    Array.Clear(pixels);
                    var width = monitor.Width >= 2 ? monitor.Width : 1280;
                    var height = monitor.Height >= 2 ? monitor.Height : 720;
                    return CapturePlaceholder.Create(monitor.Id, width, height);
                }
                if (slot.Pixels is not null) Array.Clear(slot.Pixels);
                slot.Pixels = (byte[])pixels.Clone();
                slot.Width = monitor.Width;
                slot.Height = monitor.Height;
            }
        }
        else
        {
            lock (gate) pixels = (byte[])slot.Pixels.Clone();
        }
        NoteRate(monitor, slot, requested, target);
        return Frame(monitor, pixels, repeated: !changed);
    }

    internal IDesktopPointer CreatePointer(uint output, uint width, uint height)
    {
        var monitor = Find(output);
        if (width != 0 && height != 0 && (width != monitor.Width || height != monitor.Height))
            monitor = monitor with { Width = (int)width, Height = (int)height };
        return new Pointer(connection, monitor);
    }

    internal IDesktopKeyboard CreateKeyboard() => new Keyboard(connection);

    internal Task TypeTextAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        HostControlInput.ValidateText(text);
        if (!X11Text.TryPlan(text, out var plan))
            throw new NotSupportedException("当前 X11 键位表无法输入这段文字，请使用被控端输入法或剪贴板。");
        const int shift = 50; // evdev Shift_L + 8
        foreach (var step in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (step.Shift) connection.Key(shift, true);
            connection.Key(step.KeyCode, true);
            connection.Key(step.KeyCode, false);
            if (step.Shift) connection.Key(shift, false);
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var slot in slots.Values)
                if (slot.Pixels is not null) Array.Clear(slot.Pixels);
            slots.Clear();
        }
        connection.Dispose();
        await Task.CompletedTask;
    }

    private X11Monitor Find(uint output)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            foreach (var monitor in monitors)
                if (monitor.Id == output) return monitor;
            monitors = connection.Monitors(refresh: true).ToArray();
            foreach (var monitor in monitors)
                if (monitor.Id == output) return monitor;
        }
        throw new ArgumentException("所选显示器不可用。");
    }

    private Slot SlotFor(X11Monitor monitor)
    {
        lock (gate)
        {
            if (!slots.TryGetValue(monitor.Id, out var slot)) slots[monitor.Id] = slot = new Slot();
            return slot;
        }
    }

    private void NoteRate(X11Monitor monitor, Slot slot, int requested, int target)
    {
        slot.Frames++;
        var elapsed = Stopwatch.GetElapsedTime(slot.Window);
        if (elapsed < TimeSpan.FromSeconds(1) || slot.Frames <= 0) return;
        var index = 1;
        lock (gate)
        {
            for (var i = 0; i < monitors.Length; i++)
                if (monitors[i].Id == monitor.Id) index = i + 1;
        }
        var actual = slot.Frames / elapsed.TotalSeconds;
        Console.WriteLine($"x11-frame-rate;screen={index};output={monitor.Id};requested={requested};target={target};actual={actual:F1}");
        slot.Frames = 0;
        slot.Window = Stopwatch.GetTimestamp();
    }

    private static CapturedScreen Frame(X11Monitor monitor, byte[] pixels, bool repeated = false)
        => new(monitor.Id, (uint)monitor.Width, (uint)monitor.Height, (uint)(monitor.Width * 4), 1, false, pixels, Repeated: repeated);

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(X11DesktopSession));
    }

    private static async Task ProbeLockAsync()
    {
        try
        {
            var locked = await X11LockProbe.TryReadAsync(CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine("x11-session-lock=" + (locked switch { true => "1", false => "0", _ => "unknown" }));
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Console.WriteLine("x11-session-lock=unknown"); }
    }

    private sealed class Slot
    {
        internal byte[]? Pixels;
        internal int Width, Height, Frames;
        internal long Last;
        internal long Window = Stopwatch.GetTimestamp();
    }

    private sealed class Pointer(X11Connection connection, X11Monitor monitor) : IDesktopPointer
    {
        private readonly HashSet<int> held = [];
        private readonly object gate = new();
        private bool disposed;

        public Task ApplyAsync(HostMouseMessage message, PointerCoordinates coordinates, uint width, uint height, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (message.ScreenId is not null) throw new ArgumentException("请先解析显示器编号。");
            var screen = monitor;
            if (width != 0 && height != 0) screen = screen with { Width = (int)width, Height = (int)height };
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(Pointer));
                if (message.Action == MouseAction.MoveAbsolute)
                {
                    var point = X11Model.RootPoint(screen, message.X, message.Y, coordinates);
                    connection.Move(point.X, point.Y);
                }
                else if (message.Action == MouseAction.Scroll)
                {
                    foreach (var button in X11Model.WheelButtons(message.X, message.Y))
                    {
                        connection.Button(button, true);
                        connection.Button(button, false);
                    }
                }
                else
                {
                    var button = X11Model.Button(message.Button);
                    if (message.Action == MouseAction.Click && held.Contains(button)) return Task.CompletedTask;
                    if (message.Action != MouseAction.Release && held.Add(button)) connection.Button(button, true);
                    if (message.Action != MouseAction.Press && held.Remove(button)) connection.Button(button, false);
                }
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            int[] release;
            lock (gate)
            {
                if (disposed) return ValueTask.CompletedTask;
                disposed = true;
                release = held.ToArray();
                held.Clear();
            }
            foreach (var button in release)
                try { connection.Button(button, false); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException) { }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Keyboard(X11Connection connection) : IDesktopKeyboard
    {
        private readonly HashSet<int> held = [];
        private readonly object gate = new();
        private bool disposed;

        public Task ApplyAsync(HostKeyMessage message, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (WaylandVirtualKeyboard.LinuxKey(message.MacKey) is not { } linux) return Task.CompletedTask;
            var code = X11Model.XKeyCode(linux);
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(Keyboard));
                if (message.Action == KeyAction.Click && held.Contains(code)) return Task.CompletedTask;
                if (message.Action != KeyAction.Release && held.Add(code)) connection.Key(code, true);
                if (message.Action != KeyAction.Press && held.Remove(code)) connection.Key(code, false);
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            int[] release;
            lock (gate)
            {
                if (disposed) return ValueTask.CompletedTask;
                disposed = true;
                release = held.ToArray();
                held.Clear();
            }
            foreach (var code in release)
                try { connection.Key(code, false); }
                catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException) { }
            return ValueTask.CompletedTask;
        }
    }
}
