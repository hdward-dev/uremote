using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using URemote.Core;

namespace URemote.Linux;

internal readonly record struct ScreenRect(uint Output, int X, int Y, int Width, int Height);

// Virtual-desktop pixels for one absolute uinput pointer shared by every output.
internal static class UInputPointerMap
{
    internal static (int MinX, int MinY, int MaxX, int MaxY) Range(IReadOnlyList<ScreenRect> screens)
    {
        if (screens.Count == 0) throw new ArgumentException("No screens.");
        long minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var screen in screens)
        {
            if (screen.Width <= 0 || screen.Height <= 0 || screen.Width > 131072 || screen.Height > 131072)
                throw new ArgumentException("Invalid screen.");
            minX = Math.Min(minX, screen.X);
            minY = Math.Min(minY, screen.Y);
            maxX = Math.Max(maxX, (long)screen.X + screen.Width - 1);
            maxY = Math.Max(maxY, (long)screen.Y + screen.Height - 1);
        }
        if (minX < int.MinValue || minY < int.MinValue || maxX > int.MaxValue || maxY > int.MaxValue || maxX < minX || maxY < minY)
            throw new ArgumentException("Invalid screen bounds.");
        return ((int)minX, (int)minY, (int)maxX, (int)maxY);
    }

    internal static ScreenRect Resolve(IReadOnlyList<ScreenRect> screens, uint output, uint width, uint height)
    {
        foreach (var screen in screens)
            if (screen.Output == output && screen.Width > 0 && screen.Height > 0) return screen;
        if (screens.Count == 1 && screens[0].Width > 0 && screens[0].Height > 0) return screens[0];
        if (width is 0 or > 131072 || height is 0 or > 131072) throw new ArgumentException("Invalid pointer screen.");
        return new ScreenRect(output, 0, 0, (int)width, (int)height);
    }

    // Normalized positions use the same last-pixel clamp as the EIS pointer. Pixel positions are output-local.
    internal static (int X, int Y) Absolute(ScreenRect screen, double x, double y, PointerCoordinates coordinates)
    {
        if (screen.Width <= 0 || screen.Height <= 0) throw new ArgumentException("Invalid pointer screen.");
        int localX, localY;
        if (coordinates == PointerCoordinates.MacNormalized)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > 1 || y > 1)
                throw new ArgumentException("Invalid pointer position.");
            localX = (int)Math.Min(x * screen.Width, screen.Width - 1);
            localY = (int)Math.Min(y * screen.Height, screen.Height - 1);
        }
        else if (coordinates == PointerCoordinates.WindowsPixels)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > screen.Width || y > screen.Height)
                throw new ArgumentException("Invalid pointer position.");
            localX = (int)Math.Min(x, screen.Width - 1);
            localY = (int)Math.Min(y, screen.Height - 1);
        }
        else throw new ArgumentException("Invalid pointer coordinates.");
        return (screen.X + localX, screen.Y + localY);
    }

    // EIS scroll is positive-down, so the portal path negates Y. uinput REL_WHEEL is positive-up, matching UU.
    internal static (int Horizontal, int Vertical) Wheel(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 10000 || Math.Abs(y) > 10000)
            throw new ArgumentException("Invalid scroll delta.");
        return ((int)Math.Round(x), (int)Math.Round(y));
    }
}

internal interface ILockPointer : IAsyncDisposable
{
    IReadOnlyList<ScreenRect> Screens { get; }
    (int MinX, int MinY, int MaxX, int MaxY) Range { get; }
    Task MoveAsync(int x, int y, CancellationToken ct);
    Task ButtonAsync(uint code, bool down, CancellationToken ct);
    Task ScrollAsync(int horizontal, int vertical, CancellationToken ct);
}

