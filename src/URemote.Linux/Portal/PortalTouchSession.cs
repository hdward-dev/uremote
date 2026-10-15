using URemote.Core;

namespace URemote.Linux;

// Borrows the already-authorized screen/input session. Never creates or starts
// another Portal session, and never uses Notify* after ConnectToEIS.
internal sealed class PortalTouchSession : IDesktopTouch
{
    private readonly IReadOnlyDictionary<uint, string> mappings;
    private readonly CancellationTokenSource stop = new();
    private readonly bool touchGranted;
    private readonly EisTouchSink sink;
    private readonly Task pump;
    private bool disposed;
    private PortalTouchSession(EisTouchSink sink, IReadOnlyDictionary<uint, string> mappings, bool touchGranted)
    {
        this.sink = sink; this.mappings = mappings; this.touchGranted = touchGranted;
        pump = PumpAsync();
    }
    internal static async Task<PortalTouchSession> ConnectAsync(PortalBus bus, string session,
        IReadOnlyDictionary<uint, string> mappings, bool touchGranted, CancellationToken ct)
    {
        using var fd = await bus.OpenEisAsync(session, ct);
        var owner = new PortalTouchSession(new EisTouchSink(fd, fullInput: true), mappings, touchGranted);
        try
        {
            using var ready = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ready.CancelAfter(TimeSpan.FromSeconds(5));
            while (!owner.sink.HasCapability(4) || !owner.sink.HasCapability(32)
                || !owner.sink.HasCapability(16) || !mappings.Values.All(owner.sink.CanPoint))
            {
                if (!owner.Connected) throw new IOException("EIS 输入通道已关闭。");
                await Task.Delay(10, ready.Token);
            }
            return owner;
        }
        catch { await owner.DisposeAsync(); throw; }
    }
    private async Task PumpAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(8));
            do { sink.Dispatch(); } while (await timer.WaitForNextTickAsync(stop.Token));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { sink.Reset(); }
    }
    internal bool Connected => !disposed && !pump.IsCompleted && sink.Connected;
    public bool Available => Connected && touchGranted && mappings.Values.All(sink.CanMap);
    public bool Apply(uint output, HostTouchEvent input)
        => Available && mappings.TryGetValue(output, out var id) && sink.Apply(id, input);
    internal bool Point(uint output, double x, double y)
        => Connected && mappings.TryGetValue(output, out var id) && sink.Point(id, x, y);
    internal bool Key(uint code, bool down, bool button = false) => Connected && sink.Key(code, down, button);
    internal bool Scroll(double x, double y) => Connected && sink.Scroll(x, y);
    internal bool Text(string text) => Connected && sink.Text(text);
    internal bool SupportsText => Connected && sink.HasCapability(64);
    public void Reset() => sink.Reset();
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true; stop.Cancel();
        try { await pump; }
        finally { sink.Dispose(); stop.Dispose(); }
    }
}
