namespace URemote.Linux;

// Near-black PipeWire frames keep a high capture rate while x264 collapses.
// Healthy remote desktops in the field sit around 2–5 Mbps; a stuck KWin stream is under 0.1.
public static class PortalCaptureHealth
{
    public const double LowMbps = 0.3;
    public static bool IsLowBitrate(double fps, double encodedMbps) => fps >= 20 && encodedMbps < LowMbps;
    // One window is at least two seconds and 120 frames. Two low windows is the stale streak.
    public static bool WindowIsStale(int frames, TimeSpan elapsed, long bytes)
    {
        if (frames < 120 || elapsed < TimeSpan.FromSeconds(2) || elapsed <= TimeSpan.Zero) return false;
        var fps = frames / elapsed.TotalSeconds;
        var mbps = bytes * 8d / elapsed.TotalSeconds / 1_000_000d;
        return IsLowBitrate(fps, mbps);
    }
    public static bool StreakTriggers(int consecutiveLowWindows) => consecutiveLowWindows >= 2;
    public static bool BlackStreak(int frames, TimeSpan elapsed) => frames >= 30 && elapsed >= TimeSpan.FromSeconds(4);
    // Mean luma of a subsample. Blank/lock frames are near zero; a dark but real desktop is not.
    public static bool NearBlack(ReadOnlySpan<byte> bgra)
    {
        if (bgra.Length < 16) return false;
        long luma = 0;
        var samples = 0;
        var occupied = 0;
        Span<int> buckets = stackalloc int[8];
        var step = bgra.Length > 4096 ? 128 * 4 : 4;
        for (var i = 0; i + 3 < bgra.Length; i += step)
        {
            var y = (bgra[i + 2] * 2 + bgra[i + 1] * 5 + bgra[i]) >> 3;
            luma += y;
            samples++;
            var bucket = y >> 5;
            if (buckets[bucket] == 0) occupied++;
            buckets[bucket]++;
        }
        if (samples == 0) return false;
        var mean = luma / samples;
        return mean < 8 || (mean < 12 && occupied <= 1);
    }
}
