using URemote.Core;
internal sealed record VideoProfile(int Width, int Height, int Fps, int Bitrate, int Crf = 18, int Recovery = 0, int ChromaFormat = 1);
internal sealed class HostVideoSettings
{
    private readonly object changes = new();
    private readonly (int Width, int Height)[] native;
    private readonly (int Width, int Height)[] ceiling;
    private readonly (int Width, int Height)[] maxScale;
    private readonly VideoProfile[] profiles;
    private readonly int[] frameRateLimits;
    private readonly int[] requestedFps;
    private readonly bool[] sizeLocked;
    private readonly Dictionary<int, (int Width, int Height)> decoderLimits;
    private bool supportsTrueColor;
    public bool SingleVideoStream { get; }
    private int selectedScreen;
    // A session starts without evidence of congestion. Do not force every quality tier
    // through a 4 Mbps startup ceiling; lower it only after receiver feedback.
    private int networkCeiling = 40000000;
    private long received, lost, decoded;
    private bool haveFeedback;
    private int healthyIntervals;
    private readonly Func<long> now;
    private long lastDecodedProgress, stagnantPackets;
    private int stagnantIntervals;
    private long? lastCongestion, lastStallRefresh;
    private readonly int[] requestedBitrates;
    private readonly int[] qualities, autoQualities, customBitrates;
    public string Describe(int index)
    {
        var p = Get(index);
        return $"quality={qualities[index]};auto-quality={autoQualities[index]};requested-bitrate={requestedBitrates[index]};effective-bitrate={p.Bitrate};network-ceiling={networkCeiling};crf={p.Crf};chroma={p.ChromaFormat};output={p.Width}x{p.Height};max-scale={maxScale[index].Width}x{maxScale[index].Height}";
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
        lock (changes) return ObserveReceiverLocked(totalReceived, totalLost, totalDecoded);
    }
    private bool ObserveReceiverLocked(long totalReceived, long totalLost, long totalDecoded)
    {
        if (totalReceived < 0 || totalLost < 0 || totalDecoded < 0) return false;
        var time = now();
        if (!haveFeedback || totalReceived < received || totalLost < lost || totalDecoded < decoded)
        {
            lastDecodedProgress = time; stagnantPackets = 0; stagnantIntervals = 0; lastStallRefresh = null;
            if (haveFeedback)
            { received = totalReceived; lost = totalLost; decoded = totalDecoded; healthyIntervals = 0; return false; }
        }
        var packets = totalReceived - received;
        var missing = totalLost - lost;
        var advancing = totalDecoded > decoded;
        if (advancing) { lastDecodedProgress = time; stagnantPackets = 0; stagnantIntervals = 0; lastStallRefresh = null; }
        else if (haveFeedback && packets > 0) { stagnantPackets += packets; stagnantIntervals++; }
        var stalled = packets > 0 && stagnantIntervals >= 2 && stagnantPackets > 100 && time - lastDecodedProgress >= 3000
            && (lastStallRefresh is null || time - lastStallRefresh.Value >= 5000);
        received = totalReceived; lost = totalLost; decoded = totalDecoded; haveFeedback = true;
        var enoughPackets = packets + missing >= 100;
        var loss = enoughPackets ? (double)missing / (packets + missing) : 0;
        if (enoughPackets && loss >= 0.03)
        {
            healthyIntervals = 0;
            if (lastCongestion is null || time - lastCongestion.Value >= 5000)
            {
                lastCongestion = time;
                networkCeiling = Math.Max(1000000, Math.Min(networkCeiling, profiles.Max(p => p.Bitrate)) / 2);
            }
        }
        else if (enoughPackets && advancing && loss < 0.01 && ++healthyIntervals >= 2)
        {
            healthyIntervals = 0;
            networkCeiling = Math.Min(40000000, networkCeiling + Math.Max(500000, networkCeiling / 4));
        }
        else if (loss >= 0.01 || !advancing) healthyIntervals = 0;
        // Decoder recovery is separate from congestion: a delayed decode counter
        // is not evidence that the network bitrate is too high.
        if (stalled) lastStallRefresh = time;
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
    public HostVideoSettings((int Width, int Height)[] dimensions, bool singleVideoStream = false, IReadOnlyList<int>? frameRateLimits = null, Func<long>? clock = null,
        bool supportsTrueColor = false, IReadOnlyList<DecoderCapability>? decoderCapabilities = null)
    {
        if (frameRateLimits is not null && frameRateLimits.Count != dimensions.Length) throw new ArgumentException("Display FPS limits do not match outputs.");
        now = clock ?? (() => Environment.TickCount64);
        this.frameRateLimits = dimensions.Select((_, i) => Math.Clamp(frameRateLimits?[i] ?? HostDisplayInfo.MaxSupportedFps, 1, HostDisplayInfo.MaxSupportedFps)).ToArray();
        SingleVideoStream = singleVideoStream;
        this.supportsTrueColor = supportsTrueColor;
        decoderLimits = (decoderCapabilities ?? []).Where(c => c.CodecType == 1 && c.ChromaFormat is 1 or 3
            && c.Width is >= 2 and <= 16384 && c.Height is >= 2 and <= 16384).GroupBy(c => c.ChromaFormat)
            .ToDictionary(g => g.Key, g => { var c = g.MaxBy(c => (long)c.Width * c.Height)!; return (c.Width, c.Height); });
        native = dimensions;
        requestedBitrates = dimensions.Select(_ => 8000000).ToArray();
        qualities = dimensions.Select(_ => 5).ToArray();
        autoQualities = dimensions.Select(_ => 2).ToArray();
        customBitrates = dimensions.Select(_ => 8000000).ToArray();
        // No 30 FPS startup cap. Until the controller asks, the target is this screen's refresh rate.
        requestedFps = (int[])this.frameRateLimits.Clone();
        sizeLocked = new bool[dimensions.Length];
        ceiling = new (int Width, int Height)[dimensions.Length];
        maxScale = new (int Width, int Height)[dimensions.Length];
        profiles = dimensions.Select((d, i) => {
            var size = Fit(i, d.Width, d.Height, 1);
            return new VideoProfile(size.Width, size.Height, this.frameRateLimits[i], Math.Min(8000000, networkCeiling), 23);
        }).ToArray();
    }
    public int DisplayFps(int index) => frameRateLimits[index];
    public int RequestedFps(int index) => Volatile.Read(ref requestedFps[index]);
    // Keep a continuous stream at the controller's target, including a still lock screen.
    public int OutputFps(int index) => Get(index).Fps;
    public VideoProfile Get(int index) => Volatile.Read(ref profiles[index]);
    // The matched wl_output mode. A controller request or a buffer from the other panel
    // cannot raise this screen past it. Unbound screens still grow from the first real frame.
    public void BindCeiling(int index, int width, int height)
    {
        lock (changes) BindCeilingLocked(index, width, height);
    }
    private void BindCeilingLocked(int index, int width, int height)
    {
        if ((uint)index >= profiles.Length) return;
        width &= ~1;
        height &= ~1;
        if (width is < 2 or > 16384 || height is < 2 or > 16384) return;
        ceiling[index] = (width, height);
        native[index] = (width, height);
        var current = Get(index);
        if (sizeLocked[index] && current.Width <= width && current.Height <= height) return;
        (width, height) = Fit(index, width, height, current.ChromaFormat);
        if (current.Width == width && current.Height == height) return;
        Volatile.Write(ref profiles[index], current with { Width = width, Height = height, Recovery = current.Recovery + 1 });
    }
    public bool RejectsSourceSize(int index, int width, int height)
    {
        if ((uint)index >= profiles.Length) return false;
        width &= ~1;
        height &= ~1;
        var cap = ceiling[index];
        return cap.Width > 0 && cap.Height > 0 && (width > cap.Width || height > cap.Height);
    }
    // A real first buffer can be larger than the placeholder used to publish the host.
    // An explicit controller size is left alone; an unbound native ceiling still grows.
    public void AdoptSourceSize(int index, int width, int height)
    {
        lock (changes) AdoptSourceSizeLocked(index, width, height);
    }
    private void AdoptSourceSizeLocked(int index, int width, int height)
    {
        if ((uint)index >= profiles.Length) return;
        width &= ~1;
        height &= ~1;
        if (width is < 2 or > 16384 || height is < 2 or > 16384) return;
        if (RejectsSourceSize(index, width, height)) return;
        if (ceiling[index].Width <= 0 && (width > native[index].Width || height > native[index].Height))
            native[index] = (Math.Max(width, native[index].Width), Math.Max(height, native[index].Height));
        if (sizeLocked[index]) return;
        var cap = ceiling[index];
        var current = Get(index);
        // Keep the hardware mode when a smaller logical-size buffer arrives. The controller
        // can still request a smaller encode; that path sets sizeLocked.
        if (cap.Width > 0 && cap.Height > 0) (width, height) = cap;
        (width, height) = Fit(index, width, height, current.ChromaFormat);
        if (current.Width == width && current.Height == height) return;
        Volatile.Write(ref profiles[index], current with { Width = width, Height = height, Recovery = current.Recovery + 1 });
    }
    public bool Apply(CaptureUpdate update)
    {
        lock (changes) return ApplyLocked(update);
    }
    private bool ApplyLocked(CaptureUpdate update)
    {
        // Official Windows quality changes use screen=-2 and size=-1/-1:
        // apply to the streams while preserving their existing dimensions.
        if (update.Screen < -2 || update.Screen >= native.Length || update.Width < -1 || update.Height < -1
            || update.Width > 7680 || update.Height > 4320 || update.Quality is < 0 or > 6
            || update.CustomBitrate < 0 || (update.Quality == 6 && update.CustomBitrate > 40000000)
            || update.MaxScaleWidth is < -1 or > 16384 || update.MaxScaleHeight is < -1 or > 16384
            || update.MaxScaleWidth == 1 || update.MaxScaleHeight == 1
            || update.ChromaFormat is not (0 or 1 or 3) || (update.ChromaFormat == 3 && !supportsTrueColor)
            || update.EnableHdr) return false;
        for (int i = 0; i < profiles.Length; i++)
        {
            if (update.Screen >= 0 && update.Screen != i) continue;
            var p = Get(i);
            var chroma = update.ChromaFormat == 0 ? p.ChromaFormat : update.ChromaFormat;
            var limit = maxScale[i];
            if (update.MaxScaleWidth > 0) limit.Width = update.MaxScaleWidth;
            if (update.MaxScaleHeight > 0) limit.Height = update.MaxScaleHeight;
            maxScale[i] = limit;
            var width = update.Width > 0 ? Math.Clamp(update.Width, 320, native[i].Width) & ~1 : sizeLocked[i] ? p.Width : native[i].Width;
            var height = update.Height > 0 ? Math.Clamp(update.Height, 240, native[i].Height) & ~1 : sizeLocked[i] ? p.Height : native[i].Height;
            if (update.Width > 0 || update.Height > 0) sizeLocked[i] = true;
            (width, height) = Fit(i, width, height, chroma);
            var fps = update.Fps > 0 ? Math.Clamp(update.Fps, Math.Min(15, frameRateLimits[i]), frameRateLimits[i]) : p.Fps;
            if (update.Fps > 0) Volatile.Write(ref requestedFps[i], fps);
            if (update.Quality != 0) qualities[i] = update.Quality;
            if (update.AutoQuality is >= 1 and <= 4) autoQualities[i] = update.AutoQuality;
            if (update.Quality == 6 && update.CustomBitrate > 0)
                customBitrates[i] = Math.Clamp(update.CustomBitrate, 1000000, 40000000);
            var (bitrate, crf) = qualities[i] == 6 ? (customBitrates[i], 18)
                : Tier(qualities[i] == 5 ? autoQualities[i] : qualities[i]);
            requestedBitrates[i] = bitrate;
            Volatile.Write(ref profiles[i], new(width, height, fps, Math.Min(bitrate, networkCeiling), crf, p.Recovery, chroma));
        }
        if (SingleVideoStream && update.Screen >= 0) Volatile.Write(ref selectedScreen, update.Screen);
        return true;
    }
    public void DisableTrueColor()
    {
        lock (changes)
        {
            supportsTrueColor = false;
            ApplyLocked(new(-1, 0, 0, 0, 0, 0, 0, null, ChromaFormat: 1));
        }
    }
    public byte[] Sync(ulong sequence)
    {
        lock (changes)
        {
            var index = SelectedScreen;
            var p = Get(index);
            return HostCaptureProtocol.Sync(sequence, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), p.Fps,
                qualities[index], p.ChromaFormat, qualities[index] == 6 ? customBitrates[index] : 0, p.Width, p.Height);
        }
    }
    private (int Width, int Height) Fit(int index, int width, int height, int chroma)
    {
        var limit = maxScale[index];
        // Decoder sizes can differ for 4:2:0 and 4:4:4. Switching chroma must
        // never leave the stream above that format's reported decoder ceiling.
        if (decoderLimits.TryGetValue(chroma, out var decoder))
        {
            limit.Width = limit.Width > 0 ? Math.Min(limit.Width, decoder.Width) : decoder.Width;
            limit.Height = limit.Height > 0 ? Math.Min(limit.Height, decoder.Height) : decoder.Height;
        }
        var scale = Math.Min(1, Math.Min(limit.Width > 0 ? (double)limit.Width / width : 1,
            limit.Height > 0 ? (double)limit.Height / height : 1));
        return (Math.Max(2, (int)(width * scale) & ~1), Math.Max(2, (int)(height * scale) & ~1));
    }
}