// One absolute pointer covering the virtual desktop. KWin's greeter reads kernel devices, not the portal seat.
internal sealed class UInputPointer : ILockPointer
{
    private readonly SafeFileHandle device;
    private readonly SemaphoreSlim gate = new(1);
    private readonly HashSet<uint> held = [];
    private bool disposed;
    public IReadOnlyList<ScreenRect> Screens { get; }
    public (int MinX, int MinY, int MaxX, int MaxY) Range { get; }
    private UInputPointer(SafeFileHandle device, IReadOnlyList<ScreenRect> screens, (int MinX, int MinY, int MaxX, int MaxY) range)
    { this.device = device; Screens = screens; Range = range; }

    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(SafeFileHandle fd, uint request, nuint value);
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)] private static extern int Setup(SafeFileHandle fd, uint request, byte[] value);
    [DllImport("libc", SetLastError = true)] private static extern nint write(SafeFileHandle fd, byte[] data, nuint length);

    internal static byte[] AbsSetup(ushort code, int minimum, int maximum)
    {
        var setup = new byte[28]; // uinput_abs_setup: u16 code, pad, input_absinfo
        BinaryPrimitives.WriteUInt16LittleEndian(setup, code);
        BinaryPrimitives.WriteInt32LittleEndian(setup.AsSpan(8), minimum);
        BinaryPrimitives.WriteInt32LittleEndian(setup.AsSpan(12), maximum);
        return setup;
    }

    internal static byte[] MovePacket(int x, int y)
    {
        var packet = new byte[72];
        WriteEvent(packet.AsSpan(0), 3, 0, x); // EV_ABS ABS_X
        WriteEvent(packet.AsSpan(24), 3, 1, y); // EV_ABS ABS_Y
        return packet;
    }

    internal static byte[] KeyPacket(ushort code, int value)
    {
        var packet = new byte[48];
        WriteEvent(packet.AsSpan(0), 1, code, value); // EV_KEY
        return packet;
    }

    internal static byte[] ScrollPacket(int horizontal, int vertical)
    {
        var packet = new byte[72];
        var offset = 0;
        if (vertical != 0) { WriteEvent(packet.AsSpan(offset), 2, 8, vertical); offset += 24; } // EV_REL REL_WHEEL
        if (horizontal != 0) { WriteEvent(packet.AsSpan(offset), 2, 6, horizontal); offset += 24; } // EV_REL REL_HWHEEL
        return packet[..(offset + 24)];
    }

    private static void WriteEvent(Span<byte> target, ushort type, ushort code, int value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target[16..], type);
        BinaryPrimitives.WriteUInt16LittleEndian(target[18..], code);
        BinaryPrimitives.WriteInt32LittleEndian(target[20..], value);
    }

    public static async Task<UInputPointer> CreateAsync(IReadOnlyList<ScreenRect> screens, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8) throw new PlatformNotSupportedException("Linux 64-bit required.");
        var range = UInputPointerMap.Range(screens);
        ct.ThrowIfCancellationRequested();
        var raw = open("/dev/uinput", 0x80801); // WRONLY | NONBLOCK | CLOEXEC; never read physical input.
        if (raw < 0) throw new UnauthorizedAccessException("U远程需要当前用户的 /dev/uinput 写权限才能在锁屏界面移动鼠标。");
        var fd = new SafeFileHandle((IntPtr)raw, true);
        try
        {
            Check(ioctl(fd, 0x40045564, 1)); // UI_SET_EVBIT EV_KEY
            Check(ioctl(fd, 0x40045564, 2)); // EV_REL
            Check(ioctl(fd, 0x40045564, 3)); // EV_ABS
            foreach (var button in new nuint[] { 0x110, 0x111, 0x112, 0x113, 0x114 })
                Check(ioctl(fd, 0x40045565, button)); // UI_SET_KEYBIT
            Check(ioctl(fd, 0x40045566, 8)); // UI_SET_RELBIT REL_WHEEL
            Check(ioctl(fd, 0x40045566, 6)); // REL_HWHEEL
            Check(ioctl(fd, 0x40045567, 0)); // UI_SET_ABSBIT ABS_X
            Check(ioctl(fd, 0x40045567, 1)); // ABS_Y
            Check(Setup(fd, 0x401c5504, AbsSetup(0, range.MinX, range.MaxX))); // UI_ABS_SETUP
            Check(Setup(fd, 0x401c5504, AbsSetup(1, range.MinY, range.MaxY)));
            _ = ioctl(fd, 0x4004556e, 0); // UI_SET_PROPBIT INPUT_PROP_POINTER; older kernels may reject it.
            var setup = new byte[92];
            BinaryPrimitives.WriteUInt16LittleEndian(setup, 3); // BUS_USB
            System.Text.Encoding.ASCII.GetBytes("AsterDock URemote Pointer").CopyTo(setup, 8);
            Check(Setup(fd, 0x405c5503, setup)); // UI_DEV_SETUP
            Check(ioctl(fd, 0x5501, 0)); // UI_DEV_CREATE
            var pointer = new UInputPointer(fd, screens, range);
            try { await Task.Delay(250, ct).ConfigureAwait(false); return pointer; }
            catch { await pointer.DisposeAsync().ConfigureAwait(false); throw; }
        }
        catch { fd.Dispose(); throw; }
    }

    private static void Check(int result)
    { if (result < 0) throw new IOException("uinput pointer setup failed: " + Marshal.GetLastPInvokeError()); }

    public Task MoveAsync(int x, int y, CancellationToken ct) => SendAsync(MovePacket(x, y), ct);
    public async Task ButtonAsync(uint code, bool down, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (down) held.Add(code);
            else held.Remove(code);
            await WriteAsync(KeyPacket(checked((ushort)code), down ? 1 : 0), ct).ConfigureAwait(false);
        }
        catch (IOException) { Fail(); throw; }
        finally { gate.Release(); }
    }
    public Task ScrollAsync(int horizontal, int vertical, CancellationToken ct)
    {
        if (horizontal == 0 && vertical == 0) return Task.CompletedTask;
        return SendAsync(ScrollPacket(horizontal, vertical), ct);
    }
    private async Task SendAsync(byte[] packet, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await WriteAsync(packet, ct).ConfigureAwait(false);
        }
        catch (IOException) { Fail(); throw; }
        finally { gate.Release(); }
    }
    private async Task WriteAsync(byte[] packet, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var count = write(device, packet, (nuint)packet.Length);
            if (count == packet.Length) return;
            var error = Marshal.GetLastPInvokeError();
            if (count < 0 && error == 4) continue; // EINTR
            if (count < 0 && error == 11) { await Task.Delay(5, ct).ConfigureAwait(false); continue; }
            throw new IOException("uinput pointer write failed: " + error);
        }
    }
    private void Fail()
    {
        disposed = true;
        ioctl(device, 0x5502, 0);
        device.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                foreach (var button in held)
                    await WriteAsync(KeyPacket(checked((ushort)button), 0), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException) { }
            finally { held.Clear(); ioctl(device, 0x5502, 0); device.Dispose(); }
        }
        finally { gate.Release(); }
    }
}

internal static class UInputPointerHub
{
    private static readonly SemaphoreSlim gate = new(1);
    private static UInputPointer? device;
    private static int users;

    internal static async Task<ILockPointer> AcquireAsync(IReadOnlyList<ScreenRect> screens, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (device is null) device = await UInputPointer.CreateAsync(screens, ct).ConfigureAwait(false);
            users++;
            return device;
        }
        finally { gate.Release(); }
    }

    internal static async Task ReleaseAsync(ILockPointer pointer)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (pointer is not UInputPointer concrete || !ReferenceEquals(device, concrete))
            {
                await pointer.DisposeAsync().ConfigureAwait(false);
                return;
            }
            if (users > 0) users--;
            if (users > 0) return;
            device = null;
            await concrete.DisposeAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
