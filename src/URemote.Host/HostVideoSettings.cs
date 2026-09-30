using URemote.Core;
internal sealed record VideoProfile(int Width, int Height, int Fps, int Bitrate, int Crf = 18, int Recovery = 0);
internal sealed class HostVideoSettings
{
    private readonly (int Width, int Height)[] native;
    private readonly VideoProfile[] profiles;
    public bool SingleVideoStream { get; }
    private int selectedScreen;
    private int networkCeiling = HostWirePlatform.Windows ? 4000000 : 40000000;
    private long received, lost, decoded;
    private bool haveFeedback;
    private int healthyIntervals;
    private readonly int[] requestedBitrates;
    public bool ObserveReceiver(long totalReceived, long totalLost, long totalDecoded)
    {
        if (totalReceived < 0 || totalLost < 0 || totalDecoded < 0) return false;
        if (totalReceived < received || totalLost < lost || totalDecoded < decoded)
        { received = totalReceived; lost = totalLost; decoded = totalDecoded; haveFeedback = true; return false; }
        var packets = totalReceived - received;
        var missing = totalLost - lost;
        var stalled = haveFeedback && packets > 100 && totalDecoded == decoded;
        received = totalReceived; lost = totalLost; decoded = totalDecoded; haveFeedback = true;
        if (packets + missing < 100) return false;
        var loss = (double)missing / (packets + missing);
        if (stalled || loss >= 0.03)
        {
            healthyIntervals = 0;
            networkCeiling = Math.Max(1000000, Math.Min(networkCeiling, profiles.Max(p => p.Bitrate)) / 2);
        }
        else if (loss < 0.01 && ++healthyIntervals >= 2)
        {
            healthyIntervals = 0;
            networkCeiling = Math.Min(40000000, networkCeiling + Math.Max(500000, networkCeiling / 4));
        }
        else { if (loss >= 0.01) healthyIntervals = 0; return false; }
        // At the floor, continuing packet loss alone must not restart an otherwise advancing decoder.
        var changed = false;
        for (var i = 0; i < profiles.Length; i++)
        {
            var p = Get(i);
            var bitrate = Math.Min(requestedBitrates[i], networkCeiling);
            if (bitrate == p.Bitrate && !stalled) continue;
            Volatile.Write(ref profiles[i], p with { Bitrate = bitrate, Recovery = p.Recovery + 1 });
            changed = true;
        }
        return changed;
    }
    public int SelectedScreen => Volatile.Read(ref selectedScreen);
    public HostVideoSettings((int Width, int Height)[] dimensions, bool singleVideoStream = false)
    { SingleVideoStream = singleVideoStream; native = dimensions; requestedBitrates = dimensions.Select(_ => 16000000).ToArray(); profiles = dimensions.Select(d => new VideoProfile(d.Width, d.Height, 30, Math.Min(16000000, networkCeiling))).ToArray(); }
    public VideoProfile Get(int index) => Volatile.Read(ref profiles[index]);
    public bool Apply(CaptureUpdate update)
    {
        // Official Windows quality changes use screen=-2 and size=-1/-1:
        // apply to the streams while preserving their existing dimensions.
        if (update.Screen < -2 || update.Screen >= native.Length || update.Width < -1 || update.Height < -1
            || update.Width > 7680 || update.Height > 4320 || update.Quality is < 0 or > 6) return false;
        for (int i = 0; i < profiles.Length; i++)
        {
            if (update.Screen >= 0 && update.Screen != i) continue;
            var p = Get(i);
            var width = update.Width > 0 ? Math.Clamp(update.Width, 320, native[i].Width) & ~1 : p.Width;
            var height = update.Height > 0 ? Math.Clamp(update.Height, 240, native[i].Height) & ~1 : p.Height;
            var fps = update.Fps > 0 ? Math.Clamp(update.Fps, 15, HostDisplayInfo.MaxSupportedFps) : p.Fps;
            var bitrate = update.Quality switch { 1 => 4000000, 2 => 8000000, 3 => 16000000, 4 => 24000000, 5 => 12000000, 6 => 24000000, _ => requestedBitrates[i] };
            var crf = update.Quality switch { 1 => 28, 2 => 23, 3 => 18, 4 => 14, 5 or 6 => 18, _ => p.Crf };
            requestedBitrates[i] = bitrate;
            Volatile.Write(ref profiles[i], new(width, height, fps, Math.Min(bitrate, networkCeiling), crf, p.Recovery));
        }
        if (SingleVideoStream && update.Screen >= 0) Volatile.Write(ref selectedScreen, update.Screen);
        return true;
    }
}
