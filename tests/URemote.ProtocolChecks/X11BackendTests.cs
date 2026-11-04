using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using URemote.Core;
using URemote.Linux;

static class X11BackendTests
{
    public static void Run(Action<bool, string> check)
    {
        check(LinuxSessionKind.Detect(Read(("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0"), ("XDG_SESSION_TYPE", "x11"))) == LinuxSessionKind.Desktop.Wayland,
            "Wayland display keeps the portal backend even when DISPLAY is set");
        check(LinuxSessionKind.Detect(Read(("XDG_SESSION_TYPE", "wayland"), ("DISPLAY", ":0"))) == LinuxSessionKind.Desktop.Wayland,
            "XDG_SESSION_TYPE=wayland selects Wayland without WAYLAND_DISPLAY");
        check(LinuxSessionKind.Detect(Read(("DISPLAY", ":5"))) == LinuxSessionKind.Desktop.X11,
            "DISPLAY alone selects X11");
        check(LinuxSessionKind.Detect(Read(("XDG_SESSION_TYPE", "x11"), ("DISPLAY", ":0"))) == LinuxSessionKind.Desktop.X11,
            "explicit x11 session with DISPLAY selects X11");
        check(LinuxSessionKind.Detect(Read(("DISPLAY", "  "), ("WAYLAND_DISPLAY", ""))) == LinuxSessionKind.Desktop.None,
            "blank display variables select neither backend");

        check(X11Model.MilliHz(0, 2200, 1125, 0) == 60_000, "Xvfb zero dot clock defaults to 60 Hz");
        check(X11Model.MilliHz(148_500_000, 2200, 1125, 0) == 60_000, "148.5 MHz 2200x1125 mode is 60 Hz");
        check(X11FramePace.RefreshHz(0) == 60 && X11FramePace.RefreshHz(59_940) == 60, "millihertz rounds onto the capture clock");
        check(X11FramePace.TargetFps(60, 30) == 30 && X11FramePace.TargetFps(60, 144) == 60 && X11FramePace.TargetFps(144, 60) == 60,
            "target fps is the minimum of refresh and request");
        check(X11FramePace.TargetFps(0, 0) == 60, "missing refresh and request fall back to 60");
        var target = X11FramePace.Interval(30);
        check(X11FramePace.Delay(TimeSpan.Zero, true, 30) == target, "a changed frame waits the full target period when called immediately");
        check(X11FramePace.Delay(target, true, 30) == TimeSpan.Zero, "a changed frame is due once the target period elapsed");
        check(X11FramePace.Delay(target + TimeSpan.FromMilliseconds(5), true, 30) == TimeSpan.Zero, "a late changed frame is not delayed further");
        var idle = X11FramePace.Delay(target, false, 30);
        check(idle > TimeSpan.Zero && idle > X11FramePace.Delay(target, true, 30), "static content waits longer than a changed frame");
        check(X11FramePace.Delay(TimeSpan.FromSeconds(1), false, 30) == TimeSpan.Zero, "static content is delivered once the floor interval elapsed");
        check(X11FramePace.Delay(TimeSpan.FromMilliseconds(-5), true, 30) == target, "a negative elapsed time cannot burst past the target");

        var bgrx = X11Pixels.CopyBgr0(new byte[] { 10, 20, 30, 40, 1, 2, 3, 4 }, 2, 1, 8, 32, 0, 0x00FF0000, 0x0000FF00, 0x000000FF);
        check(bgrx.SequenceEqual(new byte[] { 10, 20, 30, 0, 1, 2, 3, 0 }), "little-endian BGRX keeps channels and clears padding");
        var rgbx = X11Pixels.CopyBgr0(new byte[] { 1, 2, 3, 4 }, 1, 1, 4, 32, 0, 0x000000FF, 0x0000FF00, 0x00FF0000);
        check(rgbx.SequenceEqual(new byte[] { 3, 2, 1, 0 }), "RGBX masks convert to BGR0");
        var cursor = new byte[16];
        X11Pixels.OverlayCursor(cursor, 2, 2, 1, 1, 1, 1, 0, 0, new uint[] { 0xFF112233 });
        check(cursor[0] == 0 && cursor[12] == 0x33 && cursor[13] == 0x22 && cursor[14] == 0x11, "cursor overlay writes BGR at the hotspot");

        check(X11Model.Button(1) == 1 && X11Model.Button(2) == 3 && X11Model.Button(4) == 2 && X11Model.Button(8) == 8 && X11Model.Button(16) == 9,
            "UU buttons map to X11 button numbers");
        check(X11Model.WheelButtons(0, 1).SequenceEqual(new[] { 4 }) && X11Model.WheelButtons(0, -2).SequenceEqual(new[] { 5, 5 })
            && X11Model.WheelButtons(1, 0).SequenceEqual(new[] { 7 }), "wheel direction maps to X11 buttons 4/5/7");
        check(X11Model.XKeyCode(30) == 38 && X11Text.TryPlan("A\n", out var plan) && plan.Length == 2 && plan[0].Shift && plan[0].KeyCode == 38 && !plan[1].Shift,
            "evdev keycodes shift by 8 and basic text uses shift");
        check(!X11Text.TryPlan("星", out _), "unmapped text is rejected before any key is sent");
        var second = new X11Monitor(9, 1920, 0, 1280, 1024, 60_000, "HDMI-2");
        var origin = X11Model.RootPoint(second, 0, 0, PointerCoordinates.MacNormalized);
        var far = X11Model.RootPoint(second, 1, 1, PointerCoordinates.MacNormalized);
        check(origin == (1920, 0) && far == (1920 + 1279, 1023), "absolute pointer maps across monitors");
        check(X11Model.Visible(-100, 0, 300, 200, 1920, 1080) == (0, 0, 200, 200), "negative RandR coordinates are clipped to the root");
        check(X11Model.Visible(2000, 0, 100, 100, 1920, 1080) is null, "a monitor outside the root is ignored");
        check(X11Model.Intersects(0, 0, 10, 10, 9, 9, 5, 5) && !X11Model.Intersects(0, 0, 10, 10, 10, 0, 5, 5),
            "damage rectangles use half-open intersection");
    }

