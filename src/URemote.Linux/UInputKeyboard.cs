using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using URemote.Core;

namespace URemote.Linux;

/// <summary>Kernel virtual keyboard: compositor shortcuts and application input use the same path.</summary>
public sealed class UInputKeyboard : IDesktopKeyboard
{
    private readonly SafeFileHandle device;
    private readonly SemaphoreSlim gate = new(1);
    private readonly HashSet<uint> held = [];
    private bool disposed;
    private UInputKeyboard(SafeFileHandle device) => this.device = device;
    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(SafeFileHandle fd, uint request, nuint value);
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)] private static extern int Setup(SafeFileHandle fd, uint request, byte[] value);
    [DllImport("libc", SetLastError = true)] private static extern nint write(SafeFileHandle fd, byte[] data, nuint length);

    public static async Task<UInputKeyboard> CreateAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8) throw new PlatformNotSupportedException("Linux 64-bit required.");
        ct.ThrowIfCancellationRequested();
        var raw = open("/dev/uinput", 0x80801); // WRONLY | NONBLOCK | CLOEXEC; never read physical input.
        if (raw < 0) throw new UnauthorizedAccessException("U远程需要当前用户的 /dev/uinput 写权限才能使用系统快捷键。");
        var fd = new SafeFileHandle((IntPtr)raw, true);
        try
        {
            Check(ioctl(fd, 0x40045564, 1)); // UI_SET_EVBIT EV_KEY
            foreach (var key in Enumerable.Range(0,128).Select(WaylandVirtualKeyboard.LinuxKey).OfType<uint>().Distinct())
                Check(ioctl(fd, 0x40045565, key)); // UI_SET_KEYBIT
            var setup = new byte[92]; // uinput_setup: input_id, name[80], ff_effects_max
            BinaryPrimitives.WriteUInt16LittleEndian(setup, 3); // BUS_USB
            System.Text.Encoding.ASCII.GetBytes("AsterDock URemote Keyboard").CopyTo(setup, 8);
            Check(Setup(fd, 0x405c5503, setup));
            Check(ioctl(fd, 0x5501, 0)); // UI_DEV_CREATE
            var keyboard = new UInputKeyboard(fd);
            try { await Task.Delay(250, ct); return keyboard; } // allow compositor hotplug discovery
            catch { await keyboard.DisposeAsync(); throw; }
        }
        catch { fd.Dispose(); throw; }
    }
    private static void Check(int result)
    { if (result < 0) throw new IOException("uinput device setup failed: " + Marshal.GetLastPInvokeError()); }

    public async Task ApplyAsync(HostKeyMessage message, CancellationToken ct = default)
    {
        if (WaylandVirtualKeyboard.LinuxKey(message.MacKey) is not { } key) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await gate.WaitAsync(timeout.Token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (message.Action == KeyAction.Click && held.Contains(key)) return;
            if (message.Action != KeyAction.Release && held.Add(key)) await SendAsync(key, true, timeout.Token);
            if (message.Action != KeyAction.Press && held.Contains(key))
            { await SendAsync(key, false, timeout.Token); held.Remove(key); }
        }
        catch { disposed = true; ioctl(device, 0x5502, 0); device.Dispose(); throw; }
        finally { gate.Release(); }
    }
    private async Task SendAsync(uint key, bool down, CancellationToken ct)
    {
        // Two input_event records, EV_KEY followed by SYN_REPORT. Kernel supplies timestamps.
        var packet = new byte[48];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(18), checked((ushort)key));
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(20), down ? 1 : 0);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var count = write(device, packet, (nuint)packet.Length);
            if (count == packet.Length) return;
            var error = Marshal.GetLastPInvokeError();
            if (count < 0 && error == 4) continue; // EINTR
            if (count < 0 && error == 11) { await Task.Delay(5, ct); continue; }
            throw new IOException("uinput event write failed: " + error);
        }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { foreach (var key in held) await SendAsync(key, false, timeout.Token); }
            catch (Exception e) when (e is IOException or OperationCanceledException) { }
            finally { held.Clear(); ioctl(device, 0x5502, 0); device.Dispose(); }
        }
        finally { gate.Release(); }
    }
}
