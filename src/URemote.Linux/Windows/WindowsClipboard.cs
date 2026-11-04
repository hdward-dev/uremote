using System.Runtime.InteropServices;
using System.Text;

namespace URemote.Linux;

public sealed class WindowsClipboard : IAsyncDisposable
{
    public Task<string?> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OpenClipboard(0)) return Task.FromResult<string?>(null);
        try
        {
            var handle = GetClipboardData(13);
            if (handle == 0) return Task.FromResult<string?>(null);
            var pointer = GlobalLock(handle);
            if (pointer == 0) return Task.FromResult<string?>(null);
            try { return Task.FromResult<string?>(Marshal.PtrToStringUni(pointer)); }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    public Task WriteAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        var memory = GlobalAlloc(0x0002, (nuint)bytes.Length);
        if (memory == 0) throw new IOException("无法写入剪贴板。");
        var pointer = GlobalLock(memory);
        if (pointer == 0) { GlobalFree(memory); throw new IOException("无法写入剪贴板。"); }
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        GlobalUnlock(memory);
        if (!OpenClipboard(0)) { GlobalFree(memory); throw new IOException("无法打开剪贴板。"); }
        try
        {
            EmptyClipboard();
            if (SetClipboardData(13, memory) == 0) { GlobalFree(memory); throw new IOException("无法写入剪贴板。"); }
            memory = 0;
        }
        finally { CloseClipboard(); if (memory != 0) GlobalFree(memory); }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
}
