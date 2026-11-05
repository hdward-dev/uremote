using URemote.Core;
using URemote.Linux;
using URemote.Media;
using System.Threading.Channels;

namespace URemote.Host;

internal sealed class HostPrivacySession : IAsyncDisposable
{
    private readonly Func<byte[], bool> sendControl, sendText;
    private readonly Func<Action, CancellationToken, Task<IPrivacyScreen>> openScreen;
    private readonly Action<string> report;
    private readonly CancellationTokenSource stop;
    private readonly Task sender;
    private readonly Task imageWorker;
    private readonly Func<CancellationToken, Task<PrivacyScreenImage?>>? loadImage;
    private readonly Channel<PrivacyRequest?> imageRequests = Channel.CreateBounded<PrivacyRequest?>(new BoundedChannelOptions(8) { SingleReader = true });
    private readonly SemaphoreSlim screenGate = new(1);
    private PrivacyScreenImage? currentImage;
    private readonly Queue<byte[]> pending = [];
    private readonly object gate = new();
    private byte[]? latestMetrics;
    private IPrivacyScreen? screen;
    private long sequence;
    private int currentMode = 1;
    private int disposed;
    internal bool Supported { get; }
    internal HostPrivacySession(Func<byte[], bool> sendControl, Func<byte[], bool> sendText,
        Func<Action, CancellationToken, Task<IPrivacyScreen>> openScreen, bool supported, Action<string> report, CancellationToken ct,
        Func<CancellationToken, Task<PrivacyScreenImage?>>? loadImage = null)
    {
        this.sendControl = sendControl; this.sendText = sendText; this.openScreen = openScreen;
        Supported = supported; this.report = report;
        stop = CancellationTokenSource.CreateLinkedTokenSource(ct); sender = SendPendingAsync(stop.Token);
        this.loadImage = loadImage;
        imageWorker = loadImage is not null && supported ? RefreshImagesAsync(stop.Token) : Task.CompletedTask;
        if (loadImage is not null && supported) imageRequests.Writer.TryWrite(null); // prefetch, no RPC acknowledgement
    }
    internal static async Task<HostPrivacySession> OpenAsync(HostMediaPeer media, bool enableInput, Action<string> report, CancellationToken ct,
        Func<CancellationToken, Task<PrivacyScreenImage?>>? loadImage = null)
    {
        var supported = enableInput && DesktopBackend.UsingPortal && await KdePrivacyScreen.IsSupportedAsync(ct);
        report("privacy-screen-supported=" + supported);
        return new(media.SendControl, bytes => media.SendData("TEXT_DATA_CHANNEL", bytes),
            async (lost, token) => await KdePrivacyScreen.OpenAsync(lost, token), supported, report, ct, loadImage);
    }
    internal void SendInitial(bool touch)
    {
        Send(HostPrivacyProtocol.State(Next(), 1, Volatile.Read(ref currentMode)));
        SendMetrics(false, touch);
    }
    internal async Task<bool> HandleAsync(ReadOnlyMemory<byte> bytes, bool supportTouch, CancellationToken ct)
    {
        var request = HostPrivacyProtocol.Decode(bytes); if (request is null) return false;
        if (request.Kind == PrivacyRequestKind.Metrics)
        { SendMetrics(request.GeneralMetrics, supportTouch, request.Sequence); return true; }
        if (request.Kind == PrivacyRequestKind.ScreenSaver)
        {
            // Empty notification: reload the host's cloud selection, never a path
            // supplied by the controller. Keep keyboard/mouse processing independent.
            if (!Supported || loadImage is null || !imageRequests.Writer.TryWrite(request))
                Send(HostPrivacyProtocol.Reply(request, Volatile.Read(ref currentMode), false));
            else report("privacy-image-refresh-requested");
            return true;
        }
        var success = false;
        await screenGate.WaitAsync(ct);
        try
        {
            if (request.Mode == 1)
            {
                if (screen is not null) { await screen.DisposeAsync(); screen = null; }
                Volatile.Write(ref currentMode, 1); success = true;
            }
            else if (request.Mode == 2 && Supported)
            {
                if (screen is { IsActive: false }) { await screen.DisposeAsync(); screen = null; }
                if (screen is null)
                {
                    screen = await openScreen(() =>
                    {
                        lock (gate) Volatile.Write(ref currentMode, 1);
                        Send(HostPrivacyProtocol.State(Next(), 2, 1, false)); SendMetrics(false, supportTouch);
                        report("privacy-screen-restored;reason=backend-closed");
                    }, ct);
                    if (currentImage is not null) await screen.SetImageAsync(currentImage, ct);
                }
                lock (gate) { Volatile.Write(ref currentMode, screen.IsActive ? 2 : 1); success = currentMode == 2; }
            }
        }
        catch (Exception e) when (e is IOException or NotSupportedException or FormatException or OperationCanceledException or System.Net.Sockets.SocketException)
        {
            if (screen is not null) { await screen.DisposeAsync(); screen = null; }
            Volatile.Write(ref currentMode, 1);
            report("privacy-screen-failed;type=" + e.GetType().Name);
            ct.ThrowIfCancellationRequested();
        }
        finally { screenGate.Release(); }
        // Report only the effective state, after KWin acknowledged capture exclusion.
        Send(HostPrivacyProtocol.Reply(request, currentMode, success));
        Send(HostPrivacyProtocol.State(Next(), request.Mode, currentMode, success)); SendMetrics(false, supportTouch);
        report($"privacy-screen-request;mode={request.Mode};actual={currentMode};accepted={success}");
        return true;
    }
    private async Task RefreshImagesAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var request in imageRequests.Reader.ReadAllAsync(ct))
            {
                PrivacyScreenImage? image = null;
                try
                {
                    image = await loadImage!(ct);
                    await screenGate.WaitAsync(ct);
                    try
                    {
                        if (screen is { IsActive: true }) await screen.SetImageAsync(image, ct);
                        var previous = currentImage; currentImage = image; image = null; previous?.Dispose();
                        report($"privacy-image-ready;has-image={currentImage is not null};width={currentImage?.Width ?? 0};height={currentImage?.Height ?? 0}");
                    }
                    finally { screenGate.Release(); }
                    // Only acknowledge after successful download/decode and, while
                    // privacy is active, a compositor sync confirming the new buffers.
                    if (request is not null) Send(HostPrivacyProtocol.Reply(request, Volatile.Read(ref currentMode), true));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) when (e is IOException or HttpRequestException or FormatException or System.Text.Json.JsonException
                    or InvalidOperationException or ArgumentException or OperationCanceledException or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException)
                {
                    report("privacy-image-refresh-failed;type=" + e.GetType().Name);
                    if (request is not null) Send(HostPrivacyProtocol.Reply(request, Volatile.Read(ref currentMode), false));
                }
                finally { image?.Dispose(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private ulong Next() => (ulong)Interlocked.Increment(ref sequence);
    private void SendMetrics(bool general, bool touch, ulong? requestedSequence = null)
    {
        var bytes = HostPrivacyProtocol.Metrics(requestedSequence ?? Next(), Volatile.Read(ref currentMode), general, touch);
        TrySend(sendControl, bytes);
        lock (gate) latestMetrics = bytes;
    }
    private void Send(byte[] bytes)
    {
        TrySend(sendControl, bytes);
        lock (gate) { if (pending.Count < 64) pending.Enqueue(bytes); }
    }
    private async Task SendPendingAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                lock (gate)
                {
                    while (pending.TryPeek(out var next) && TrySend(sendText, next)) pending.Dequeue();
                    if (pending.Count == 0 && latestMetrics is { } bytes && TrySend(sendText, bytes)) latestMetrics = null;
                }
                await Task.Delay(50, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private static bool TrySend(Func<byte[], bool> send, byte[] bytes)
    {
        try { return send(bytes); }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.Net.Sockets.SocketException) { return false; }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); imageRequests.Writer.TryComplete();
        await screenGate.WaitAsync();
        // Restore local visibility before waiting on network cleanup.
        try
        {
            if (screen is not null) { await screen.DisposeAsync(); screen = null; report("privacy-screen-restored;reason=session-ended"); }
            currentImage?.Dispose(); currentImage = null;
        }
        finally { screenGate.Release(); }
        await Task.WhenAll(sender, imageWorker); stop.Dispose(); screenGate.Dispose();
        lock (gate) { pending.Clear(); latestMetrics = null; }
    }
}
