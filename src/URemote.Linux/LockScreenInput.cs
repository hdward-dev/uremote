using URemote.Core;

namespace URemote.Linux;

// KWin ignores RemoteDesktop / EIS input while kscreenlocker_greet is up. Kernel devices still reach it.
internal interface ILockSource
{
    bool? IsLocked { get; }
    event Action? Changed;
}

internal sealed class LiveLockSource : ILockSource
{
    internal static readonly LiveLockSource Instance = new();
    private LiveLockSource() { }
    public bool? IsLocked => SessionLock.IsLocked;
    public event Action? Changed
    {
        add => SessionLock.Changed += value;
        remove => SessionLock.Changed -= value;
    }
}

internal static class LockInputNotices
{
    private static string? lastPointer;
    internal static void Pointer(Action<string> report, string line)
    {
        lock (typeof(LockInputNotices))
        {
            if (lastPointer == line) return;
            lastPointer = line;
            report(line);
        }
    }
    internal static void ResetForTests() { lock (typeof(LockInputNotices)) lastPointer = null; }
}

internal static class LockScreenInput
{
    internal static async Task<IDesktopKeyboard> WrapKeyboardAsync(IDesktopKeyboard primary, string primaryName, Action<string> report, CancellationToken ct)
    {
        var router = new LockRoutedKeyboard(primary, primaryName, static token => OpenKeyboard(token), LiveLockSource.Instance, report);
        try { await router.StartAsync(ct, probe: true).ConfigureAwait(false); return router; }
        catch { await router.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal static async Task<IDesktopPointer> WrapPointerAsync(uint output, IDesktopPointer primary, uint width, uint height, Action<string> report, CancellationToken ct)
    {
        var router = new LockRoutedPointer(output, primary, width, height, null, UInputPointerHub.AcquireAsync, UInputPointerHub.ReleaseAsync, LiveLockSource.Instance, report);
        try { await router.StartAsync(ct, probe: true).ConfigureAwait(false); return router; }
        catch { await router.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static async Task<IDesktopKeyboard> OpenKeyboard(CancellationToken ct) => await UInputKeyboard.CreateAsync(ct).ConfigureAwait(false);

    internal static async Task<LockRoutedKeyboard> CreateKeyboardForTests(IDesktopKeyboard primary, string primaryName,
        Func<CancellationToken, Task<IDesktopKeyboard>> openUinput, ILockSource source, Action<string> report, CancellationToken ct)
    {
        var router = new LockRoutedKeyboard(primary, primaryName, openUinput, source, report);
        await router.StartAsync(ct, probe: false).ConfigureAwait(false);
        return router;
    }

    internal static async Task<LockRoutedPointer> CreatePointerForTests(uint output, IDesktopPointer primary, uint width, uint height,
        IReadOnlyList<ScreenRect> screens, Func<IReadOnlyList<ScreenRect>, CancellationToken, Task<ILockPointer>> acquire,
        ILockSource source, Action<string> report, CancellationToken ct)
    {
        var router = new LockRoutedPointer(output, primary, width, height, screens, acquire, static pointer => pointer.DisposeAsync().AsTask(), source, report);
        await router.StartAsync(ct, probe: false).ConfigureAwait(false);
        return router;
    }
}

internal sealed class LockRoutedKeyboard : IDesktopKeyboard
{
    private readonly IDesktopKeyboard primary;
    private readonly string primaryName;
    private readonly Func<CancellationToken, Task<IDesktopKeyboard>> openUinput;
    private readonly ILockSource source;
    private readonly Action<string> report;
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<int, IDesktopKeyboard> owners = [];
    private IDesktopKeyboard? uinput;
    private Task tail = Task.CompletedTask;
    private int hooked, disposed;
    private bool usingUinput, denied;

    internal LockRoutedKeyboard(IDesktopKeyboard primary, string primaryName, Func<CancellationToken, Task<IDesktopKeyboard>> openUinput, ILockSource source, Action<string> report)
    {
        this.primary = primary;
        this.primaryName = primaryName;
        this.openUinput = openUinput;
        this.source = source;
        this.report = report;
    }

    internal Task Settled() => Volatile.Read(ref tail);

    internal async Task StartAsync(CancellationToken ct, bool probe)
    {
        Hook();
        if (probe) await SessionLock.EnsureKnownAsync(ct).ConfigureAwait(false);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            await ApplyLockAsync(source.IsLocked == true, ct).ConfigureAwait(false);
            if (!usingUinput && !denied) report("keyboard-backend=" + primaryName);
        }
        finally { gate.Release(); }
    }

    public async Task ApplyAsync(HostKeyMessage message, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            await ApplyLockAsync(source.IsLocked == true, ct).ConfigureAwait(false);
            var target = usingUinput && uinput is not null ? uinput : primary;
            if (message.Action == KeyAction.Release)
            {
                if (owners.TryGetValue(message.MacKey, out var owner)) target = owner;
            }
            else if (owners.TryGetValue(message.MacKey, out var owner)) target = owner;
            else owners[message.MacKey] = target;
            try { await target.ApplyAsync(message, ct).ConfigureAwait(false); }
            catch (IOException) when (ReferenceEquals(target, uinput))
            {
                await AbandonAsync().ConfigureAwait(false);
                report("keyboard-backend=" + primaryName + ";reason=uinput-unavailable");
                owners[message.MacKey] = primary;
                await primary.ApplyAsync(message, ct).ConfigureAwait(false);
            }
            if (message.Action != KeyAction.Press) owners.Remove(message.MacKey);
        }
        finally { gate.Release(); }
    }

    private async Task ApplyLockAsync(bool locked, CancellationToken ct)
    {
        if (denied || locked == usingUinput) return;
        if (locked)
        {
            try { uinput ??= await openUinput(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                denied = true;
                report("keyboard-backend=" + primaryName + ";reason=uinput-unavailable");
                return;
            }
            await ReleaseOwnedAsync(primary, ct).ConfigureAwait(false);
            usingUinput = true;
            report("keyboard-backend=uinput;reason=locked");
            return;
        }
        await ReleaseOwnedAsync(uinput, ct).ConfigureAwait(false);
        if (uinput is not null)
        {
            var closing = uinput;
            uinput = null;
            await closing.DisposeAsync().ConfigureAwait(false);
        }
        usingUinput = false;
        report("keyboard-backend=" + primaryName + ";reason=unlocked");
    }

    private async Task ReleaseOwnedAsync(IDesktopKeyboard? device, CancellationToken ct)
    {
        if (device is null) return;
        foreach (var key in owners.Where(pair => ReferenceEquals(pair.Value, device)).Select(pair => pair.Key).ToArray())
        {
            try { await device.ApplyAsync(new HostKeyMessage(KeyAction.Release, key), ct).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
            owners.Remove(key);
        }
    }

    private async Task AbandonAsync()
    {
        denied = true;
        usingUinput = false;
        var closing = uinput;
        uinput = null;
        if (closing is not null)
        {
            try { await closing.DisposeAsync().ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }

    private void Hook()
    {
        if (Interlocked.Exchange(ref hooked, 1) != 0) return;
        source.Changed += OnChanged;
    }

    private void OnChanged()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        Volatile.Write(ref tail, Task.Run(SyncAsync));
    }

    private async Task SyncAsync()
    {
        try
        {
            await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                if (disposed != 0) return;
                await ApplyLockAsync(source.IsLocked == true, lifetime.Token).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception e)
        {
            denied = true;
            report("keyboard-backend=" + primaryName + ";reason=uinput-unavailable;type=" + e.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (hooked != 0) source.Changed -= OnChanged;
        lifetime.Cancel();
        try { await Settled().ConfigureAwait(false); } catch (Exception) { }
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (uinput is not null)
            {
                try { await ReleaseOwnedAsync(uinput, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
                await uinput.DisposeAsync().ConfigureAwait(false);
                uinput = null;
            }
            await primary.DisposeAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); lifetime.Dispose(); }
    }
}

internal sealed class LockRoutedPointer : IDesktopPointer
{
    private static readonly int[] Buttons = [1, 2, 4, 8, 16];
    private readonly uint output;
    private readonly IDesktopPointer primary;
    private readonly IReadOnlyList<ScreenRect>? fixedScreens;
    private readonly Func<IReadOnlyList<ScreenRect>, CancellationToken, Task<ILockPointer>> acquire;
    private readonly Func<ILockPointer, Task> release;
    private readonly ILockSource source;
    private readonly Action<string> report;
    private readonly SemaphoreSlim gate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<int> heldButtons = [];
    private ScreenRect[]? cached;
    private ILockPointer? uinput;
    private Task tail = Task.CompletedTask;
    private uint width, height;
    private int hooked, disposed;
    private bool usingUinput, denied;

    internal LockRoutedPointer(uint output, IDesktopPointer primary, uint width, uint height, IReadOnlyList<ScreenRect>? screens,
        Func<IReadOnlyList<ScreenRect>, CancellationToken, Task<ILockPointer>> acquire, Func<ILockPointer, Task> release, ILockSource source, Action<string> report)
    {
        this.output = output;
        this.primary = primary;
        this.width = width;
        this.height = height;
        fixedScreens = screens;
        this.acquire = acquire;
        this.release = release;
        this.source = source;
        this.report = report;
    }

    internal Task Settled() => Volatile.Read(ref tail);

    internal async Task StartAsync(CancellationToken ct, bool probe)
    {
        Hook();
        if (probe) await SessionLock.EnsureKnownAsync(ct).ConfigureAwait(false);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            await ApplyLockAsync(source.IsLocked == true, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task ApplyAsync(HostMouseMessage message, PointerCoordinates coordinates, uint width, uint height, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (width > 0) this.width = width;
            if (height > 0) this.height = height;
            await ApplyLockAsync(source.IsLocked == true, ct).ConfigureAwait(false);
            if (usingUinput && uinput is not null) await ApplyUinputAsync(message, coordinates, ct).ConfigureAwait(false);
            else await primary.ApplyAsync(message, coordinates, width, height, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task ApplyUinputAsync(HostMouseMessage message, PointerCoordinates coordinates, CancellationToken ct)
    {
        try
        {
            switch (message.Action)
            {
                case MouseAction.MoveAbsolute:
                    var screen = UInputPointerMap.Resolve(uinput!.Screens, output, width, height);
                    var point = UInputPointerMap.Absolute(screen, message.X, message.Y, coordinates);
                    var range = uinput.Range;
                    await uinput.MoveAsync(Math.Clamp(point.X, range.MinX, range.MaxX), Math.Clamp(point.Y, range.MinY, range.MaxY), ct).ConfigureAwait(false);
                    break;
                case MouseAction.Scroll:
                    var wheel = UInputPointerMap.Wheel(message.X, message.Y);
                    await uinput!.ScrollAsync(wheel.Horizontal, wheel.Vertical, ct).ConfigureAwait(false);
                    break;
                case MouseAction.Press:
                case MouseAction.Release:
                case MouseAction.Click:
                    var code = WaylandVirtualPointer.LinuxButton(message.Button);
                    if (message.Action == MouseAction.Click && heldButtons.Contains(message.Button)) return;
                    if (message.Action != MouseAction.Release)
                    {
                        heldButtons.Add(message.Button);
                        await uinput!.ButtonAsync(code, true, ct).ConfigureAwait(false);
                    }
                    if (message.Action != MouseAction.Press)
                    {
                        await uinput!.ButtonAsync(code, false, ct).ConfigureAwait(false);
                        heldButtons.Remove(message.Button);
                    }
                    break;
                default: throw new ArgumentException("不支持的鼠标操作。");
            }
        }
        catch (IOException)
        {
            await AbandonAsync().ConfigureAwait(false);
            LockInputNotices.Pointer(report, "pointer-backend=portal;reason=uinput-unavailable");
            await primary.ApplyAsync(message, coordinates, width, height, ct).ConfigureAwait(false);
        }
    }

    private async Task ApplyLockAsync(bool locked, CancellationToken ct)
    {
        if (denied || locked == usingUinput) return;
        if (locked)
        {
            await ReleasePrimaryAsync(ct).ConfigureAwait(false);
            try
            {
                var screens = await GeometryAsync(ct).ConfigureAwait(false);
                uinput = await acquire(screens, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException or ArgumentException)
            {
                denied = true;
                LockInputNotices.Pointer(report, "pointer-backend=portal;reason=uinput-unavailable");
                return;
            }
            usingUinput = true;
            LockInputNotices.Pointer(report, "pointer-backend=uinput;reason=locked");
            return;
        }
        await ReleaseUinputAsync(ct).ConfigureAwait(false);
        if (uinput is not null)
        {
            var closing = uinput;
            uinput = null;
            await release(closing).ConfigureAwait(false);
        }
        usingUinput = false;
        LockInputNotices.Pointer(report, "pointer-backend=portal;reason=unlocked");
    }

    private async Task<IReadOnlyList<ScreenRect>> GeometryAsync(CancellationToken ct)
    {
        if (fixedScreens is { Count: > 0 }) return fixedScreens;
        var portalScreens = DesktopBackend.ScreenLayout();
        if (portalScreens.Length > 0) return portalScreens;
        if (cached is { Length: > 0 }) return cached;
        try
        {
            var outputs = await WaylandOutputRefresh.ReadOutputsAsync(ct).ConfigureAwait(false);
            var list = outputs.Where(item => item.Width > 0 && item.Height > 0)
                .Select(item => new ScreenRect(item.Name, item.X, item.Y, item.Width, item.Height)).ToArray();
            if (list.Length > 0) return cached = list;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or PlatformNotSupportedException
            or NotSupportedException or ArgumentException or FormatException or UnauthorizedAccessException
            or System.Net.Sockets.SocketException or DllNotFoundException or EntryPointNotFoundException) { }
        if (width is 0 or > 131072 || height is 0 or > 131072) throw new ArgumentException("Invalid pointer screen.");
        return cached = [new ScreenRect(output, 0, 0, (int)width, (int)height)];
    }

    private async Task ReleasePrimaryAsync(CancellationToken ct)
    {
        foreach (var button in Buttons)
        {
            try
            {
                await primary.ApplyAsync(new HostMouseMessage(MouseAction.Release, Button: button), PointerCoordinates.MacNormalized,
                    Math.Max(width, 1), Math.Max(height, 1), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException or ArgumentException or DesktopSessionClosedException) { }
        }
    }

    private async Task ReleaseUinputAsync(CancellationToken ct)
    {
        if (uinput is null) return;
        foreach (var button in heldButtons.ToArray())
        {
            try { await uinput.ButtonAsync(WaylandVirtualPointer.LinuxButton(button), false, ct).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
        heldButtons.Clear();
    }

    private async Task AbandonAsync()
    {
        denied = true;
        usingUinput = false;
        heldButtons.Clear();
        var closing = uinput;
        uinput = null;
        if (closing is null) return;
        try { await release(closing).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    private void Hook()
    {
        if (Interlocked.Exchange(ref hooked, 1) != 0) return;
        source.Changed += OnChanged;
    }

    private void OnChanged()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        Volatile.Write(ref tail, Task.Run(SyncAsync));
    }

    private async Task SyncAsync()
    {
        try
        {
            await gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                if (disposed != 0) return;
                await ApplyLockAsync(source.IsLocked == true, lifetime.Token).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception e)
        {
            denied = true;
            LockInputNotices.Pointer(report, "pointer-backend=portal;reason=uinput-unavailable;type=" + e.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (hooked != 0) source.Changed -= OnChanged;
        lifetime.Cancel();
        try { await Settled().ConfigureAwait(false); } catch (Exception) { }
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (uinput is not null)
            {
                try { await ReleaseUinputAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
                var closing = uinput;
                uinput = null;
                await release(closing).ConfigureAwait(false);
            }
            await primary.DisposeAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); lifetime.Dispose(); }
    }
}
