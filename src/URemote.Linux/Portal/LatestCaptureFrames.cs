using System.Threading.Channels;

namespace URemote.Linux;

// A slow encoder consumes the newest capture, never a queue of old desktops.
// Ownership transfers on Push; dropped and abandoned pixel arrays are erased.
internal sealed class LatestCaptureFrames : IDisposable
{
    private readonly Channel<CapturedScreen> frames = Channel.CreateBounded<CapturedScreen>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true },
        frame => Array.Clear(frame.Pixels));
    internal bool Push(CapturedScreen frame)
    {
        if (frames.Writer.TryWrite(frame)) return true;
        Array.Clear(frame.Pixels);
        return false;
    }
    internal IAsyncEnumerable<CapturedScreen> ReadAsync(CancellationToken ct) => frames.Reader.ReadAllAsync(ct);
    internal void Complete(Exception? error = null) => frames.Writer.TryComplete(error);
    public void Dispose()
    {
        Complete();
        while (frames.Reader.TryRead(out var frame)) Array.Clear(frame.Pixels);
    }
}
