using URemote.Core;
using URemote.Linux;

static class LockInputChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        State(check);
        Startup(check);
        Geometry(check);
        Packets(check);
        await KeyboardAsync(check);
        await PointerAsync(check);
        SessionLock.ResetForTests();
        LockInputNotices.ResetForTests();
    }

    private static void State(Action<bool, string> check)
    {
        var state = new SessionLockState();
        check(state.IsLocked is null, "lock is unknown until a source reports");
        check(state.ObserveScreensaver(true) && state.IsLocked == true, "screensaver active locks even before logind answers");
        check(!state.ObserveLoginHint(false) && state.IsLocked == true, "LockedHint=false does not clear an active screensaver");
        check(!state.ObserveLoginHint(true) && state.IsLocked == true, "LockedHint stays a secondary lock");
        check(!state.ObserveScreensaver(false) && state.IsLocked == true, "login hint keeps the lock after the screensaver clears");
        check(state.ObserveLoginHint(false) && state.IsLocked == false, "unlock requires every known source to be clear");

        var hintOnly = new SessionLockState();
        check(hintOnly.ObserveLoginHint(true) && hintOnly.IsLocked == true, "LockedHint alone can lock when the screensaver is unavailable");
        check(hintOnly.ObserveLoginHint(false) && hintOnly.IsLocked is null, "a cleared LockedHint without a screensaver reading is not proof of unlock");
        var saverOnly = new SessionLockState();
        check(saverOnly.ObserveScreensaver(false) && saverOnly.IsLocked == false, "an inactive screensaver with no login hint is unlocked");
        var hintClear = new SessionLockState();
        check(!hintClear.ObserveLoginHint(false) && hintClear.IsLocked is null, "LockedHint=false alone does not mark the session unlocked");
    }

    private static void Startup(Action<bool, string> check)
    {
        var logs = new List<string>();
        void NoteBoth()
        {
            SessionLock.NoteProbeFinished(SessionLock.ScreensaverProbe, logs.Add);
            SessionLock.NoteProbeFinished(SessionLock.LoginProbe, logs.Add);
        }
        SessionLock.ResetForTests();
        SessionLock.ObserveScreensaver(true);
        SessionLock.ObserveLoginHint(false);
        SessionLock.BeginInitialProbe();
        SessionLock.NoteProbeFinished(SessionLock.ScreensaverProbe, logs.Add);
        check(logs.Count == 0, "startup lock waits for both probes");
        SessionLock.NoteProbeFinished(SessionLock.LoginProbe, logs.Add);
        check(logs.Single() == "portal-start-locked=1", "host already locked is reported when LockedHint is false");
        SessionLock.NoteProbeFinished(SessionLock.ScreensaverProbe, logs.Add);
        check(logs.Count == 1, "startup lock is logged once");

        logs.Clear();
        SessionLock.ResetForTests();
        SessionLock.ObserveScreensaver(false);
        SessionLock.ObserveLoginHint(true);
        SessionLock.BeginInitialProbe();
        NoteBoth();
        check(logs.Single() == "portal-start-locked=1", "LockedHint locks at startup when the screensaver is inactive");

        logs.Clear();
        SessionLock.ResetForTests();
        SessionLock.ObserveScreensaver(false);
        SessionLock.ObserveLoginHint(false);
        SessionLock.BeginInitialProbe();
        NoteBoth();
        check(logs.Single() == "portal-start-locked=0", "a clear screen at startup is not treated as locked");

        logs.Clear();
        SessionLock.ResetForTests();
        SessionLock.BeginInitialProbe();
        NoteBoth();
        check(logs.Single() == "portal-start-locked=unknown", "missing lock sources stay unknown");
        SessionLock.ResetForTests();
    }

    private static void Geometry(Action<bool, string> check)
    {
        var screens = new[]
        {
            new ScreenRect(1, 0, 0, 1920, 1080),
            new ScreenRect(2, 1920, 0, 2560, 1440),
            new ScreenRect(3, -1920, 100, 1920, 1080)
        };
        var range = UInputPointerMap.Range(screens);
        check(range == (-1920, 0, 4479, 1439), "absolute pointer spans every output, including a negative origin");
        check(UInputPointerMap.Absolute(screens[1], .25, .5, PointerCoordinates.MacNormalized) == (2560, 720),
            "normalized input on the right monitor includes its virtual-desktop origin");
        check(UInputPointerMap.Absolute(screens[0], 1, 1, PointerCoordinates.MacNormalized) == (1919, 1079),
            "normalized corner lands on the last pixel");
        check(UInputPointerMap.Absolute(screens[2], 0, 0, PointerCoordinates.MacNormalized) == (-1920, 100),
            "the left monitor keeps its negative origin");
        check(UInputPointerMap.Absolute(screens[1], 10, 20, PointerCoordinates.WindowsPixels) == (1930, 20),
            "pixel input is output-local before the desktop origin is added");
        check(UInputPointerMap.Resolve(screens, 2, 1, 1).Output == 2, "pointer routing selects the output that owns the event");
        check(UInputPointerMap.Resolve([screens[0]], 99, 640, 480) == screens[0], "a single screen is used when the output id is stale");
        check(UInputPointerMap.Resolve([], 7, 800, 600) == new ScreenRect(7, 0, 0, 800, 600), "missing layout falls back to the captured size");
        check(UInputPointerMap.Wheel(2.2, -3.4) == (2, -3), "uinput wheel keeps the UU vertical direction");
        try { UInputPointerMap.Absolute(screens[0], double.NaN, 0, PointerCoordinates.MacNormalized); check(false, "nonfinite pointer rejected"); }
        catch (ArgumentException) { check(true, "nonfinite pointer rejected"); }
        try { UInputPointerMap.Range([]); check(false, "empty layout rejected"); }
        catch (ArgumentException) { check(true, "empty layout rejected"); }
    }

    private static void Packets(Action<bool, string> check)
    {
        var abs = UInputPointer.AbsSetup(0, -1920, 1919);
        check(abs.Length == 28 && BitConverter.ToUInt16(abs, 0) == 0
            && BitConverter.ToInt32(abs, 8) == -1920 && BitConverter.ToInt32(abs, 12) == 1919,
            "absolute axis setup matches uinput_abs_setup");
        var move = UInputPointer.MovePacket(-1920, 100);
        check(move.Length == 72 && BitConverter.ToUInt16(move, 16) == 3 && BitConverter.ToUInt16(move, 18) == 0
            && BitConverter.ToInt32(move, 20) == -1920 && BitConverter.ToUInt16(move, 40) == 3
            && BitConverter.ToUInt16(move, 42) == 1 && BitConverter.ToInt32(move, 44) == 100
            && move.AsSpan(48).ToArray().All(b => b == 0),
            "pointer motion is ABS_X, ABS_Y, then SYN_REPORT");
        var key = UInputPointer.KeyPacket(0x110, 1);
        check(key.Length == 48 && BitConverter.ToUInt16(key, 16) == 1 && BitConverter.ToUInt16(key, 18) == 0x110
            && BitConverter.ToInt32(key, 20) == 1 && key.AsSpan(24).ToArray().All(b => b == 0),
            "button events are EV_KEY plus SYN_REPORT");
        var wheel = UInputPointer.ScrollPacket(0, 3);
        var both = UInputPointer.ScrollPacket(2, -4);
        check(wheel.Length == 48 && BitConverter.ToUInt16(wheel, 16) == 2 && BitConverter.ToUInt16(wheel, 18) == 8
            && BitConverter.ToInt32(wheel, 20) == 3 && both.Length == 72 && BitConverter.ToUInt16(both, 42) == 6
            && BitConverter.ToInt32(both, 44) == 2, "scroll uses REL_WHEEL and REL_HWHEEL");
    }

    private static async Task KeyboardAsync(Action<bool, string> check)
    {
        var logs = new List<string>();
        var source = new MutableLock(false);
        var primary = new FakeKeyboard();
        await using (var router = await LockScreenInput.CreateKeyboardForTests(primary, "portal", _ => throw new InvalidOperationException("uinput opened while unlocked"), source, logs.Add, CancellationToken.None))
        {
            await router.ApplyAsync(new HostKeyMessage(KeyAction.Click, 36));
            check(logs.SequenceEqual(["keyboard-backend=portal"]) && primary.Events.Count == 1, "an unlocked portal keyboard stays on the portal path");
        }

        logs.Clear();
        source.Set(true);
        var created = new List<FakeKeyboard>();
        primary = new FakeKeyboard();
        await using (var router = await LockScreenInput.CreateKeyboardForTests(primary, "portal", _ =>
        {
            var keyboard = new FakeKeyboard();
            created.Add(keyboard);
            return Task.FromResult<IDesktopKeyboard>(keyboard);
        }, source, logs.Add, CancellationToken.None))
        {
            await router.ApplyAsync(new HostKeyMessage(KeyAction.Press, 36));
            check(logs.Single() == "keyboard-backend=uinput;reason=locked" && primary.Events.Count == 0 && created[0].Events.Count == 1,
                "a viewer that connects while locked types through uinput");
            source.Set(false);
            await router.Settled();
            check(created[0].Events[^1].Action == KeyAction.Release, "unlock releases keys still held on uinput");
            await router.ApplyAsync(new HostKeyMessage(KeyAction.Release, 36));
            check(logs[^1] == "keyboard-backend=portal;reason=unlocked" && primary.Events.Count == 1 && primary.Events[0].Action == KeyAction.Release,
                "unlock returns later keys to the portal keyboard");
            source.Set(true);
            await router.Settled();
            await router.ApplyAsync(new HostKeyMessage(KeyAction.Click, 36));
            check(created.Count == 2 && created[1].Events.Count == 1 && logs.Contains("keyboard-backend=uinput;reason=locked"),
                "locking again opens a fresh uinput keyboard");
        }

        logs.Clear();
        source.Set(true);
        var opens = 0;
        primary = new FakeKeyboard();
        await using var denied = await LockScreenInput.CreateKeyboardForTests(primary, "portal", _ =>
        {
            opens++;
            throw new UnauthorizedAccessException("no uinput");
        }, source, logs.Add, CancellationToken.None);
        await denied.ApplyAsync(new HostKeyMessage(KeyAction.Click, 36));
        await denied.ApplyAsync(new HostKeyMessage(KeyAction.Click, 36));
        check(opens == 1 && primary.Events.Count == 2 && logs.Single() == "keyboard-backend=portal;reason=uinput-unavailable",
            "a missing /dev/uinput keeps the portal keyboard and is logged once");
    }

    private static async Task PointerAsync(Action<bool, string> check)
    {
        LockInputNotices.ResetForTests();
        var screens = new[]
        {
            new ScreenRect(1, 0, 0, 1920, 1080),
            new ScreenRect(2, 1920, 0, 2560, 1440),
            new ScreenRect(3, -1920, 100, 1920, 1080)
        };
        var logs = new List<string>();
        var source = new MutableLock(true);
        var primary = new FakePointer();
        FakeAbsolute? device = null;
        await using (var router = await LockScreenInput.CreatePointerForTests(2, primary, 2560, 1440, screens, (requested, _) =>
        {
            device = new FakeAbsolute(requested);
            return Task.FromResult<ILockPointer>(device);
        }, source, logs.Add, CancellationToken.None))
        {
            await router.ApplyAsync(new HostMouseMessage(MouseAction.MoveAbsolute, .25, .5), PointerCoordinates.MacNormalized, 2560, 1440);
            check(device!.Moves.Single() == (2560, 720) && logs.Single() == "pointer-backend=uinput;reason=locked",
                "lock-screen pointer motion is scaled into the virtual desktop");
            check(primary.Events.All(item => item.Action == MouseAction.Release), "the portal pointer is not used for the locked move");
            await router.ApplyAsync(new HostMouseMessage(MouseAction.Scroll, 2.2, -3.4), PointerCoordinates.MacNormalized, 2560, 1440);
            check(device.Scrolls.Single() == (2, -3), "lock-screen scroll is not inverted a second time");
            await router.ApplyAsync(new HostMouseMessage(MouseAction.Click, Button: 1), PointerCoordinates.MacNormalized, 2560, 1440);
            check(device.Buttons.SequenceEqual([(0x110u, true), (0x110u, false)]), "lock-screen click uses the Linux left button");
            var left = new FakePointer();
            FakeAbsolute? leftDevice = null;
            await using var other = await LockScreenInput.CreatePointerForTests(3, left, 1920, 1080, screens, (requested, _) =>
            {
                leftDevice = new FakeAbsolute(requested);
                return Task.FromResult<ILockPointer>(leftDevice);
            }, source, logs.Add, CancellationToken.None);
            await other.ApplyAsync(new HostMouseMessage(MouseAction.MoveAbsolute, 0, 0), PointerCoordinates.MacNormalized, 1920, 1080);
            check(leftDevice!.Moves.Single() == (-1920, 100) && logs.Count(line => line == "pointer-backend=uinput;reason=locked") == 1,
                "a second monitor maps from its own origin and the switch is logged once");
            source.Set(false);
            await router.Settled();
            await other.Settled();
            await router.ApplyAsync(new HostMouseMessage(MouseAction.MoveAbsolute, .5, .5), PointerCoordinates.MacNormalized, 2560, 1440);
            check(logs.Contains("pointer-backend=portal;reason=unlocked") && primary.Events.Any(item => item.Action == MouseAction.MoveAbsolute) && device.Disposed,
                "unlock returns the pointer to the portal and drops the uinput device");
        }

        LockInputNotices.ResetForTests();
        logs.Clear();
        source.Set(true);
        var opens = 0;
        primary = new FakePointer();
        await using var denied = await LockScreenInput.CreatePointerForTests(1, primary, 1920, 1080, screens, (_, _) =>
        {
            opens++;
            throw new UnauthorizedAccessException("no uinput");
        }, source, logs.Add, CancellationToken.None);
        await denied.ApplyAsync(new HostMouseMessage(MouseAction.MoveAbsolute, .5, .5), PointerCoordinates.MacNormalized, 1920, 1080);
        await denied.ApplyAsync(new HostMouseMessage(MouseAction.MoveAbsolute, .25, .25), PointerCoordinates.MacNormalized, 1920, 1080);
        check(opens == 1 && primary.Events.Count(item => item.Action == MouseAction.MoveAbsolute) == 2
            && logs.Single() == "pointer-backend=portal;reason=uinput-unavailable",
            "a missing /dev/uinput keeps portal pointer events and is logged once");
    }

    private sealed class MutableLock(bool? initial) : ILockSource
    {
        public bool? IsLocked { get; private set; } = initial;
        public event Action? Changed;
        public void Set(bool? value)
        {
            IsLocked = value;
            Changed?.Invoke();
        }
    }

    private sealed class FakeKeyboard : IDesktopKeyboard
    {
        public List<HostKeyMessage> Events { get; } = [];
        public Task ApplyAsync(HostKeyMessage message, CancellationToken ct = default)
        {
            Events.Add(message);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakePointer : IDesktopPointer
    {
        public List<HostMouseMessage> Events { get; } = [];
        public Task ApplyAsync(HostMouseMessage message, PointerCoordinates coordinates, uint width, uint height, CancellationToken ct = default)
        {
            Events.Add(message);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAbsolute(IReadOnlyList<ScreenRect> screens) : ILockPointer
    {
        public IReadOnlyList<ScreenRect> Screens { get; } = screens;
        public (int MinX, int MinY, int MaxX, int MaxY) Range { get; } = UInputPointerMap.Range(screens);
        public List<(int X, int Y)> Moves { get; } = [];
        public List<(uint Code, bool Down)> Buttons { get; } = [];
        public List<(int Horizontal, int Vertical)> Scrolls { get; } = [];
        public bool Disposed { get; private set; }
        public Task MoveAsync(int x, int y, CancellationToken ct) { Moves.Add((x, y)); return Task.CompletedTask; }
        public Task ButtonAsync(uint code, bool down, CancellationToken ct) { Buttons.Add((code, down)); return Task.CompletedTask; }
        public Task ScrollAsync(int horizontal, int vertical, CancellationToken ct) { Scrolls.Add((horizontal, vertical)); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
