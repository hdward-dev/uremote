using System.Text.Json;
using URemote.Core;
using URemote.Linux;

// Owned by the local hosting lifetime, not by an individual remote connection.
public sealed class HostTerminalManager : IAsyncDisposable
{
    private readonly Dictionary<uint, Session> sessions = new();
    private uint sequence;
    public Session Create(int columns, int rows)
    {
        if (sessions.Count >= 8) throw new InvalidOperationException("Terminal session limit reached.");
        var session = OperatingSystem.IsWindows()
            ? new Session(++sequence, new WindowsTerminal(columns, rows))
            : new Session(++sequence, new LinuxTerminal(columns, rows));
        sessions.Add(session.Id, session); return session;
    }
    public Session? Find(uint id) => sessions.GetValueOrDefault(id);
    public byte[] List() => JsonSerializer.SerializeToUtf8Bytes(sessions.Values.Select(s => new
    { session_id = s.Id, name = "终端 " + s.Id, state = s.Exited ? "exited" : "running", created_at_ms = s.CreatedAt, last_active_ms = s.CreatedAt, shell = "bash" }).ToArray());
    public async Task CloseAsync(uint id)
    { if (sessions.Remove(id, out var session)) await session.DisposeAsync(); }
    public async ValueTask DisposeAsync()
    { foreach (var session in sessions.Values) await session.DisposeAsync(); sessions.Clear(); }

    public sealed class Session : IAsyncDisposable
    {
        private readonly LinuxTerminal? linux;
        private readonly WindowsTerminal? windows;
        public Session(uint id, LinuxTerminal terminal) { Id = id; linux = terminal; }
        public Session(uint id, WindowsTerminal terminal) { Id = id; windows = terminal; }
        private readonly object gate = new();
        private readonly Queue<byte[]> history = new();
        private int historyBytes;
        private Action<byte, uint, byte[]>? sink;
        private readonly CancellationTokenSource stop = new();
        private Task? pump;
        public uint Id { get; }
        public long CreatedAt { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public bool Exited { get; private set; }
        public void Attach(Action<byte, uint, byte[]> output)
        {
            lock (gate)
            {
                sink = output;
                foreach (var bytes in history) output(5, Id, bytes);
                pump ??= Task.Run(PumpAsync);
            }
        }
        public void Detach() { lock (gate) sink = null; }
        public Task WriteAsync(byte[] bytes, CancellationToken ct) => windows?.WriteAsync(bytes, ct) ?? linux!.WriteAsync(bytes, ct);
        public void Resize(int columns, int rows) { if (windows is not null) windows.Resize(columns, rows); else linux!.Resize(columns, rows); }
        public void Signal(int signal) { if (windows is not null) windows.Signal(signal); else linux!.Signal(signal); }
        private Task Deliver(ReadOnlyMemory<byte> bytes)
        {
            lock (gate)
            {
                var copy = bytes.ToArray(); history.Enqueue(copy); historyBytes += copy.Length;
                while (historyBytes > 65536 && history.TryDequeue(out var old)) { historyBytes -= old.Length; Array.Clear(old); }
                try { sink?.Invoke(5, Id, copy); } catch { sink = null; }
            }
            return Task.CompletedTask;
        }
        private async Task PumpAsync()
        {
            try { await (windows?.ReadAsync(Deliver, stop.Token) ?? linux!.ReadAsync(Deliver, stop.Token)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (IOException) { }
            finally
            {
                if (windows is not null) await windows.DisposeAsync(); else await linux!.DisposeAsync();
                Exited = true;
                var payload = JsonSerializer.SerializeToUtf8Bytes(new { exit_code = windows?.ExitCode ?? linux?.ExitCode ?? 0 });
                lock (gate) { try { sink?.Invoke(6, Id, payload); } catch { } }
                Array.Clear(payload);
            }
        }
        public async ValueTask DisposeAsync()
        {
            Detach(); await stop.CancelAsync();
            if (windows is not null) await windows.DisposeAsync(); else await linux!.DisposeAsync();
            if (pump is not null) await pump;
            lock (gate) { foreach (var bytes in history) Array.Clear(bytes); history.Clear(); }
            stop.Dispose();
        }
    }
}
