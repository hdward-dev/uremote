using URemote.Core;

namespace URemote.Linux;

/// <summary>
/// Per-screen capture clock. Target is min(display refresh, controller request).
/// Changing content is delivered at that rate and never faster. Static content may wait longer.
/// </summary>
internal static class X11FramePace
{
    internal const int DefaultRefreshHz = 60;
    internal const int DefaultRequestedFps = 30;
    internal const int StaticFloorFps = 2;

    internal static int RefreshHz(int milliHz)
    {
        if (milliHz <= 0) return DefaultRefreshHz;
        var hz = (int)Math.Round(milliHz / 1000.0);
        if (hz < 1) return DefaultRefreshHz;
        return Math.Min(hz, HostDisplayInfo.MaxSupportedFps);
    }

    internal static int TargetFps(int refreshHz, int requestedFps)
    {
        var refresh = refreshHz is >= 1 and <= HostDisplayInfo.MaxSupportedFps ? refreshHz : DefaultRefreshHz;
        var requested = requestedFps is >= 1 and <= HostDisplayInfo.MaxSupportedFps ? requestedFps : refresh;
        return Math.Min(refresh, requested);
    }

    internal static TimeSpan Interval(int fps)
        => TimeSpan.FromTicks((long)Math.Round(TimeSpan.TicksPerSecond / (double)Math.Clamp(fps, 1, HostDisplayInfo.MaxSupportedFps)));

    /// <summary>
    /// Wait before the next delivered frame. <paramref name="sinceLastDelivery"/> is the time already elapsed.
    /// A changed frame waits only the remainder of the target period. An unchanged frame may wait until the static floor.
    /// </summary>
    internal static TimeSpan Delay(TimeSpan sinceLastDelivery, bool contentChanged, int targetFps, int staticFloorFps = StaticFloorFps)
    {
        if (sinceLastDelivery < TimeSpan.Zero) sinceLastDelivery = TimeSpan.Zero;
        var target = Interval(targetFps);
        if (contentChanged)
            return sinceLastDelivery >= target ? TimeSpan.Zero : target - sinceLastDelivery;
        var floor = Math.Clamp(staticFloorFps, 1, Math.Max(1, targetFps));
        var idle = Interval(floor);
        if (idle < target) idle = target;
        return sinceLastDelivery >= idle ? TimeSpan.Zero : idle - sinceLastDelivery;
    }
}

/// <summary>Controller-requested fps, keyed by output. Absent entries use the host's 30 fps default.</summary>
internal static class X11FrameRateNotes
{
    private static readonly object gate = new();
    private static readonly Dictionary<uint, int> requested = [];

    internal static void Note(uint output, int fps)
    {
        if (fps is < 1 or > HostDisplayInfo.MaxSupportedFps) return;
        lock (gate) requested[output] = fps;
    }

    internal static int Requested(uint output)
    {
        lock (gate) return requested.TryGetValue(output, out var fps) ? fps : X11FramePace.DefaultRequestedFps;
    }

    internal static void ResetForTests()
    {
        lock (gate) requested.Clear();
    }
}
