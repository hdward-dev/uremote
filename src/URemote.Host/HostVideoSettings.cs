using URemote.Core;
internal sealed record VideoProfile(int Width, int Height, int Fps, int Bitrate, int Crf = 18);
internal sealed class HostVideoSettings
{
    private readonly (int Width, int Height)[] native;
    private readonly VideoProfile[] profiles;
    public bool SingleVideoStream { get; }
    private int selectedScreen;
    public int SelectedScreen => Volatile.Read(ref selectedScreen);
    public HostVideoSettings((int Width, int Height)[] dimensions, bool singleVideoStream = false)
    { SingleVideoStream = singleVideoStream; native = dimensions; profiles = dimensions.Select(d => new VideoProfile(d.Width, d.Height, 30, 16000000)).ToArray(); }
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
            var bitrate = update.Quality switch { 1 => 4000000, 2 => 8000000, 3 => 16000000, 4 => 24000000, 5 => 12000000, 6 => 24000000, _ => p.Bitrate };
            var crf = update.Quality switch { 1 => 28, 2 => 23, 3 => 18, 4 => 14, 5 or 6 => 18, _ => p.Crf };
            Volatile.Write(ref profiles[i], new(width, height, fps, bitrate, crf));
        }
        if (SingleVideoStream && update.Screen >= 0) Volatile.Write(ref selectedScreen, update.Screen);
        return true;
    }
}
