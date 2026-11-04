using System.Diagnostics;
using System.Text;

namespace URemote.Linux;

/// <summary>A current-user Windows console. No input or output is logged or persisted.</summary>
public sealed class WindowsTerminal : IAsyncDisposable
{
    private readonly Process process;
    private readonly SemaphoreSlim writer = new(1);
    private readonly StreamWriter input;
    private bool disposed;
    public int? ExitCode { get; private set; }

    public WindowsTerminal(int columns = 80, int rows = 24)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (columns is < 20 or > 500 || rows is < 5 or > 200) throw new ArgumentOutOfRangeException(nameof(columns));
        var shell = Environment.GetEnvironmentVariable("COMSPEC");
        if (string.IsNullOrEmpty(shell) || !File.Exists(shell)) shell = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var start = new ProcessStartInfo(shell)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        process = Process.Start(start) ?? throw new IOException("无法启动 Windows 终端。");
        URemote.Core.HiddenConsole.Hide(process);
        input = process.StandardInput;
        input.AutoFlush = true;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await writer.WaitAsync(ct);
        try { await input.WriteAsync(Encoding.UTF8.GetString(bytes.Span).AsMemory(), ct); }
        finally { writer.Release(); }
    }

    public async Task ReadAsync(Func<ReadOnlyMemory<byte>, Task> output, CancellationToken ct)
    {
        var buffer = new byte[16384];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var count = await process.StandardOutput.BaseStream.ReadAsync(buffer, ct);
                if (count == 0) { if (process.HasExited) ExitCode = process.ExitCode; return; }
                await output(buffer.AsMemory(0, count));
                Array.Clear(buffer, 0, count);
            }
        }
        finally { Array.Clear(buffer); }
    }
    public void Signal(int signal)
    {
        if (signal is not (1 or 2 or 9 or 15)) throw new ArgumentOutOfRangeException(nameof(signal));
        if (signal == 2) { try { process.StandardInput.Write("\u0003"); } catch (IOException) { } return; }
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
    }

    public void Resize(int columns, int rows)
    {
        if (columns is < 20 or > 500 || rows is < 5 or > 200) throw new ArgumentOutOfRangeException(nameof(columns));
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        process.Dispose();
        writer.Dispose();
        await Task.CompletedTask;
    }
}
