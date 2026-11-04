namespace URemote.Linux;

// A PipeWire stream that has not produced a buffer yet must not fail the host.
// The placeholder is this monitor's size, black, and marked repeated so pacing
// treats it as static until a real sample arrives.
internal static class CapturePlaceholder
{
    internal static CapturedScreen Create(uint output, int width, int height)
    {
        width &= ~1;
        height &= ~1;
        if (width is < 2 or > 16384 || height is < 2 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(width));
        var stride = (uint)width * 4;
        var pixels = new byte[stride * (uint)height];
        return new CapturedScreen(output, (uint)width, (uint)height, stride, 1, false, pixels, Repeated: true, Pending: true);
    }
}

internal static class FirstFrameRetry
{
    // Short enough that two monitors cannot add up to the old 10s-per-screen startup failure.
    internal static readonly TimeSpan Probe = TimeSpan.FromMilliseconds(250);
    internal static readonly TimeSpan RebuildAfter = TimeSpan.FromSeconds(5);
    // A locked blank output produces no buffers. Rebuilding it frees PipeWire state the
    // remote thread is still using. Leave that pipeline up until unlock refreshes capture.
    internal static bool ShouldRebuild(TimeSpan pendingFor, bool? sessionLocked = null) =>
        sessionLocked != true && pendingFor >= RebuildAfter;
}

// Logs each missing first frame once per portal generation, then once when a real buffer arrives.
internal sealed class FirstFrameTracker
{
    private readonly object gate = new();
    private readonly bool[] pendingLogged;
    private readonly bool[] arrivedLogged;
    private int generation = int.MinValue;

    internal FirstFrameTracker(int screens)
    {
        if (screens < 0) throw new ArgumentOutOfRangeException(nameof(screens));
        pendingLogged = new bool[screens];
        arrivedLogged = new bool[screens];
    }

    internal void Observe(int index, bool pending, int portalGeneration, Action<string> report)
    {
        if ((uint)index >= pendingLogged.Length) return;
        string? line;
        lock (gate)
        {
            if (portalGeneration != generation)
            {
                generation = portalGeneration;
                Array.Clear(pendingLogged);
                Array.Clear(arrivedLogged);
            }
            if (pending)
            {
                if (pendingLogged[index]) return;
                pendingLogged[index] = true;
                arrivedLogged[index] = false;
                line = $"capture-first-frame-pending;screen={index + 1}";
            }
            else
            {
                if (!pendingLogged[index] || arrivedLogged[index]) return;
                arrivedLogged[index] = true;
                line = $"capture-first-frame-arrived;screen={index + 1}";
            }
        }
        report(line);
    }
}
