using System.Text.RegularExpressions;
using URemote.Linux;

static class CaptureOnDemandTests
{
    public static async Task Run(Action<bool, string> check)
    {
        check(DesktopBackend.CaptureGrace == TimeSpan.FromSeconds(30), "the host capture grace is 30 seconds");
        var opens = 0;
        var closes = 0;
        uint[] armed = [];
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new List<string>();
        try
        {
            DesktopBackend.InstallCaptureHooksForTests(async (screens, _) =>
            {
                armed = screens.ToArray();
                var n = Interlocked.Increment(ref opens);
                if (n == 1)
                {
                    started.TrySetResult();
                    await release.Task;
                }
            }, _ =>
            {
                Interlocked.Increment(ref closes);
                return Task.FromResult(armed.Length);
            }, TimeSpan.FromMilliseconds(80));
            check(opens == 0 && !DesktopBackend.CaptureStreamsOpenForTests, "a resident host does not open capture before a viewer");
            await DesktopBackend.ArmForViewerAsync([1u, 2u], dataOnly: true, logs.Add, CancellationToken.None);
            await DesktopBackend.ArmForViewerAsync([], dataOnly: false, logs.Add, CancellationToken.None);
            check(opens == 0 && logs.Count == 0, "file and terminal sessions do not arm screen capture");

            var pending = Task.WhenAll(Enumerable.Range(0, 6).Select(_ => DesktopBackend.ArmForViewerAsync([7u, 9u], false, logs.Add, CancellationToken.None)));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(20);
            check(opens == 1, "overlapping viewer requests open each screen once");
            release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
            check(logs.Count == 6 && logs.All(line => line == "capture-armed;reason=viewer-requested;screens=2"),
                "be-controlled arms capture with reason viewer-requested");
            DesktopBackend.NoteCaptureBuffer(7);
            DesktopBackend.NoteCaptureBuffer(7);
            check(Regex.IsMatch(logs[^1], "^capture-first-frame-ms;screen=1;ms=\\d+$")
                && logs.Count(line => line.StartsWith("capture-first-frame-ms;", StringComparison.Ordinal)) == 1,
                "capture-first-frame-ms is measured from arm to the first real buffer");

            for (var i = 0; i < 6; i++) DesktopBackend.ReleaseCapture();
            await DesktopBackend.ArmForViewerAsync([7u, 9u], false, logs.Add, CancellationToken.None);
            check(opens == 1 && DesktopBackend.CaptureStreamsOpenForTests, "a viewer that returns inside the grace period reuses the pipelines");
            DesktopBackend.ReleaseCapture();
            var closed = false;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(2))
            {
                if (logs.Contains("capture-idle-closed;screens=2")) { closed = true; break; }
                await Task.Delay(10);
            }
            check(closed && closes == 1 && opens == 1 && !DesktopBackend.CaptureStreamsOpenForTests,
                "capture-idle-closed is logged once the grace period ends");

            var beforeSwap = opens;
            await DesktopBackend.NoteCaptureSessionSwappedForTests(CancellationToken.None);
            check(opens == beforeSwap, "refreshing the portal while idle does not open pipelines");
            await DesktopBackend.ArmForViewerAsync([7u], false, logs.Add, CancellationToken.None);
            var opened = opens;
            await DesktopBackend.NoteCaptureSessionSwappedForTests(CancellationToken.None);
            check(opens == opened + 1, "refreshing the portal while a viewer is connected reopens pipelines");
            DesktopBackend.ReleaseCapture();
        }
        finally { DesktopBackend.ResetCaptureForTests(); }
        check(DesktopBackend.CaptureGrace == TimeSpan.FromSeconds(30) && !DesktopBackend.CaptureStreamsOpenForTests,
            "tests restore the 30 second capture grace");
    }
}
