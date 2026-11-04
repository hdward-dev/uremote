using System.Runtime.InteropServices;

namespace URemote.Linux;

internal static class X11Native
{
    internal const int ZPixmap = 2;
    internal const int PropModeReplace = 0;
    internal const int IpcCreat = 512;
    internal const int IpcRmid = 0;
    internal const int PollIn = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        internal int Fd;
        internal short Events;
        internal short Revents;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ErrorHandler(IntPtr display, IntPtr error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int IoHandler(IntPtr display);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int DestroyImage(IntPtr image);

    private static readonly ErrorHandler ErrorDelegate = OnError;
    private static readonly IoHandler IoDelegate = OnIo;
    private static readonly object handlerGate = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, int> errors = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, byte> owned = new();
    private static IntPtr previousError, previousIo;
    private static int handlersInstalled;

    internal static bool HasRandR { get; private set; }
    internal static bool HasShm { get; private set; }
    internal static bool HasDamage { get; private set; }
    internal static bool HasXTest { get; private set; }
    internal static bool HasFixes { get; private set; }

    internal static void Prepare()
    {
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("X11 后端需要 64 位 Linux。");
        if (!NativeLibrary.TryLoad("libX11.so.6", out _))
            throw new NotSupportedException("缺少 libX11。X11 被控无法启动。");
        HasRandR = NativeLibrary.TryLoad("libXrandr.so.2", out _);
        HasShm = NativeLibrary.TryLoad("libXext.so.6", out _);
        HasDamage = NativeLibrary.TryLoad("libXdamage.so.1", out _);
        HasXTest = NativeLibrary.TryLoad("libXtst.so.6", out _);
        HasFixes = NativeLibrary.TryLoad("libXfixes.so.3", out _);
        XInitThreads();
        if (Interlocked.Exchange(ref handlersInstalled, 1) == 0)
        {
            lock (handlerGate)
            {
                previousError = XSetErrorHandler(ErrorDelegate);
                previousIo = XSetIOErrorHandler(IoDelegate);
            }
        }
    }

    internal static void Own(IntPtr display) => owned[display] = 1;
    internal static void Release(IntPtr display) { owned.TryRemove(display, out _); errors.TryRemove(display, out _); }

    internal static int TakeError(IntPtr display)
    {
        XSync(display, 0);
        return errors.TryRemove(display, out var code) ? code : 0;
    }

    private static int OnError(IntPtr display, IntPtr error)
    {
        if (owned.ContainsKey(display))
        {
            errors[display] = Marshal.ReadByte(error, 32);
            return 0;
        }
        if (previousError != IntPtr.Zero)
            return Marshal.GetDelegateForFunctionPointer<ErrorHandler>(previousError)(display, error);
        return 0;
    }

    private static int OnIo(IntPtr display)
    {
        if (owned.ContainsKey(display))
            throw new IOException("X11 连接已断开。");
        if (previousIo != IntPtr.Zero)
            return Marshal.GetDelegateForFunctionPointer<IoHandler>(previousIo)(display);
        throw new IOException("X11 连接已断开。");
    }

    [DllImport("libX11.so.6")] internal static extern int XInitThreads();
    [DllImport("libX11.so.6")] internal static extern IntPtr XSetErrorHandler(ErrorHandler handler);
    [DllImport("libX11.so.6")] internal static extern IntPtr XSetIOErrorHandler(IoHandler handler);
    /// <summary>
    /// Opens <paramref name="name"/>, or the managed DISPLAY value when <paramref name="name"/> is null.
    /// The managed value is passed explicitly so a .NET host and this connection agree even when
    /// <see cref="Environment.SetEnvironmentVariable"/> has not updated native getenv.
    /// </summary>
    internal static IntPtr Open(string? name)
    {
        var chosen = name ?? Environment.GetEnvironmentVariable("DISPLAY");
        if (string.IsNullOrWhiteSpace(chosen)) return IntPtr.Zero;
        return XOpenDisplay(chosen);
    }

    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)] private static extern IntPtr XOpenDisplay(string name);
    [DllImport("libX11.so.6")] internal static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] internal static extern int XDefaultScreen(IntPtr display);
    [DllImport("libX11.so.6")] internal static extern nuint XRootWindow(IntPtr display, int screen);
    [DllImport("libX11.so.6")] internal static extern int XDisplayWidth(IntPtr display, int screen);
    [DllImport("libX11.so.6")] internal static extern int XDisplayHeight(IntPtr display, int screen);
    [DllImport("libX11.so.6")] internal static extern int XDefaultDepth(IntPtr display, int screen);
    [DllImport("libX11.so.6")] internal static extern IntPtr XDefaultVisual(IntPtr display, int screen);
    [DllImport("libX11.so.6")] internal static extern int XConnectionNumber(IntPtr display);
    [DllImport("libX11.so.6")] internal static extern int XFlush(IntPtr display);
    [DllImport("libX11.so.6")] internal static extern int XSync(IntPtr display, int discard);
    [DllImport("libX11.so.6")] internal static extern int XPending(IntPtr display);
    [DllImport("libX11.so.6")] internal static extern int XNextEvent(IntPtr display, IntPtr eventBuffer);
    [DllImport("libX11.so.6")] internal static extern int XFree(IntPtr data);
    [DllImport("libX11.so.6")] internal static extern nuint XCreateSimpleWindow(IntPtr display, nuint parent, int x, int y, uint width, uint height, uint border, nuint borderPixel, nuint background);
    [DllImport("libX11.so.6")] internal static extern int XMapWindow(IntPtr display, nuint window);
    [DllImport("libX11.so.6")] internal static extern int XDestroyWindow(IntPtr display, nuint window);
    [DllImport("libX11.so.6")] internal static extern int XMoveWindow(IntPtr display, nuint window, int x, int y);
    [DllImport("libX11.so.6")] internal static extern IntPtr XGetImage(IntPtr display, nuint drawable, int x, int y, uint width, uint height, nuint plane, int format);
    [DllImport("libX11.so.6", CharSet = CharSet.Ansi)] internal static extern nuint XInternAtom(IntPtr display, string name, int onlyIfExists);
    [DllImport("libX11.so.6")] internal static extern int XSetSelectionOwner(IntPtr display, nuint selection, nuint owner, nuint time);
    [DllImport("libX11.so.6")] internal static extern nuint XGetSelectionOwner(IntPtr display, nuint selection);
    [DllImport("libX11.so.6")] internal static extern int XConvertSelection(IntPtr display, nuint selection, nuint target, nuint property, nuint requestor, nuint time);
    [DllImport("libX11.so.6")] internal static extern int XSendEvent(IntPtr display, nuint window, int propagate, nint eventMask, IntPtr eventBuffer);
    [DllImport("libX11.so.6")] internal static extern int XChangeProperty(IntPtr display, nuint window, nuint property, nuint type, int format, int mode, byte[] data, int nelements);
    [DllImport("libX11.so.6")] internal static extern int XGetWindowProperty(IntPtr display, nuint window, nuint property, nint offset, nint length, int delete, nuint reqType, out nuint actualType, out int actualFormat, out nuint nitems, out nuint bytesAfter, out IntPtr prop);
    [DllImport("libX11.so.6")] internal static extern int XSelectInput(IntPtr display, nuint window, nint mask);
    [DllImport("libX11.so.6")] internal static extern int XSetInputFocus(IntPtr display, nuint focus, int revert, nuint time);
    [DllImport("libX11.so.6")] internal static extern int XQueryPointer(IntPtr display, nuint window, out nuint root, out nuint child, out int rootX, out int rootY, out int winX, out int winY, out uint mask);
    [DllImport("libX11.so.6")] internal static extern int XStoreName(IntPtr display, nuint window, string name);
    [DllImport("libX11.so.6")] internal static extern int XClearWindow(IntPtr display, nuint window);
    [DllImport("libX11.so.6")] internal static extern int XSetWindowBackground(IntPtr display, nuint window, nuint pixel);

    [DllImport("libXext.so.6")] internal static extern int XShmQueryExtension(IntPtr display);
    [DllImport("libXext.so.6")] internal static extern IntPtr XShmCreateImage(IntPtr display, IntPtr visual, uint depth, int format, IntPtr data, IntPtr shmInfo, uint width, uint height);
    [DllImport("libXext.so.6")] internal static extern int XShmAttach(IntPtr display, IntPtr shmInfo);
    [DllImport("libXext.so.6")] internal static extern int XShmDetach(IntPtr display, IntPtr shmInfo);
    [DllImport("libXext.so.6")] internal static extern int XShmGetImage(IntPtr display, nuint drawable, IntPtr image, int x, int y, nuint plane);

    [DllImport("libXrandr.so.2")] internal static extern int XRRQueryExtension(IntPtr display, out int eventBase, out int errorBase);
    [DllImport("libXrandr.so.2")] internal static extern IntPtr XRRGetScreenResourcesCurrent(IntPtr display, nuint window);
    [DllImport("libXrandr.so.2")] internal static extern IntPtr XRRGetScreenResources(IntPtr display, nuint window);
    [DllImport("libXrandr.so.2")] internal static extern void XRRFreeScreenResources(IntPtr resources);
    [DllImport("libXrandr.so.2")] internal static extern IntPtr XRRGetOutputInfo(IntPtr display, IntPtr resources, nuint output);
    [DllImport("libXrandr.so.2")] internal static extern void XRRFreeOutputInfo(IntPtr info);
    [DllImport("libXrandr.so.2")] internal static extern nuint XRRGetOutputPrimary(IntPtr display, nuint window);
    [DllImport("libXrandr.so.2")] internal static extern IntPtr XRRGetCrtcInfo(IntPtr display, IntPtr resources, nuint crtc);
    [DllImport("libXrandr.so.2")] internal static extern void XRRFreeCrtcInfo(IntPtr info);

    [DllImport("libXdamage.so.1")] internal static extern int XDamageQueryExtension(IntPtr display, out int eventBase, out int errorBase);
    [DllImport("libXdamage.so.1")] internal static extern nuint XDamageCreate(IntPtr display, nuint drawable, int level);
    [DllImport("libXdamage.so.1")] internal static extern void XDamageDestroy(IntPtr display, nuint damage);
    [DllImport("libXdamage.so.1")] internal static extern void XDamageSubtract(IntPtr display, nuint damage, nuint repair, nuint parts);

    [DllImport("libXfixes.so.3")] internal static extern int XFixesQueryExtension(IntPtr display, out int eventBase, out int errorBase);
    [DllImport("libXfixes.so.3")] internal static extern IntPtr XFixesGetCursorImage(IntPtr display);

    [DllImport("libXtst.so.6")] internal static extern int XTestQueryExtension(IntPtr display, out int eventBase, out int errorBase, out int major, out int minor);
    [DllImport("libXtst.so.6")] internal static extern int XTestFakeMotionEvent(IntPtr display, int screenNumber, int x, int y, nuint delay);
    [DllImport("libXtst.so.6")] internal static extern int XTestFakeButtonEvent(IntPtr display, uint button, int pressed, nuint delay);
    [DllImport("libXtst.so.6")] internal static extern int XTestFakeKeyEvent(IntPtr display, uint keycode, int pressed, nuint delay);

    [DllImport("libc", SetLastError = true)] internal static extern int shmget(int key, nuint size, int flags);
    [DllImport("libc", SetLastError = true)] internal static extern IntPtr shmat(int id, IntPtr address, int flags);
    [DllImport("libc", SetLastError = true)] internal static extern int shmdt(IntPtr address);
    [DllImport("libc", SetLastError = true)] internal static extern int shmctl(int id, int command, IntPtr buffer);
    [DllImport("libc")] internal static extern int poll(ref PollFd fd, nuint count, int timeout);
}
