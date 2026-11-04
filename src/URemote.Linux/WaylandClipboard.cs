using System.Diagnostics;
using System.Text;
namespace URemote.Linux;

public sealed class WaylandClipboard : IAsyncDisposable
{
    private readonly string paste, copy;
    public WaylandClipboard() : this(OperatingSystem.IsMacOS() ? "/usr/bin/pbpaste" : "wl-paste", OperatingSystem.IsMacOS() ? "/usr/bin/pbcopy" : "wl-copy") { }
    internal WaylandClipboard(string paste, string copy) { this.paste = paste; this.copy = copy; }
    private Process? owner;
    private Task<string>? ownerError;
    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var process = Process.Start(OperatingSystem.IsMacOS() ? Start(paste, false) : Start(paste, false, "--no-newline", "--type", "text")) ?? throw new IOException();
        using var cancel = timeout.Token.Register(() => Kill(process));
        var errors = process.StandardError.ReadToEndAsync();
        using var bytes = new MemoryStream(); var buffer = new byte[4096];
        try
        {
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (bytes.Length + count > 65536) return null;
                bytes.Write(buffer, 0, count);
            }
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) return null;
            return new UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
        }
        finally { Kill(process); await process.WaitForExitAsync(); await errors; Array.Clear(buffer); Array.Clear(bytes.GetBuffer()); }
    }
    public async Task WriteAsync(string text, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(text) > 65536 || text.Contains('\0')) throw new ArgumentException("Clipboard text rejected.");
        await StopOwnerAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        owner = Process.Start(OperatingSystem.IsMacOS() ? Start(copy, true) : Start(copy, true, "--foreground", "--type", "text/plain;charset=utf-8")) ?? throw new IOException();
        ownerError = owner.StandardError.ReadToEndAsync();
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            await owner.StandardInput.BaseStream.WriteAsync(bytes, timeout.Token); owner.StandardInput.Close();
            if (OperatingSystem.IsMacOS())
            {
                await owner.WaitForExitAsync(timeout.Token);
                if (owner.ExitCode != 0) throw new IOException("macOS 剪贴板写入失败。");
            }
            else
            {
                // Foreground wl-copy stays alive after acquiring ownership. Its
                // stdin closing alone does not prove the compositor accepted it.
                while (true)
                {
                    if (owner.HasExited) throw new IOException("剪贴板写入进程已退出。");
                    if (await ReadAsync(timeout.Token) == text && !owner.HasExited) break;
                    await Task.Delay(25, timeout.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { await StopOwnerAsync(); throw new IOException("等待剪贴板所有权超时。"); }
        catch (DecoderFallbackException e)
        { await StopOwnerAsync(); throw new IOException("无法确认剪贴板写入结果。", e); }
        catch { await StopOwnerAsync(); throw; }
        finally { Array.Clear(bytes); }
    }

    private static ProcessStartInfo Start(string exe, bool input, params string[] args)
    {
        var path = Path.IsPathFullyQualified(exe) && File.Exists(exe) ? exe : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(p => Path.Combine(p, exe)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Install wl-clipboard for clipboard synchronization.");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardInput = input, RedirectStandardOutput = !input, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg); return start;
    }
    private static void Kill(Process p) { try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { } }
    private async Task StopOwnerAsync() { if (owner is null) return; Kill(owner); await owner.WaitForExitAsync(); if (ownerError is not null) await ownerError; owner.Dispose(); owner = null; }
    public async ValueTask DisposeAsync() => await StopOwnerAsync();
}
