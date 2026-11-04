using System.Text.RegularExpressions;
using URemote.Core;
using URemote.Linux;

static class CaptureOnDemandChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        check(CaptureScheduler.DefaultGrace == TimeSpan.FromSeconds(30), "capture grace stays 30 seconds");
        await SchedulerAsync(check);
        await LockedIdleAsync(check);
    }

    private static async Task SchedulerAsync(Action<bool, string> check)
    {
        var opens = 0;
        uint[] armedScreens = [];
        var idleLogs = new List<string>();
        var idle = new CaptureScheduler((_, _) =>
        {
            Interlocked.Increment(ref opens);
            return Task.CompletedTask;
        }, _ => Task.FromResult(0), TimeSpan.FromMilliseconds(30));
        await Task.Delay(70);
        check(opens == 0 && idleLogs.Count == 0, "no capture stream is opened at startup");
        idle.Abandon();

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new List<string>();
        var closes = 0;
        var scheduler = new CaptureScheduler((screens, _) =>
        {
            armedScreens = screens.ToArray();
            var n = Interlocked.Increment(ref opens);
            if (n == 1) started.TrySetResult();
            return n == 1 ? release.Task : Task.CompletedTask;
        }, _ =>
        {
            Interlocked.Increment(ref closes);
            return Task.FromResult(armedScreens.Length);
        }, TimeSpan.FromMilliseconds(80));

        var pending = Task.WhenAll(Enumerable.Range(0, 8).Select(_ => scheduler.ArmAsync([4u, 8u], "viewer-requested", logs.Add, CancellationToken.None)));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(30);
        check(opens == 1, "concurrent viewer connects open capture once");
        release.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        check(opens == 1 && logs.Count == 8 && logs.All(line => line == "capture-armed;reason=viewer-requested;screens=2"),
            "each viewer request logs capture-armed without opening again");
        scheduler.NoteRealFrame(8);
        scheduler.NoteRealFrame(8);
        scheduler.NoteRealFrame(4);
        check(logs.Any(line => Regex.IsMatch(line, "^capture-first-frame-ms;screen=2;ms=\\d+$"))
            && logs.Any(line => Regex.IsMatch(line, "^capture-first-frame-ms;screen=1;ms=\\d+$"))
            && logs.Count(line => line.StartsWith("capture-first-frame-ms;", StringComparison.Ordinal)) == 2,
            "first real buffer is timed once per screen from the arm");

        for (var i = 0; i < 8; i++) scheduler.Release();
        await scheduler.ArmAsync([4u, 8u], "viewer-requested", logs.Add, CancellationToken.None);
        check(opens == 1, "a reconnect inside the grace period reuses the open pipelines");
        scheduler.Release();
        var closed = await WaitForAsync(() => logs.Contains("capture-idle-closed;screens=2"), TimeSpan.FromSeconds(2));
        check(closed && closes == 1 && opens == 1, "pipelines close once after the grace period");

        await scheduler.ArmAsync([4u], "viewer-requested", logs.Add, CancellationToken.None);
        check(opens == 2, "a viewer after the grace period opens capture again");
        scheduler.Release();
        await WaitForAsync(() => closes == 2, TimeSpan.FromSeconds(2));

        var swapped = 0;
        var swapLogs = new List<string>();
        var swap = new CaptureScheduler((_, _) =>
        {
            Interlocked.Increment(ref swapped);
            return Task.CompletedTask;
        }, _ => Task.FromResult(1), TimeSpan.FromMilliseconds(50));
        await swap.NoteSessionSwappedAsync(CancellationToken.None);
        check(swapped == 0 && swapLogs.Count == 0, "an idle portal refresh does not open pipelines");
        await swap.ArmAsync([1u], "viewer-requested", swapLogs.Add, CancellationToken.None);
        await swap.NoteSessionSwappedAsync(CancellationToken.None);
        check(swapped == 2 && swapLogs.Count(line => line.StartsWith("capture-armed;", StringComparison.Ordinal)) == 1,
            "a refresh while a viewer is connected reopens pipelines without a second arm log");
        swap.Abandon();
        scheduler.Abandon();
    }

    private static async Task LockedIdleAsync(Action<bool, string> check)
    {
        SessionLock.ResetForTests();
        LockInputNotices.ResetForTests();
        DesktopBackend.ResetOutputAliasesForTests();
        var opens = 0;
        try
        {
            await using var session = PortalDesktopSession.CreateForTests();
            session.PipeOverrideForTests = (_, _) =>
            {
                Interlocked.Increment(ref opens);
                return Task.FromResult<IPortalPipe>(new SilentPipe());
            };
            session.RememberOutputForTests(4, 0, 0, 2560, 1440);
            session.RememberOutputForTests(8, 2560, 0, 3840, 2160);
            check(opens == 0 && !session.HasPipelineForTests(4) && !session.HasPipelineForTests(8),
                "startup keeps portal geometry and does not open pipewiresrc");
            SessionLock.ObserveScreensaver(true);
            check(SessionLock.IsLocked == true, "the idle connect case runs while the session is locked");
            var before = await session.CaptureAsync(8, CancellationToken.None);
            check(before.Pending && before.Width == 3840 && before.Height == 2160, "a locked idle screen is a placeholder before any pipeline exists");
            Array.Clear(before.Pixels);
            IDesktopPointer? pointer = null;
            var rejected = false;
            try { pointer = session.CreatePointer(8); }
            catch (ArgumentException) { rejected = true; }
            check(!rejected && pointer is not null, "CreatePointer works from layout while capture is idle and locked");
            var logs = new List<string>();
            FakeAbsolute? device = null;
            await using var routed = await LockScreenInput.CreatePointerForTests(8, pointer!, 3840, 2160, session.ScreenRectsForTests(), (_, _) =>
            {
                device = new FakeAbsolute(session.ScreenRectsForTests());
                return Task.FromResult<ILockPointer>(device);
            }, new FixedLock(true), logs.Add, CancellationToken.None);
            await routed.ApplyAsync(new HostMouseMessage(MouseAction.MoveAbsolute, 0, 0), PointerCoordinates.MacNormalized, 3840, 2160);
            check(device!.Moves.Single() == (2560, 0) && logs.Single() == "pointer-backend=uinput;reason=locked",
                "connecting while locked still moves the pointer through uinput");
            await session.OpenPipesAsync([8u], CancellationToken.None);
            check(opens == 1 && session.HasPipelineForTests(8) && !session.HasPipelineForTests(4),
                "arming capture while locked opens the selected screen once");
            await session.RebuildStreamAsync(8, CancellationToken.None);
            check(opens == 1, "a locked screen does not rebuild its pipeline");
            var frame = await session.CaptureAsync(8, CancellationToken.None);
            check(frame.Pending && frame.Width == 3840 && frame.Height == 2160, "a locked screen with no buffer stays a placeholder after connect");
            Array.Clear(frame.Pixels);
            check(await session.ClosePipesAsync() == 1 && !session.HasPipelineForTests(8), "closing capture drops the pipeline and keeps the layout");
            var again = await session.CaptureAsync(8, CancellationToken.None);
            check(again.Pending && session.ScreenRectsForTests().Length == 2, "layout remains after the pipeline is closed");
            Array.Clear(again.Pixels);
            await pointer!.DisposeAsync();
        }
        finally
        {
            SessionLock.ResetForTests();
            LockInputNotices.ResetForTests();
            DesktopBackend.ResetOutputAliasesForTests();
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> ready, TimeSpan timeout)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (ready()) return true;
            await Task.Delay(10);
        }
        return ready();
    }

    private sealed class SilentPipe : IPortalPipe
    {
        public Task<CapturedScreen> CaptureAsync(CancellationToken ct, TimeSpan firstFrameBudget) => throw new TimeoutException("no buffer");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedLock(bool locked) : ILockSource
    {
        public bool? IsLocked => locked;
        public event Action? Changed { add { } remove { } }
    }

    private sealed class FakeAbsolute(IReadOnlyList<ScreenRect> screens) : ILockPointer
    {
        public IReadOnlyList<ScreenRect> Screens { get; } = screens;
        public (int MinX, int MinY, int MaxX, int MaxY) Range { get; } = UInputPointerMap.Range(screens);
        public List<(int X, int Y)> Moves { get; } = [];
        public Task MoveAsync(int x, int y, CancellationToken ct) { Moves.Add((x, y)); return Task.CompletedTask; }
        public Task ButtonAsync(uint code, bool down, CancellationToken ct) => Task.CompletedTask;
        public Task ScrollAsync(int horizontal, int vertical, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