    public static async Task LiveAsync(Action<bool, string> check)
    {
        DesktopBackend.ResetCaptureForTests();
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/Xvfb")) return;
        var displayNumber = Enumerable.Range(20, 30).First(number => !File.Exists("/tmp/.X11-unix/X" + number));
        var display = ":" + displayNumber;
        var savedDisplay = Environment.GetEnvironmentVariable("DISPLAY");
        var savedWayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var savedSession = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
        Process? server = null;
        Process? windowManager = null;
        try
        {
            server = Process.Start(new ProcessStartInfo("Xvfb", $"{display} -screen 0 1920x1200x24 -ac")
            { UseShellExecute = false, RedirectStandardError = true });
            var ready = SpinWait.SpinUntil(() => File.Exists("/tmp/.X11-unix/X" + displayNumber), TimeSpan.FromSeconds(5));
            check(ready && server is { HasExited: false }, "Xvfb test display started");
            if (!ready || server is not { HasExited: false }) return;

            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", null);
            Environment.SetEnvironmentVariable("DISPLAY", ":99");
            Exception? missing = null;
            try { await DesktopBackend.DiscoverAsync(); }
            catch (Exception e) { missing = e; }
            check(missing is InvalidOperationException && missing.Message.Contains("X11", StringComparison.Ordinal),
                "X11 discovery fails clearly when DISPLAY has no server");

            Environment.SetEnvironmentVariable("DISPLAY", display);
            X11FrameRateNotes.ResetForTests();
            var globals = await DesktopBackend.DiscoverAsync();
            var output = globals.Single(item => item.Interface == "wl_output").Name;
            check(output != 0, "RandR or the root window is published as wl_output");
            var rates = await DesktopBackend.ReadRefreshRatesAsync(CancellationToken.None);
            check(rates[output] == 60_000, "Xvfb refresh of zero becomes 60 Hz");
            await using var session = await DesktopBackend.OpenAsync(true, CancellationToken.None);
            check(!DesktopBackend.X11DamageSubscribedForTests && DesktopBackend.X11ShmBuffersForTests == 0 && !DesktopBackend.CaptureStreamsOpenForTests,
                "X11 damage and shared-memory capture stay down until a viewer arms capture");
            var idle = await DesktopBackend.CaptureAsync(output, false, CancellationToken.None);
            check(idle.Pending && idle.Width == 1920 && idle.Height == 1200, "an idle X11 session returns geometry without grabbing");
            Array.Clear(idle.Pixels);
            DesktopBackend.SetCaptureGraceForTests(TimeSpan.FromMilliseconds(200));
            var armed = new List<string>();
            await DesktopBackend.ArmForViewerAsync([output], false, armed.Add, CancellationToken.None);
            check(armed.Count == 1 && armed[0] == "capture-armed;reason=viewer-requested;screens=1", "X11 capture arms when a viewer is requested");
            check(!DesktopBackend.X11DamageSupportedForTests || DesktopBackend.X11DamageSubscribedForTests,
                "XDamage is subscribed while capture is armed");
            var first = await DesktopBackend.CaptureAsync(output, true, CancellationToken.None);
            check(first.Width == 1920 && first.Height == 1200 && first.ShmFormat == 1 && first.Pixels.Length == 1920 * 1200 * 4,
                "X11 capture returns tightly packed BGR0");
            Array.Clear(first.Pixels);

            X11FrameRateNotes.Note(output, 30);
            var quiet = await MeasureAsync(output, TimeSpan.FromMilliseconds(1100));
            check(quiet.Fps < 10, $"static X11 capture stays below the 30 fps target ({quiet.Fps:F1} fps)");
            using (var painter = new Painter(display))
            {
                painter.Start();
                var moving = await MeasureAsync(output, TimeSpan.FromMilliseconds(1000));
                check(moving.Fps <= 34, $"changing X11 capture does not exceed 30 fps ({moving.Fps:F1} fps)");
                check(moving.Fps > quiet.Fps && moving.Fps >= 24, $"changing content reaches the target rate ({moving.Fps:F1} fps, quiet {quiet.Fps:F1})");
                var painted = await DesktopBackend.CaptureAsync(output, false, CancellationToken.None);
                try
                {
                    var pixel = (int)painted.Stride * 140 + 120 * 4;
                    check(painted.Pixels[pixel + 2] == 255, "captured BGR0 contains the painted red window");
                }
                finally { Array.Clear(painted.Pixels); }
                painter.Stop();
            }

            DesktopBackend.ReleaseCapture();
            var released = false;
            var releaseWatch = Stopwatch.StartNew();
            while (releaseWatch.Elapsed < TimeSpan.FromSeconds(2))
            {
                if (!DesktopBackend.CaptureStreamsOpenForTests && !DesktopBackend.X11DamageSubscribedForTests && DesktopBackend.X11ShmBuffersForTests == 0)
                { released = true; break; }
                await Task.Delay(20);
            }
            check(released, "XDamage and shared-memory buffers are released after the capture grace period");

            await using var pointer = await DesktopBackend.CreatePointerAsync(output, CancellationToken.None, width: 1920, height: 1200);
            await pointer.ApplyAsync(new HostMouseMessage(MouseAction.MoveAbsolute, 100d / 1920, 80d / 1200), PointerCoordinates.MacNormalized, 1920, 1200);
            await pointer.ApplyAsync(new HostMouseMessage(MouseAction.Press, Button: 1), PointerCoordinates.MacNormalized, 1920, 1200);
            using (var probe = X11Connection.Open(display))
            {
                var point = probe.Pointer();
                check(point == (100, 80), "XTest pointer uses root coordinates across the framebuffer");
            }
            await pointer.ApplyAsync(new HostMouseMessage(MouseAction.Release, Button: 1), PointerCoordinates.MacNormalized, 1920, 1200);
            var keys = new List<string>();
            await using var keyboard = await DesktopBackend.CreateKeyboardAsync(CancellationToken.None, keys.Add);
            check(keys.Contains("keyboard-backend=x11"), "X11 keyboard does not fall through to uinput or Wayland");
            check(await TypedAsync(display, keyboard, 0) == 38, "macOS key 0 is delivered as X keycode 38");
            await DesktopBackend.TypeTextAsync("Ab", CancellationToken.None);

            await using (var clipboard = new X11Clipboard())
            {
                await clipboard.WriteAsync("星栈 X11", CancellationToken.None);
                check(await clipboard.ReadAsync(CancellationToken.None) == "星栈 X11", "X11 clipboard returns its own UTF-8 selection");
                check(ReadSelection(display) == "星栈 X11", "another X client can read the CLIPBOARD selection");
            }
            var locked = await X11LockProbe.TryReadAsync(CancellationToken.None);
            check(true, "lock probe completed: " + (locked switch { true => "locked", false => "unlocked", _ => "unknown" }));

            var windowManagerStart = new ProcessStartInfo("xfwm4", "--compositor=off --sm-client-disable")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            windowManagerStart.Environment["DISPLAY"] = display;
            windowManagerStart.Environment.Remove("WAYLAND_DISPLAY");
            windowManagerStart.Environment.Remove("XDG_SESSION_TYPE");
            windowManager = Process.Start(windowManagerStart);
            await Task.Delay(500);
            var managed = await DesktopBackend.DiscoverAsync();
            check(managed.Any(item => item.Interface == "wl_output"), "discovery still works with xfwm4 compositor off");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DISPLAY", savedDisplay);
            Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", savedWayland);
            Environment.SetEnvironmentVariable("XDG_SESSION_TYPE", savedSession);
            X11FrameRateNotes.ResetForTests();
            DesktopBackend.ResetCaptureForTests();
            Stop(windowManager);
            Stop(server);
            try { if (File.Exists("/tmp/.X11-unix/X" + displayNumber)) File.Delete("/tmp/.X11-unix/X" + displayNumber); }
            catch (IOException) { }
        }
    }

