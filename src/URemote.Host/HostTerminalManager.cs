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
        var session = new Session(++sequence, new LinuxTerminal(columns, rows));
        sessions.Add(session.Id, session); return session;
    }
    public Session? Find(uint id) => sessions.GetValueOrDefault(id);
    public byte[] List() => JsonSerializer.SerializeToUtf8Bytes(sessions.Values.Select(s => new
    { session_id = s.Id, name = "终端 " + s.Id, state = s.Exited ? "exited" : "running", created_at_ms = s.CreatedAt, last_active_ms = s.CreatedAt, shell = "bash" }).ToArray());
    public async Task CloseAsync(uint id)
    { if (sessions.Remove(id, out var session)) await session.DisposeAsync(); }
    public async ValueTask DisposeAsync()
    { foreach (var session in sessions.Values) await session.DisposeAsync(); sessions.Clear(); }

    public sealed class Session(uint id, LinuxTerminal terminal) : IAsyncDisposable
    {
        private readonly object gate = new();
        private readonly Queue<byte[]> history = new();
        private int historyBytes;
        private Action<byte, uint, byte[]>? sink;
        private readonly CancellationTokenSource stop = new();
        private Task? pump;
        public uint Id { get; } = id;
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
        public Task WriteAsync(byte[] bytes, CancellationToken ct) => terminal.WriteAsync(bytes, ct);
        public void Resize(int columns, int rows) => terminal.Resize(columns, rows);
        public void Signal(int signal) => terminal.Signal(signal);
        private async Task PumpAsync()
        {
            try
            {
                await terminal.ReadAsync(bytes =>
                {
                    lock (gate)
                    {
                        var copy = bytes.ToArray(); history.Enqueue(copy); historyBytes += copy.Length;
                        while (historyBytes > 65536 && history.TryDequeue(out var old)) { historyBytes -= old.Length; Array.Clear(old); }
                        try { sink?.Invoke(5, Id, copy); } catch { sink = null; }
                    }
                    return Task.CompletedTask;
                }, stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (IOException) { }
            finally
            {
                await terminal.DisposeAsync(); Exited = true;
                var payload = JsonSerializer.SerializeToUtf8Bytes(new { exit_code = terminal.ExitCode ?? 0 });
                lock (gate) { try { sink?.Invoke(6, Id, payload); } catch { } }
                Array.Clear(payload);
            }
        }
        public async ValueTask DisposeAsync()
        {
            Detach(); await stop.CancelAsync(); await terminal.DisposeAsync();
            if (pump is not null) await pump;
            lock (gate) { foreach (var bytes in history) Array.Clear(bytes); history.Clear(); }
            stop.Dispose();
        }
    }
}
