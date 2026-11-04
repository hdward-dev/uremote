using System.Runtime.InteropServices;
using System.Text;

namespace URemote.Linux;

/// <summary>
/// One X connection. Every Xlib call takes <see cref="gate"/>. MIT-SHM images stay alive across frames.
/// Missing RandR, Damage, SHM, XTEST or XFixes degrades that feature and does not abort the process.
/// </summary>
internal sealed class X11Connection : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<uint, ShmBuffer> buffers = [];
    private readonly Dictionary<uint, bool> dirty = [];
    private readonly List<X11Monitor> monitors = [];
    private IntPtr display;
    private nuint root;
    private nuint damage;
    private int screen;
    private int damageEvent;
    private int fd;
    private bool hasRandR, hasShm, hasDamage, damageSupported, hasXTest, hasFixes, shmBroken, disposed;
    private int rootWidth, rootHeight;

    internal IntPtr HandleForTests => display;
    internal bool HasXTest { get { lock (gate) return hasXTest; } }
    internal bool HasDamage { get { lock (gate) return hasDamage; } }
    internal bool DamageSupported { get { lock (gate) return damageSupported; } }
    internal bool DamageSubscribed { get { lock (gate) return damage != 0; } }
    internal int ShmBufferCount { get { lock (gate) return buffers.Count; } }
    internal bool HasShm { get { lock (gate) return hasShm && !shmBroken; } }
    internal int Screen => screen;

    internal static X11Connection Open(string? name)
    {
        X11Native.Prepare();
        var display = X11Native.Open(name);
        if (display == IntPtr.Zero)
            throw new InvalidOperationException("无法连接 X11 显示器。请确认 DISPLAY 已设置且 X 服务正在运行。");
        X11Native.Own(display);
        try { return new X11Connection(display); }
        catch
        {
            X11Native.Release(display);
            X11Native.XCloseDisplay(display);
            throw;
        }
    }

    private X11Connection(IntPtr display)
    {
        this.display = display;
        screen = X11Native.XDefaultScreen(display);
        root = X11Native.XRootWindow(display, screen);
        rootWidth = X11Native.XDisplayWidth(display, screen);
        rootHeight = X11Native.XDisplayHeight(display, screen);
        fd = X11Native.XConnectionNumber(display);
        if (rootWidth is < 1 or > 16384 || rootHeight is < 1 or > 16384)
            throw new FormatException("X11 根窗口尺寸无效。");
        ProbeExtensions();
        // XDamage stays down until a viewer arms capture. Monitor geometry does not need it.
        ReloadMonitors();
    }

    internal void SubscribeDamage()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (damage != 0 || !hasDamage) return;
            damage = X11Native.XDamageCreate(display, root, 1);
            if (damage == 0 || X11Native.TakeError(display) != 0)
            {
                damage = 0;
                hasDamage = false;
                return;
            }
            X11Native.XDamageSubtract(display, damage, 0, 0);
            foreach (var monitor in monitors) dirty[monitor.Id] = true;
        }
    }

    internal void ReleaseCaptureResources()
    {
        lock (gate)
        {
            if (disposed) return;
            if (damage != 0)
            {
                X11Native.XDamageDestroy(display, damage);
                X11Native.TakeError(display);
                damage = 0;
            }
            foreach (var buffer in buffers.Values) buffer.Dispose(display);
            buffers.Clear();
        }
    }

    internal IReadOnlyList<X11Monitor> Monitors(bool refresh = false)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (refresh || monitors.Count == 0) ReloadMonitors();
            return monitors.ToArray();
        }
    }

    internal byte[] Grab(X11Monitor monitor, bool includeCursor)
    {
        lock (gate) return GrabUnlocked(monitor, includeCursor);
    }

    internal (string? Output, int? Workspace) WallpaperHint()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            string? output = null;
            if (hasRandR)
            {
                nuint primary = 0;
                try { primary = X11Native.XRRGetOutputPrimary(display, root); }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
                if (primary != 0 && primary <= uint.MaxValue)
                    output = monitors.FirstOrDefault(item => item.Id == (uint)primary)?.Name;
            }
            return (output, CurrentWorkspace());
        }
    }

    private byte[] GrabUnlocked(X11Monitor monitor, bool includeCursor)
    {
        ThrowIfDisposed();
        var pixels = hasShm && !shmBroken ? GrabShared(monitor) : GrabDirect(monitor);
        if (includeCursor && hasFixes) TryOverlayCursor(pixels, monitor);
        return pixels;
    }

    private int? CurrentWorkspace()
    {
        var atom = X11Native.XInternAtom(display, "_NET_CURRENT_DESKTOP", 1);
        if (atom == 0) return null;
        if (X11Native.XGetWindowProperty(display, root, atom, 0, 1, 0, 6, out _, out var format, out var count, out _, out var data) != 0)
            return null;
        try
        {
            if (data == IntPtr.Zero || format != 32 || count < 1) return null;
            var workspace = Marshal.ReadInt64(data);
            return workspace is >= 0 and <= 64 ? (int)workspace : null;
        }
        finally { if (data != IntPtr.Zero) X11Native.XFree(data); }
    }

    internal bool ConsumeDamage(uint output)
    {
        lock (gate)
        {
            if (disposed || !hasDamage || damage == 0) return true;
            DrainEvents();
            var changed = dirty.TryGetValue(output, out var flag) && flag;
            dirty[output] = false;
            return changed;
        }
    }

    internal async Task<bool> WaitForEventAsync(TimeSpan timeout, CancellationToken ct)
    {
        var ms = (int)Math.Clamp(timeout.TotalMilliseconds, 1, 50);
        var descriptor = fd;
        ct.ThrowIfCancellationRequested();
        var ready = await Task.Run(() =>
        {
            var poll = new X11Native.PollFd { Fd = descriptor, Events = X11Native.PollIn };
            return X11Native.poll(ref poll, 1, ms);
        }, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (disposed) return false;
            var before = dirty.Values.Count(value => value);
            DrainEvents();
            return ready >= 0 && dirty.Values.Count(value => value) > before;
        }
    }

    internal void Move(int x, int y)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            RequireXTest();
            if (X11Native.XTestFakeMotionEvent(display, screen, x, y, 0) == 0)
                throw new IOException("XTEST 指针移动失败。");
            X11Native.XFlush(display);
        }
    }

    internal void Button(int button, bool pressed)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            RequireXTest();
            if (button is < 1 or > 9) throw new ArgumentOutOfRangeException(nameof(button));
            if (X11Native.XTestFakeButtonEvent(display, (uint)button, pressed ? 1 : 0, 0) == 0)
                throw new IOException("XTEST 按键失败。");
            X11Native.XFlush(display);
        }
    }

    internal void Key(int keycode, bool pressed)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            RequireXTest();
            if (keycode is < 8 or > 255) return;
            if (X11Native.XTestFakeKeyEvent(display, (uint)keycode, pressed ? 1 : 0, 0) == 0)
                throw new IOException("XTEST 键盘事件失败。");
            X11Native.XFlush(display);
        }
    }

    internal (int X, int Y) Pointer()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (X11Native.XQueryPointer(display, root, out _, out _, out var x, out var y, out _, out _, out _) == 0)
                throw new IOException("无法读取 X11 指针位置。");
            return (x, y);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var buffer in buffers.Values) buffer.Dispose(display);
            buffers.Clear();
            if (damage != 0) X11Native.XDamageDestroy(display, damage);
            damage = 0;
            var closing = display;
            display = IntPtr.Zero;
            if (closing != IntPtr.Zero)
            {
                X11Native.XCloseDisplay(closing);
                X11Native.Release(closing);
            }
        }
    }

    private void ProbeExtensions()
    {
        hasShm = X11Native.HasShm && Query(() => X11Native.XShmQueryExtension(display) != 0);
        hasRandR = X11Native.HasRandR && Query(() => X11Native.XRRQueryExtension(display, out _, out _) != 0);
        hasDamage = damageSupported = X11Native.HasDamage && Query(() =>
        {
            if (X11Native.XDamageQueryExtension(display, out var damageBase, out _) == 0) return false;
            damageEvent = damageBase;
            return true;
        });
        hasXTest = X11Native.HasXTest && Query(() => X11Native.XTestQueryExtension(display, out _, out _, out _, out _) != 0);
        hasFixes = X11Native.HasFixes && Query(() => X11Native.XFixesQueryExtension(display, out _, out _) != 0);
        X11Native.TakeError(display);
    }

    private static bool Query(Func<bool> probe)
    {
        try { return probe(); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    private void ReloadMonitors()
    {
        var next = hasRandR ? ReadRandR() : [];
        if (next.Count == 0) next.Add(RootMonitor());
        monitors.Clear();
        monitors.AddRange(next);
        foreach (var monitor in monitors)
            if (!dirty.ContainsKey(monitor.Id)) dirty[monitor.Id] = true;
    }

    private List<X11Monitor> ReadRandR()
    {
        var resources = X11Native.XRRGetScreenResourcesCurrent(display, root);
        if (resources == IntPtr.Zero) resources = X11Native.XRRGetScreenResources(display, root);
        if (resources == IntPtr.Zero || X11Native.TakeError(display) != 0) return [];
        try
        {
            var count = Marshal.ReadInt32(resources, 32);
            var outputs = Marshal.ReadIntPtr(resources, 40);
            var modeCount = Marshal.ReadInt32(resources, 48);
            var modes = Marshal.ReadIntPtr(resources, 56);
            if (count is < 0 or > 32 || modeCount is < 0 or > 256 || (count > 0 && outputs == IntPtr.Zero))
                return [];
            var refresh = new Dictionary<ulong, int>();
            for (var i = 0; i < modeCount && modes != IntPtr.Zero; i++)
            {
                var mode = IntPtr.Add(modes, i * 80);
                var id = unchecked((ulong)Marshal.ReadInt64(mode));
                var hTotal = unchecked((uint)Marshal.ReadInt32(mode, 32));
                var vTotal = unchecked((uint)Marshal.ReadInt32(mode, 48));
                var flags = unchecked((uint)Marshal.ReadInt32(mode, 72));
                var clock = unchecked((ulong)Marshal.ReadInt64(mode, 16));
                refresh[id] = X11Model.MilliHz(clock, hTotal, vTotal, flags);
            }
            var list = new List<X11Monitor>();
            for (var i = 0; i < count; i++)
            {
                var xid = unchecked((ulong)Marshal.ReadInt64(outputs, i * 8));
                if (xid == 0 || xid > uint.MaxValue) continue;
                var info = X11Native.XRRGetOutputInfo(display, resources, (nuint)xid);
                if (info == IntPtr.Zero) continue;
                try
                {
                    if (Marshal.ReadInt32(info, 48) != 0) continue; // RR_Connected == 0
                    var crtc = unchecked((ulong)Marshal.ReadInt64(info, 8));
                    if (crtc == 0) continue;
                    var crtcInfo = X11Native.XRRGetCrtcInfo(display, resources, (nuint)crtc);
                    if (crtcInfo == IntPtr.Zero) continue;
                    try
                    {
                        var x = Marshal.ReadInt32(crtcInfo, 8);
                        var y = Marshal.ReadInt32(crtcInfo, 12);
                        var width = Marshal.ReadInt32(crtcInfo, 16);
                        var height = Marshal.ReadInt32(crtcInfo, 20);
                        var mode = unchecked((ulong)Marshal.ReadInt64(crtcInfo, 24));
                        var visible = X11Model.Visible(x, y, width, height, rootWidth, rootHeight);
                        if (visible is not { } box) continue;
                        var name = ReadName(Marshal.ReadIntPtr(info, 16), Marshal.ReadInt32(info, 24));
                        var milli = refresh.TryGetValue(mode, out var rate) ? rate : 60_000;
                        list.Add(new((uint)xid, box.X, box.Y, box.Width, box.Height, milli, name));
                    }
                    finally { X11Native.XRRFreeCrtcInfo(crtcInfo); }
                }
                finally { X11Native.XRRFreeOutputInfo(info); }
            }
            X11Native.TakeError(display);
            return list.OrderBy(item => item.Y).ThenBy(item => item.X).ThenBy(item => item.Id).Take(8).ToList();
        }
        finally { X11Native.XRRFreeScreenResources(resources); }
    }

    private X11Monitor RootMonitor() => new(X11Model.RootOutputId, 0, 0, rootWidth, rootHeight, 60_000, "default");

    private static string ReadName(IntPtr text, int length)
    {
        if (text == IntPtr.Zero || length is < 1 or > 64) return "screen";
        var bytes = new byte[length];
        Marshal.Copy(text, bytes, 0, length);
        var end = Array.IndexOf(bytes, (byte)0);
        if (end >= 0) length = end;
        if (length == 0) return "screen";
        string name;
        try { name = new UTF8Encoding(false, true).GetString(bytes, 0, length); }
        catch (DecoderFallbackException) { return "screen"; }
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl)) return "screen";
        return name;
    }

    private byte[] GrabShared(X11Monitor monitor)
    {
        try
        {
            var buffer = BufferFor(monitor);
            if (X11Native.XShmGetImage(display, root, buffer.Image, monitor.X, monitor.Y, nuint.MaxValue) == 0
                || X11Native.TakeError(display) != 0)
                throw new IOException("XShmGetImage 失败。");
            return CopyImage(buffer.Image);
        }
        catch (Exception e) when (e is IOException or FormatException or EntryPointNotFoundException)
        {
            shmBroken = true;
            return GrabDirect(monitor);
        }
    }

    private byte[] GrabDirect(X11Monitor monitor)
    {
        var image = X11Native.XGetImage(display, root, monitor.X, monitor.Y, (uint)monitor.Width, (uint)monitor.Height, nuint.MaxValue, X11Native.ZPixmap);
        if (image == IntPtr.Zero || X11Native.TakeError(display) != 0)
            throw new IOException("XGetImage 失败。");
        try { return CopyImage(image); }
        finally { DestroyImage(image); }
    }

    private ShmBuffer BufferFor(X11Monitor monitor)
    {
        if (buffers.TryGetValue(monitor.Id, out var existing) && existing.Width == monitor.Width && existing.Height == monitor.Height)
            return existing;
        if (existing is not null) { existing.Dispose(display); buffers.Remove(monitor.Id); }
        var created = ShmBuffer.Create(display, screen, monitor.Width, monitor.Height);
        buffers[monitor.Id] = created;
        return created;
    }

    private static byte[] CopyImage(IntPtr image)
    {
        var width = Marshal.ReadInt32(image);
        var height = Marshal.ReadInt32(image, 4);
        var data = Marshal.ReadIntPtr(image, 16);
        var order = Marshal.ReadInt32(image, 24);
        var stride = Marshal.ReadInt32(image, 44);
        var bits = Marshal.ReadInt32(image, 48);
        var red = unchecked((ulong)Marshal.ReadInt64(image, 56));
        var green = unchecked((ulong)Marshal.ReadInt64(image, 64));
        var blue = unchecked((ulong)Marshal.ReadInt64(image, 72));
        if (data == IntPtr.Zero || stride <= 0 || height <= 0) throw new FormatException("X11 图像为空。");
        var bytes = (long)stride * height;
        if (bytes is <= 0 or > 268435456) throw new FormatException("X11 图像超出限制。");
        var source = new byte[bytes];
        Marshal.Copy(data, source, 0, (int)bytes);
        try { return X11Pixels.CopyBgr0(source, width, height, stride, bits, order, red, green, blue); }
        finally { Array.Clear(source); }
    }

    private void TryOverlayCursor(byte[] pixels, X11Monitor monitor)
    {
        IntPtr cursor;
        try { cursor = X11Native.XFixesGetCursorImage(display); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { hasFixes = false; return; }
        if (cursor == IntPtr.Zero) return;
        try
        {
            var x = Marshal.ReadInt16(cursor);
            var y = Marshal.ReadInt16(cursor, 2);
            var width = (ushort)Marshal.ReadInt16(cursor, 4);
            var height = (ushort)Marshal.ReadInt16(cursor, 6);
            var hotX = (ushort)Marshal.ReadInt16(cursor, 8);
            var hotY = (ushort)Marshal.ReadInt16(cursor, 10);
            var pixelPtr = Marshal.ReadIntPtr(cursor, 24);
            if (pixelPtr == IntPtr.Zero || width == 0 || height == 0 || width > 256 || height > 256) return;
            var count = width * height;
            var argb = new uint[count];
            for (var i = 0; i < count; i++)
                argb[i] = unchecked((uint)Marshal.ReadInt64(pixelPtr, i * 8));
            X11Pixels.OverlayCursor(pixels, monitor.Width, monitor.Height, x - monitor.X, y - monitor.Y, width, height, hotX, hotY, argb);
        }
        finally { X11Native.XFree(cursor); }
    }

    private void DrainEvents()
    {
        if (display == IntPtr.Zero) return;
        var buffer = Marshal.AllocHGlobal(192);
        try
        {
            var sawDamage = false;
            while (X11Native.XPending(display) > 0)
            {
                X11Native.XNextEvent(display, buffer);
                if (!hasDamage || Marshal.ReadInt32(buffer) != damageEvent) continue;
                sawDamage = true;
                var x = Marshal.ReadInt16(buffer, 64);
                var y = Marshal.ReadInt16(buffer, 66);
                var width = (ushort)Marshal.ReadInt16(buffer, 68);
                var height = (ushort)Marshal.ReadInt16(buffer, 70);
                if (width == 0 || height == 0)
                {
                    foreach (var monitor in monitors) dirty[monitor.Id] = true;
                    continue;
                }
                foreach (var monitor in monitors)
                    if (X11Model.Intersects(x, y, width, height, monitor.X, monitor.Y, monitor.Width, monitor.Height))
                        dirty[monitor.Id] = true;
            }
            if (sawDamage && damage != 0) X11Native.XDamageSubtract(display, damage, 0, 0);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private void RequireXTest()
    {
        if (!hasXTest) throw new NotSupportedException("X11 缺少 XTEST 扩展，无法注入键鼠。");
    }

    private void ThrowIfDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(X11Connection));
    }

    private static void DestroyImage(IntPtr image)
    {
        if (image == IntPtr.Zero) return;
        var destroy = Marshal.ReadIntPtr(image, 96);
        Marshal.WriteIntPtr(image, 16, IntPtr.Zero);
        if (destroy != IntPtr.Zero)
            Marshal.GetDelegateForFunctionPointer<X11Native.DestroyImage>(destroy)(image);
    }

    private sealed class ShmBuffer
    {
        internal IntPtr Image;
        internal IntPtr Info;
        internal int SharedId = -1;
        internal IntPtr Address;
        internal int Width, Height;
        private bool attached;

        internal static ShmBuffer Create(IntPtr display, int screen, int width, int height)
        {
            var info = Marshal.AllocHGlobal(32);
            for (var i = 0; i < 32; i++) Marshal.WriteByte(info, i, 0);
            var depth = (uint)X11Native.XDefaultDepth(display, screen);
            var visual = X11Native.XDefaultVisual(display, screen);
            var image = X11Native.XShmCreateImage(display, visual, depth, X11Native.ZPixmap, IntPtr.Zero, info, (uint)width, (uint)height);
            if (image == IntPtr.Zero) { Marshal.FreeHGlobal(info); throw new IOException("无法创建 XShm 图像。"); }
            var buffer = new ShmBuffer { Image = image, Info = info, Width = width, Height = height };
            try
            {
                var stride = Marshal.ReadInt32(image, 44);
                var lines = Marshal.ReadInt32(image, 4);
                var bytes = (long)stride * lines;
                if (stride <= 0 || lines != height || bytes is <= 0 or > 268435456)
                    throw new FormatException("XShm 图像尺寸无效。");
                var id = X11Native.shmget(0, (nuint)bytes, X11Native.IpcCreat | 0x180); // 0600
                if (id < 0) throw new IOException("无法创建共享内存：" + Marshal.GetLastPInvokeError());
                buffer.SharedId = id;
                var address = X11Native.shmat(id, IntPtr.Zero, 0);
                if (address == new IntPtr(-1)) throw new IOException("无法附加共享内存：" + Marshal.GetLastPInvokeError());
                buffer.Address = address;
                Marshal.WriteInt32(info, 8, id);
                Marshal.WriteIntPtr(info, 16, address);
                Marshal.WriteIntPtr(image, 16, address);
                if (X11Native.XShmAttach(display, info) == 0 || X11Native.TakeError(display) != 0)
                    throw new IOException("X 服务器拒绝了 MIT-SHM。");
                buffer.attached = true;
                X11Native.shmctl(id, X11Native.IpcRmid, IntPtr.Zero);
                return buffer;
            }
            catch
            {
                buffer.Dispose(display);
                throw;
            }
        }

        internal void Dispose(IntPtr display)
        {
            if (attached && display != IntPtr.Zero) X11Native.XShmDetach(display, Info);
            attached = false;
            if (Image != IntPtr.Zero)
            {
                DestroyImage(Image);
                Image = IntPtr.Zero;
            }
            if (Address != IntPtr.Zero && Address != new IntPtr(-1)) X11Native.shmdt(Address);
            Address = IntPtr.Zero;
            if (SharedId >= 0) X11Native.shmctl(SharedId, X11Native.IpcRmid, IntPtr.Zero);
            SharedId = -1;
            if (Info != IntPtr.Zero) Marshal.FreeHGlobal(Info);
            Info = IntPtr.Zero;
        }
    }
}
