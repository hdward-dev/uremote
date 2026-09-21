using System.Collections;
using System.Runtime.InteropServices;
using System.Text;

namespace URemote.Linux;

/// <summary>A current-user Linux PTY. No input or output is logged or persisted.</summary>
public sealed class LinuxTerminal : IAsyncDisposable
{
    private int fd = -1, pid;
    private readonly object gate = new();
    private readonly SemaphoreSlim writer = new(1);
    private bool disposed, reaped;
    public int? ExitCode { get; private set; }

    public LinuxTerminal(int columns = 80, int rows = 24)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        ValidateSize(columns, rows);
        fd = posix_openpt(2 | 0x100 | 0x800 | 0x80000); // RDWR, NOCTTY, NONBLOCK, CLOEXEC
        if (fd < 0) throw Error();
        var actions = Marshal.AllocHGlobal(1024);
        var attributes = Marshal.AllocHGlobal(1024);
        var actionsReady = false; var attributesReady = false;
        try
        {
            if (grantpt(fd) != 0 || unlockpt(fd) != 0) throw Error();
            var slave = new StringBuilder(256); Check(ptsname_r(fd, slave, 256));
            Resize(columns, rows);
            Check(posix_spawn_file_actions_init(actions)); actionsReady = true;
            Check(posix_spawnattr_init(attributes)); attributesReady = true;
            // SETSID runs before opening the slave, making it the child's controlling terminal.
            Check(posix_spawnattr_setflags(attributes, 0x80));
            Check(posix_spawn_file_actions_addopen(actions, 0, slave.ToString(), 2, 0));
            Check(posix_spawn_file_actions_adddup2(actions, 0, 1));
            Check(posix_spawn_file_actions_adddup2(actions, 0, 2));
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (Directory.Exists(home)) Check(posix_spawn_file_actions_addchdir_np(actions, home));
            var shell = Environment.GetEnvironmentVariable("SHELL");
            if (string.IsNullOrEmpty(shell) || !Path.IsPathFullyQualified(shell) || !File.Exists(shell))
                shell = File.Exists("/run/current-system/sw/bin/bash") ? "/run/current-system/sw/bin/bash" : "/bin/sh";
            using var argv = new NativeStrings([shell, "-i"]);
            var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                .Where(x => x.Key.ToString() is not ("TERM" or "COLORTERM" or "HISTFILE"))
                .Select(x => x.Key + "=" + x.Value).ToList();
            environment.AddRange(["TERM=xterm-256color", "COLORTERM=truecolor", "HISTFILE=/dev/null"]);
            using var envp = new NativeStrings(environment);
            Check(posix_spawn(out pid, shell, actions, attributes, argv.Pointer, envp.Pointer));
        }
        catch { close(fd); fd = -1; throw; }
        finally
        {
            if (actionsReady) posix_spawn_file_actions_destroy(actions);
            if (attributesReady) posix_spawnattr_destroy(attributes);
            Marshal.FreeHGlobal(actions); Marshal.FreeHGlobal(attributes);
        }
    }

    public void Resize(int columns, int rows)
    {
        ValidateSize(columns, rows);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var size = new WindowSize { Rows = (ushort)rows, Columns = (ushort)columns };
            if (ioctl(fd, 0x5414, ref size) != 0) throw Error();
        }
    }

    public void Signal(int signal)
    {
        if (signal is not (1 or 2 or 9 or 15)) throw new ArgumentOutOfRangeException(nameof(signal));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Reap(); if (reaped) return;
            var foreground = tcgetpgrp(fd);
            var group = foreground > 0 && getsid(foreground) == pid ? foreground : pid;
            if (kill(-group, signal) != 0) throw Error();
        }
    }

    public async Task ReadAsync(Func<ReadOnlyMemory<byte>, Task> output, CancellationToken ct)
    {
        var buffer = new byte[16384];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int count, error;
                lock (gate)
                {
                    if (disposed) return;
                    count = (int)read(fd, buffer, (nuint)buffer.Length); error = Marshal.GetLastPInvokeError();
                    Reap();
                }
                if (count > 0) { await output(buffer.AsMemory(0, count)); Array.Clear(buffer, 0, count); continue; }
                if (count == 0 || count < 0 && error == 5) return; // Linux PTY EOF is EIO.
                if (error is not (4 or 11)) throw new IOException("Terminal read failed.");
                await Task.Delay(10, ct);
            }
        }
        finally { Array.Clear(buffer); }
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct)
    {
        if (input.Length > 65536) throw new ArgumentException("Terminal input exceeds limit.");
        await writer.WaitAsync(ct);
        var bytes = input.ToArray();
        try
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                ct.ThrowIfCancellationRequested();
                int count, error;
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                    try { count = (int)write(fd, pin.AddrOfPinnedObject() + offset, (nuint)(bytes.Length - offset)); error = Marshal.GetLastPInvokeError(); }
                    finally { pin.Free(); }
                }
                if (count > 0) offset += count;
                else if (count < 0 && error is not (4 or 11)) throw new IOException("Terminal write failed.");
                else await Task.Delay(10, ct);
            }
        }
        finally { Array.Clear(bytes); writer.Release(); }
    }

    private void Reap()
    {
        if (reaped || pid <= 0) return;
        if (waitpid(pid, out var status, 1) == pid)
        { reaped = true; ExitCode = (status & 0x7f) == 0 ? status >> 8 & 255 : 128 + (status & 0x7f); }
    }
    public async ValueTask DisposeAsync()
    {
        int foreground;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            // End both the foreground job and the shell; never signal unrelated sessions.
            foreground = tcgetpgrp(fd);
            if (foreground > 0) kill(-foreground, 1);
            Reap(); if (!reaped && pid > 0) kill(-pid, 1);
            close(fd); fd = -1;
        }
        for (var attempt = 0; attempt < 20; attempt++)
        { lock (gate) { Reap(); if (reaped) break; } await Task.Delay(25); }
        lock (gate)
        {
            if (foreground > 0 && getsid(foreground) == pid) kill(-foreground, 9);
            if (!reaped && pid > 0) { kill(-pid, 9); kill(pid, 9); }
        }
        await Task.Run(() => { lock (gate) { if (!reaped && pid > 0) { waitpid(pid, out _, 0); reaped = true; } } });
    }
    private static void ValidateSize(int columns, int rows)
    { if (columns is < 2 or > 1000 || rows is < 2 or > 1000) throw new ArgumentOutOfRangeException(nameof(columns)); }
    private static IOException Error() => new("Terminal system call failed: " + Marshal.GetLastPInvokeError());
    private static void Check(int code) { if (code != 0) throw new IOException("Terminal initialization failed: " + code); }
    [StructLayout(LayoutKind.Sequential)] private struct WindowSize { public ushort Rows, Columns, Width, Height; }
    private sealed class NativeStrings : IDisposable
    {
        private readonly nint[] strings;
        public nint Pointer { get; }
        public NativeStrings(IEnumerable<string> values)
        {
            strings = values.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
            Pointer = Marshal.AllocHGlobal((strings.Length + 1) * IntPtr.Size);
            for (var i = 0; i < strings.Length; i++) Marshal.WriteIntPtr(Pointer, i * IntPtr.Size, strings[i]);
            Marshal.WriteIntPtr(Pointer, strings.Length * IntPtr.Size, 0);
        }
        public void Dispose() { foreach (var value in strings) Marshal.FreeCoTaskMem(value); Marshal.FreeHGlobal(Pointer); }
    }
    [DllImport("libc", SetLastError = true)] private static extern int posix_openpt(int flags);
    [DllImport("libc", SetLastError = true)] private static extern int grantpt(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int unlockpt(int fd);
    [DllImport("libc")] private static extern int ptsname_r(int fd, StringBuilder buffer, nuint size);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_init(nint actions);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_destroy(nint actions);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_addopen(nint actions, int fd, string path, int flags, uint mode);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_adddup2(nint actions, int fd, int target);
    [DllImport("libc")] private static extern int posix_spawn_file_actions_addchdir_np(nint actions, string path);
    [DllImport("libc")] private static extern int posix_spawnattr_init(nint attributes);
    [DllImport("libc")] private static extern int posix_spawnattr_destroy(nint attributes);
    [DllImport("libc")] private static extern int posix_spawnattr_setflags(nint attributes, short flags);
    [DllImport("libc")] private static extern int posix_spawn(out int pid, string path, nint actions, nint attributes, nint argv, nint environment);
    [DllImport("libc", SetLastError = true)] private static extern nint read(int fd, byte[] buffer, nuint count);
    [DllImport("libc", SetLastError = true)] private static extern nint write(int fd, nint buffer, nuint count);
    [DllImport("libc", SetLastError = true)] private static extern int ioctl(int fd, nuint operation, ref WindowSize size);
    [DllImport("libc")] private static extern int close(int fd);
    [DllImport("libc")] private static extern int kill(int pid, int signal);
    [DllImport("libc")] private static extern int waitpid(int pid, out int status, int options);
    [DllImport("libc")] private static extern int tcgetpgrp(int fd);
    [DllImport("libc")] private static extern int getsid(int pid);
}
