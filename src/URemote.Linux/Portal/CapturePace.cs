namespace URemote.Linux;

// Absolute deadlines absorb timer jitter; a slow encoder skips missed deadlines
// instead of queuing frames or bursting stale pictures to catch up.
internal sealed class CapturePace(int fps, long frequency, long started)
{
    private readonly double period = (double)frequency / Math.Max(fps, 1);
    private double next = started;
    internal TimeSpan AfterFrame(long now)
    {
        next += period;
        if (next < now) next = now;
        return TimeSpan.FromSeconds((next - now) / frequency);
    }
}
