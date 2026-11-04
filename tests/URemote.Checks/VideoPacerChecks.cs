using URemote.Media;

internal static class VideoPacerChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const long frequency = 1000000;
        static long Send(VideoPacketPacer pacer, int bytes, long started = 0, bool onLink = true, bool burst = true)
        {
            var now = started;
            while (bytes > 0)
            {
                var size = Math.Min(bytes, 1164);
                var wait = pacer.Reserve(size, 8000000, now, onLink, burst);
                now += (long)Math.Ceiling(wait.TotalSeconds * frequency);
                bytes -= size;
            }
            return now;
        }
        var pacer = new VideoPacketPacer(frequency);
        var elapsed = Send(pacer, 500000);
        check(elapsed is >= 49000 and <= 51000, "an on-link 500 KB refresh uses a smooth 50 ms burst instead of a 385 ms per-frame throttle");
        var routed = Send(new(frequency), 500000, onLink: false);
        check(routed is >= 95000 and <= 98000, "routed paths keep a smaller bounded peak rate");
        var total = Send(new(frequency), 5000000);
        check(total is >= 3340000 and <= 3360000, "consecutive large frames still obey the average bitrate and finite VBV allowance");
        var strict = Send(new(frequency), 500000, burst: false);
        check(strict is >= 384000 and <= 386000, "loss feedback disables bursts and returns to strict average-rate pacing");
        var afterIdle = Send(pacer, 500000, 2000000);
        check(afterIdle - 2000000 is >= 49000 and <= 51000, "a static desktop replenishes only the bounded burst allowance");
        check(VideoPacketPacer.MatchesPrefix(new byte[] {192,168,40,1}, new byte[] {192,168,40,123}, 24)
            && !VideoPacketPacer.MatchesPrefix(new byte[] {192,168,40,1}, new byte[] {192,168,41,123}, 24)
            && !VideoPacketPacer.MatchesPrefix(new byte[] {192,168,40,1}, new byte[] {192,168,40,123}, 32),
            "LAN burst classification requires the actual connected subnet");
    }
}