    private static async Task<(int Frames, double Fps)> MeasureAsync(uint output, TimeSpan duration)
    {
        var frames = 0;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < duration)
        {
            var frame = await DesktopBackend.CaptureAsync(output, false, CancellationToken.None);
            Array.Clear(frame.Pixels);
            frames++;
        }
        return (frames, frames / watch.Elapsed.TotalSeconds);
    }

    private static async Task<int> TypedAsync(string display, IDesktopKeyboard keyboard, int macKey)
    {
        using var probe = X11Connection.Open(display);
        var window = Native.CreateKeyWindow(probe);
        await keyboard.ApplyAsync(new HostKeyMessage(KeyAction.Click, macKey), CancellationToken.None);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            if (Native.KeyCode(probe, window) is { } code) return code;
            await Task.Delay(10);
        }
        return -1;
    }

    private static string? ReadSelection(string display)
    {
        using var probe = X11Connection.Open(display);
        return Native.ReadClipboard(probe);
    }

    private static Func<string, string?> Read(params (string Key, string? Value)[] values)
        => key => values.FirstOrDefault(item => item.Key == key).Value;

    private static void Stop(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(true); process.WaitForExit(2000); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process.Dispose();
    }

    private static class Native
    {
        internal static nuint CreateKeyWindow(X11Connection connection)
        {
            var display = Handle(connection);
            var screen = XDefaultScreen(display);
            var window = XCreateSimpleWindow(display, XRootWindow(display, screen), 0, 0, 50, 50, 0, 0, 0);
            XSelectInput(display, window, 1);
            XMapWindow(display, window);
            XSetInputFocus(display, window, 2, 0);
            XFlush(display);
            XSync(display, 0);
            return window;
        }

        internal static int? KeyCode(X11Connection connection, nuint window)
        {
            var display = Handle(connection);
            var buffer = Marshal.AllocHGlobal(192);
            try
            {
                while (XPending(display) > 0)
                {
                    XNextEvent(display, buffer);
                    if (Marshal.ReadInt32(buffer) != 2) continue;
                    if (unchecked((nuint)Marshal.ReadInt64(buffer, 32)) != window) continue;
                    return Marshal.ReadInt32(buffer, 84);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return null;
        }

        internal static string? ReadClipboard(X11Connection connection)
        {
            var display = Handle(connection);
            var screen = XDefaultScreen(display);
            var window = XCreateSimpleWindow(display, XRootWindow(display, screen), 0, 0, 1, 1, 0, 0, 0);
            var clipboard = XInternAtom(display, "CLIPBOARD", 0);
            var utf8 = XInternAtom(display, "UTF8_STRING", 0);
            var property = XInternAtom(display, "UREMOTE_READ", 0);
            XConvertSelection(display, clipboard, utf8, property, window, 0);
            XFlush(display);
            var buffer = Marshal.AllocHGlobal(192);
            var deadline = Stopwatch.StartNew();
            try
            {
                while (deadline.Elapsed < TimeSpan.FromSeconds(2))
                {
                    while (XPending(display) > 0)
                    {
                        XNextEvent(display, buffer);
                        if (Marshal.ReadInt32(buffer) != 31) continue;
                        var propertyId = unchecked((nuint)Marshal.ReadInt64(buffer, 56));
                        if (propertyId == 0) return null;
                        if (XGetWindowProperty(display, window, propertyId, 0, 16384, 1, 0, out _, out var format, out var count, out _, out var data) != 0)
                            return null;
                        try
                        {
                            if (data == IntPtr.Zero || format != 8 || count == 0 || count > 65536) return null;
                            var bytes = new byte[(int)count];
                            Marshal.Copy(data, bytes, 0, bytes.Length);
                            return Encoding.UTF8.GetString(bytes);
                        }
                        finally { if (data != IntPtr.Zero) XFree(data); }
                    }
                    Thread.Sleep(10);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); XDestroyWindow(display, window); }
            return null;
        }

        private static IntPtr Handle(X11Connection connection) => connection.HandleForTests;

        [DllImport("libX11.so.6")] private static extern int XDefaultScreen(IntPtr display);
        [DllImport("libX11.so.6")] private static extern nuint XRootWindow(IntPtr display, int screen);
        [DllImport("libX11.so.6")] private static extern nuint XCreateSimpleWindow(IntPtr display, nuint parent, int x, int y, uint width, uint height, uint border, nuint borderPixel, nuint background);
        [DllImport("libX11.so.6")] private static extern int XSelectInput(IntPtr display, nuint window, nint mask);
        [DllImport("libX11.so.6")] private static extern int XMapWindow(IntPtr display, nuint window);
        [DllImport("libX11.so.6")] private static extern int XSetInputFocus(IntPtr display, nuint focus, int revert, nuint time);
        [DllImport("libX11.so.6")] private static extern int XFlush(IntPtr display);
        [DllImport("libX11.so.6")] private static extern int XSync(IntPtr display, int discard);
        [DllImport("libX11.so.6")] private static extern int XPending(IntPtr display);
        [DllImport("libX11.so.6")] private static extern int XNextEvent(IntPtr display, IntPtr buffer);
        [DllImport("libX11.so.6")] private static extern int XDestroyWindow(IntPtr display, nuint window);
        [DllImport("libX11.so.6", CharSet = CharSet.Ansi)] private static extern nuint XInternAtom(IntPtr display, string name, int onlyIfExists);
        [DllImport("libX11.so.6")] private static extern int XConvertSelection(IntPtr display, nuint selection, nuint target, nuint property, nuint requestor, nuint time);
        [DllImport("libX11.so.6")] private static extern int XGetWindowProperty(IntPtr display, nuint window, nuint property, nint offset, nint length, int delete, nuint reqType, out nuint actualType, out int actualFormat, out nuint nitems, out nuint bytesAfter, out IntPtr prop);
        [DllImport("libX11.so.6")] private static extern int XFree(IntPtr data);
    }

    private sealed class Painter : IDisposable
    {
        private readonly Process? process;
        private readonly CancellationTokenSource stop = new();
        private Task? loop;
        public Painter(string display)
        {
            process = null;
            Display = display;
        }
        private string Display { get; }
        public void Start() => loop = Task.Run(Paint);
        public void Stop() { stop.Cancel(); try { loop?.Wait(1000); } catch (AggregateException) { } }
        public void Dispose() { Stop(); stop.Dispose(); process?.Dispose(); }
        private void Paint()
        {
            var display = XOpen(Display);
            if (display == IntPtr.Zero) return;
            try
            {
                var screen = ScreenOf(display);
                var window = Make(display, screen);
                var color = (nuint)0x00FF0000;
                while (!stop.IsCancellationRequested)
                {
                    Background(display, window, color);
                    Clear(display, window);
                    Flush(display);
                    color ^= 0x00FFFF;
                    Thread.Sleep(5);
                }
            }
            finally { XClose(display); }
        }
        [DllImport("libX11.so.6", CharSet = CharSet.Ansi, EntryPoint = "XOpenDisplay")] private static extern IntPtr XOpen(string name);
        [DllImport("libX11.so.6", EntryPoint = "XCloseDisplay")] private static extern int XClose(IntPtr display);
        [DllImport("libX11.so.6", EntryPoint = "XDefaultScreen")] private static extern int ScreenOf(IntPtr display);
        [DllImport("libX11.so.6", EntryPoint = "XRootWindow")] private static extern nuint Root(IntPtr display, int screen);
        [DllImport("libX11.so.6", EntryPoint = "XCreateSimpleWindow")] private static extern nuint MakeWindow(IntPtr display, nuint parent, int x, int y, uint width, uint height, uint border, nuint borderPixel, nuint background);
        [DllImport("libX11.so.6", EntryPoint = "XMapWindow")] private static extern int Map(IntPtr display, nuint window);
        [DllImport("libX11.so.6", EntryPoint = "XSetWindowBackground")] private static extern int Background(IntPtr display, nuint window, nuint pixel);
        [DllImport("libX11.so.6", EntryPoint = "XClearWindow")] private static extern int Clear(IntPtr display, nuint window);
        [DllImport("libX11.so.6", EntryPoint = "XFlush")] private static extern int Flush(IntPtr display);
        private static nuint Make(IntPtr display, int screen)
        {
            var window = MakeWindow(display, Root(display, screen), 100, 100, 80, 80, 0, 0, 0x00FF0000);
            Map(display, window);
            Flush(display);
            return window;
        }
    }
}
