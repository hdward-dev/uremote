namespace URemote.Media;

// RTP media time follows elapsed monotonic time, even if capture/encode misses
// its nominal frame rate. Otherwise a 20-FPS stream stamped as 60 accumulates
// clock skew that the official receiver treats as jitter and playback delay.
internal sealed class VideoRtpClock(uint initial, long started, long frequency)
{
    private uint last;
    private bool used;
    internal uint Stamp(long now)
    {
        var elapsed = Math.Max(0, now - started) * 90000d / frequency;
        var stamp = unchecked(initial + (uint)(ulong)elapsed);
        if (used && unchecked((int)(stamp - last)) <= 0) stamp = unchecked(last + 1);
        used = true;
        return last = stamp;
    }
}
