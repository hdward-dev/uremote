using URemote.Core;

namespace URemote.Linux;

// ConnectToEIS forbids Notify* on the same session. Use a separate touch-only
// authorization so existing keyboard and pointer authorization stays intact.
internal sealed class PortalTouchSession : IDesktopTouch
{
    private readonly PortalBus bus;
    private readonly IReadOnlyDictionary<uint, string> mappings;
    private readonly CancellationTokenSource stop = new();
    private string? session;
    private EisTouchSink? sink;
    private Task pump = Task.CompletedTask;
    private bool disposed;
    private PortalTouchSession(PortalBus bus, IReadOnlyDictionary<uint, string> mappings)
    { this.bus = bus; this.mappings = mappings; }
    internal static async Task<PortalTouchSession> OpenAsync(IReadOnlyDictionary<uint, string> mappings, CancellationToken ct)
    {
        var owner = new PortalTouchSession(await PortalBus.ConnectAsync(ct), mappings);
        try { await owner.StartAsync(ct); return owner; }
        catch { await owner.DisposeAsync(); throw; }
    }
    private async Task StartAsync(CancellationToken ct)
    {
        if ((await bus.PropertyAsync(PortalBus.RemoteDesktop, "AvailableDeviceTypes", ct) & 4) == 0)
            throw new NotSupportedException("桌面未提供原生触屏授权。");
        var handle = "u" + Guid.NewGuid().ToString("N");
        session = bus.SessionPath(handle);
        using var token = PortalValue.String(handle);
        using (var result = await bus.RequestAsync(PortalBus.RemoteDesktop, "CreateSession", null, false, [("session_handle_token", token)], ct))
        {
            using var path = result.Get("session_handle") ?? throw new FormatException("缺少触屏会话。");
            session = path.Text();
            if (!session.StartsWith(PortalBus.Desktop + "/session/", StringComparison.Ordinal)) throw new FormatException("无效触屏会话。");
        }
        await bus.SubscribeAsync(PortalBus.InterfacePrefix + ".Session", "Closed", session, _ => stop.Cancel());
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        using var types = PortalValue.UInt(4);
        using (var selected = await bus.RequestAsync(PortalBus.RemoteDesktop, "SelectDevices", session, false, [("types", types)], lifetime.Token)) { }
        using (var result = await bus.RequestAsync(PortalBus.RemoteDesktop, "Start", session, true, [], lifetime.Token))
        {
            using var granted = result.Get("devices");
            if (granted is null || (granted.UInt() & 4) == 0) throw new NotSupportedException("未授权原生触屏输入。");
        }
        using var fd = await bus.OpenEisAsync(session, lifetime.Token);
        sink = new EisTouchSink(fd);
        pump = PumpAsync();
        using var ready = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        ready.CancelAfter(TimeSpan.FromSeconds(5));
        while (!mappings.Values.All(sink.CanMap)) await Task.Delay(10, ready.Token);
    }
    private async Task PumpAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(8));
            do { sink!.Dispatch(); } while (await timer.WaitForNextTickAsync(stop.Token));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { sink?.Reset(); }
    }
    public bool Available => !disposed && !stop.IsCancellationRequested && !pump.IsCompleted && mappings.Values.All(id => sink?.CanMap(id) == true);
    public bool Apply(uint output, HostTouchEvent input)
    {
        if (!Available || !mappings.TryGetValue(output, out var mapping)) return false;
        return sink!.Apply(mapping, input);
    }
    public void Reset() => sink?.Reset();
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true; stop.Cancel();
        try { await pump; }
        finally
        {
            sink?.Dispose();
            try
            {
                if (session is not null)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    using var args = PortalValue.Tuple();
                    using var result = await bus.CallAsync(PortalBus.InterfacePrefix + ".Session", "Close", args, deadline.Token, session);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException) { }
            finally { await bus.DisposeAsync(); stop.Dispose(); }
        }
    }
}
