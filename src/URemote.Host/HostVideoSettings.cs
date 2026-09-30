using URemote.Core;
internal sealed record VideoProfile(int Width, int Height, int Fps, int Bitrate, int Crf = 18, int Recovery = 0);
internal sealed class HostVideoSettings
{
    private readonly (int Width, int Height)[] native;
    private readonly VideoProfile[] profiles;
    private readonly int[] frameRateLimits;
    public bool SingleVideoStream { get; }
    private int selectedScreen;
    // A session starts without evidence of congestion. Do not force every quality tier
    // through a 4 Mbps startup ceiling; lower it only after receiver feedback.
    private int networkCeiling = 40000000;
    private long received, lost, decoded;
    private bool haveFeedback;
    private int healthyIntervals;
    private readonly int[] requestedBitrates;
    private readonly int[] qualities, autoQualities, customBitrates;
    public string Describe(int index)
    {
        var p = Get(index);
        return $"quality={qualities[index]};auto-quality={autoQualities[index]};requested-bitrate={requestedBitrates[index]};effective-bitrate={p.Bitrate};network-ceiling={networkCeiling};crf={p.Crf}";
    }
    private static (int Bitrate, int Crf) Tier(int quality) => quality switch
    {
        1 => (4000000, 28), // Legacy fluent.
        2 => (8000000, 23), // Windows HD (General on the wire).
        3 => (14000000, 18), // Windows Ultra (HD on the wire).
        4 => (30000000, 14), // Windows Original (Bluray on the wire).
        _ => (8000000, 23)
    };
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
    public HostVideoSettings((int Width, int Height)[] dimensions, bool singleVideoStream = false, IReadOnlyList<int>? frameRateLimits = null)
    {
        if (frameRateLimits is not null && frameRateLimits.Count != dimensions.Length) throw new ArgumentException("Display FPS limits do not match outputs.");
        this.frameRateLimits = dimensions.Select((_, i) => Math.Clamp(frameRateLimits?[i] ?? HostDisplayInfo.MaxSupportedFps, 1, HostDisplayInfo.MaxSupportedFps)).ToArray();
        SingleVideoStream = singleVideoStream;
        native = dimensions;
        requestedBitrates = dimensions.Select(_ => 8000000).ToArray();
        qualities = dimensions.Select(_ => 5).ToArray();
        autoQualities = dimensions.Select(_ => 2).ToArray();
        customBitrates = dimensions.Select(_ => 8000000).ToArray();
        profiles = dimensions.Select((d, i) => new VideoProfile(d.Width, d.Height,
            Math.Min(30, this.frameRateLimits[i]), Math.Min(8000000, networkCeiling), 23)).ToArray();
    }
    public VideoProfile Get(int index) => Volatile.Read(ref profiles[index]);
    public bool Apply(CaptureUpdate update)
    {
        // Official Windows quality changes use screen=-2 and size=-1/-1:
        // apply to the streams while preserving their existing dimensions.
        if (update.Screen < -2 || update.Screen >= native.Length || update.Width < -1 || update.Height < -1
            || update.Width > 7680 || update.Height > 4320 || update.Quality is < 0 or > 6
            || update.CustomBitrate < 0 || (update.Quality == 6 && update.CustomBitrate > 40000000)) return false;
        for (int i = 0; i < profiles.Length; i++)
        {
            if (update.Screen >= 0 && update.Screen != i) continue;
            var p = Get(i);
            var width = update.Width > 0 ? Math.Clamp(update.Width, 320, native[i].Width) & ~1 : p.Width;
            var height = update.Height > 0 ? Math.Clamp(update.Height, 240, native[i].Height) & ~1 : p.Height;
            var fps = update.Fps > 0 ? Math.Clamp(update.Fps, Math.Min(15, frameRateLimits[i]), frameRateLimits[i]) : p.Fps;
            if (update.Quality != 0) qualities[i] = update.Quality;
            if (update.AutoQuality is >= 1 and <= 4) autoQualities[i] = update.AutoQuality;
            if (update.Quality == 6 && update.CustomBitrate > 0)
                customBitrates[i] = Math.Clamp(update.CustomBitrate, 1000000, 40000000);
            var (bitrate, crf) = qualities[i] == 6 ? (customBitrates[i], 18)
                : Tier(qualities[i] == 5 ? autoQualities[i] : qualities[i]);
            requestedBitrates[i] = bitrate;
            Volatile.Write(ref profiles[i], new(width, height, fps, Math.Min(bitrate, networkCeiling), crf, p.Recovery));
        }
        if (SingleVideoStream && update.Screen >= 0) Volatile.Write(ref selectedScreen, update.Screen);
        return true;
    }
}
