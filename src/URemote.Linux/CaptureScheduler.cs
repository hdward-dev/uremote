using System.Diagnostics;

namespace URemote.Linux;

/// <summary>
/// Portal authorization stays up for the host lifetime. Capture pipelines and XDamage
/// exist only while a viewer is connected, plus a grace period so a reconnect reuses them.
/// Open and close are serialized. Overlapping arm calls share one open.
/// </summary>
internal sealed class CaptureScheduler
{
    internal static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(30);
    private readonly Func<IReadOnlyList<uint>, CancellationToken, Task> open;
    private readonly Func<CancellationToken, Task<int>> close;
    private readonly object sync = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private TimeSpan grace;
    private int viewers;
    private int epoch;
    private int openTicket;
    private bool streamsOpen;
    private Task? opening;
    private CancellationTokenSource? idle;
    private uint[] screens = [];
    private Action<string>? report;
    private long armedAt;
    private int frameEpoch;
    private readonly Dictionary<uint, int> loggedFrame = [];

    internal CaptureScheduler(
        Func<IReadOnlyList<uint>, CancellationToken, Task> open,
        Func<CancellationToken, Task<int>> close,
        TimeSpan? grace = null)
    {
        this.open = open;
        this.close = close;
        this.grace = grace is { } value && value > TimeSpan.Zero ? value : DefaultGrace;
    }

    internal int ViewerCount { get { lock (sync) return viewers; } }
    internal bool StreamsOpen { get { lock (sync) return streamsOpen; } }
    internal TimeSpan Grace { get { lock (sync) return grace; } }

    internal void SetGrace(TimeSpan value)
    {
        if (value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(value));
        lock (sync) grace = value;
    }

    internal Task ArmAsync(IReadOnlyList<uint> requested, string reason, Action<string> report, CancellationToken ct)
    {
        if (requested.Count > 5) throw new ArgumentException("无效的显示器数量。");
        Task? pending;
        lock (sync)
        {
            this.report = report;
            screens = requested.ToArray();
            viewers++;
            CancelIdle();
            if (requested.Count == 0 || streamsOpen)
                pending = null;
            else if (opening is { IsCompleted: false })
                pending = opening;
            else
            {
                frameEpoch++;
                armedAt = Stopwatch.GetTimestamp();
                loggedFrame.Clear();
                // The task yields before it touches this lock again, so open cannot pass the log.
                pending = opening = OpenCoreAsync(++openTicket, ct);
            }
            // report only appends a line. It must not call back into the scheduler.
            report("capture-armed;reason=" + Sanitize(reason) + ";screens=" + requested.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return pending ?? Task.CompletedTask;
    }

    internal void Release()
    {
        CancellationTokenSource timer;
        int token;
        TimeSpan delay;
        lock (sync)
        {
            if (viewers > 0) viewers--;
            if (viewers > 0 || (!streamsOpen && opening is null)) return;
            token = ++epoch;
            timer = new CancellationTokenSource();
            CancelIdle();
            idle = timer;
            delay = grace;
        }
        _ = IdleAsync(token, delay, timer);
    }

    internal void NoteRealFrame(uint output)
    {
        Action<string>? sink;
        string line;
        lock (sync)
        {
            if (armedAt == 0 || report is null) return;
            var index = Array.IndexOf(screens, output);
            if (index < 0) return;
            if (loggedFrame.TryGetValue(output, out var seen) && seen == frameEpoch) return;
            loggedFrame[output] = frameEpoch;
            var ms = (int)Math.Max(0, Stopwatch.GetElapsedTime(armedAt).TotalMilliseconds);
            line = "capture-first-frame-ms;screen=" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ";ms=" + ms.ToString(System.Globalization.CultureInfo.InvariantCulture);
            sink = report;
        }
        sink.Invoke(line);
    }

    /// <summary>
    /// The portal session object was replaced. Pipes that belonged to it are gone.
    /// Open again only when a viewer is still connected.
    /// </summary>
    internal async Task NoteSessionSwappedAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            uint[] nodes;
            bool reopen;
            lock (sync)
            {
                CancelIdle();
                streamsOpen = false;
                opening = null;
                openTicket++;
                reopen = viewers > 0 && screens.Length > 0;
                nodes = screens;
                if (reopen)
                {
                    frameEpoch++;
                    armedAt = Stopwatch.GetTimestamp();
                    loggedFrame.Clear();
                }
            }
            if (!reopen) return;
            await open(nodes, ct).ConfigureAwait(false);
            var dropped = false;
            lock (sync)
            {
                if (viewers > 0) streamsOpen = true;
                else dropped = true;
            }
            if (dropped) await TearDownAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal async Task EnsureOpenAsync(CancellationToken ct)
    {
        Task? pending;
        lock (sync)
        {
            if (viewers == 0 || screens.Length == 0 || streamsOpen) return;
            if (opening is { IsCompleted: false }) pending = opening;
            else pending = opening = OpenCoreAsync(++openTicket, ct);
        }
        await pending.ConfigureAwait(false);
    }

    /// <summary>Drop viewer state without closing. The desktop lease disposes the pipelines itself.</summary>
    internal void Abandon()
    {
        lock (sync)
        {
            viewers = 0;
            streamsOpen = false;
            opening = null;
            openTicket++;
            epoch++;
            armedAt = 0;
            CancelIdle();
        }
    }

    private async Task OpenCoreAsync(int ticket, CancellationToken ct)
    {
        await Task.Yield();
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            uint[] nodes;
            lock (sync)
            {
                if (ticket != openTicket || streamsOpen || screens.Length == 0) return;
                nodes = screens;
            }
            await open(nodes, ct).ConfigureAwait(false);
            lock (sync)
            {
                if (ticket == openTicket) streamsOpen = true;
            }
        }
        finally
        {
            lock (sync)
            {
                if (ticket == openTicket) opening = null;
            }
            gate.Release();
        }
    }

    private async Task IdleAsync(int token, TimeSpan delay, CancellationTokenSource timer)
    {
        var cancelled = false;
        try { await Task.Delay(delay, timer.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { cancelled = true; }
        lock (sync)
        {
            if (ReferenceEquals(idle, timer)) idle = null;
        }
        try
        {
            if (!cancelled) await CloseCoreAsync(token).ConfigureAwait(false);
        }
        finally { timer.Dispose(); }
    }

    private async Task CloseCoreAsync(int token)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sync)
            {
                if (viewers > 0 || token != epoch || !streamsOpen) return;
                // Cleared before releasing the lock so a reconnect cannot observe a live pipeline and then lose it.
                streamsOpen = false;
            }
            await TearDownAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task TearDownAsync()
    {
        int closed;
        try { closed = await close(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or DesktopSessionClosedException)
        {
            return;
        }
        Action<string>? sink;
        lock (sync)
        {
            opening = null;
            if (viewers > 0) return;
            sink = report;
        }
        if (closed > 0)
            sink?.Invoke("capture-idle-closed;screens=" + closed.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void CancelIdle()
    {
        if (idle is null) return;
        try { idle.Cancel(); } catch (ObjectDisposedException) { }
        idle = null;
    }

    private static string Sanitize(string reason)
    {
        if (reason.Length is 0 or > 32) return "viewer-requested";
        foreach (var character in reason)
            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                return "viewer-requested";
        return reason;
    }
}
