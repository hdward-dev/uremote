using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace URemote.Linux;

public sealed class X11Clipboard : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentQueue<Action> commands = new();
    private readonly Task pump;
    private IntPtr display;
    private nuint window, clipboard, utf8, targets, textAtom, latin1, incr, property;
    private byte[]? owned;
    private TaskCompletionSource<string?>? pendingRead;
    private int failed;

    public X11Clipboard() => pump = Task.Run(Pump);

    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref failed) != 0) return null;
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Enqueue(() => BeginRead(done));
        return await WaitAsync(done, ct).ConfigureAwait(false);
    }

    public async Task WriteAsync(string text, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(text) > 65536 || text.Contains('\0'))
            throw new ArgumentException("Clipboard text rejected.");
        if (Volatile.Read(ref failed) != 0) throw new IOException("X11 剪贴板不可用。");
        var bytes = Encoding.UTF8.GetBytes(text);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Enqueue(() => BeginWrite(bytes, done));
        try { await WaitAsync(done, ct).ConfigureAwait(false); }
        catch { Array.Clear(bytes); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await pump.ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or OperationCanceledException or DllNotFoundException) { }
    }

    private async Task<T> WaitAsync<T>(TaskCompletionSource<T> done, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try { return await done.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (default(T) is null) return default!;
            throw new IOException("X11 剪贴板超时。");
        }
    }

    private void Pump()
    {
        try
        {
            X11Native.Prepare();
            display = X11Native.Open(null);
            if (display == IntPtr.Zero) { Volatile.Write(ref failed, 1); return; }
            X11Native.Own(display);
            var screen = X11Native.XDefaultScreen(display);
            window = X11Native.XCreateSimpleWindow(display, X11Native.XRootWindow(display, screen), -10, -10, 1, 1, 0, 0, 0);
            clipboard = Atom("CLIPBOARD");
            utf8 = Atom("UTF8_STRING");
            targets = Atom("TARGETS");
            textAtom = Atom("TEXT");
            latin1 = Atom("STRING");
            incr = Atom("INCR");
            property = Atom("UREMOTE_CLIP");
            var fd = X11Native.XConnectionNumber(display);
            var buffer = Marshal.AllocHGlobal(192);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    while (commands.TryDequeue(out var command)) command();
                    Drain(buffer);
                    var poll = new X11Native.PollFd { Fd = fd, Events = X11Native.PollIn };
                    X11Native.poll(ref poll, 1, 100);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch (Exception e) when (e is IOException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or NotSupportedException or ObjectDisposedException)
        { Volatile.Write(ref failed, 1); pendingRead?.TrySetResult(null); }
        finally
        {
            if (display != IntPtr.Zero)
            {
                if (window != 0) X11Native.XDestroyWindow(display, window);
                X11Native.XCloseDisplay(display);
                X11Native.Release(display);
                display = IntPtr.Zero;
            }
            if (owned is not null) Array.Clear(owned);
            owned = null;
        }
    }

    private nuint Atom(string name) => X11Native.XInternAtom(display, name, 0);

    private void BeginRead(TaskCompletionSource<string?> done)
    {
        if (Volatile.Read(ref failed) != 0 || display == IntPtr.Zero) { done.TrySetResult(null); return; }
        if (X11Native.XGetSelectionOwner(display, clipboard) == window && owned is not null)
        {
            done.TrySetResult(Decode(owned));
            return;
        }
        pendingRead = done;
        X11Native.XConvertSelection(display, clipboard, utf8, property, window, 0);
        X11Native.XFlush(display);
    }

    private void BeginWrite(byte[] bytes, TaskCompletionSource<bool> done)
    {
        try
        {
            if (Volatile.Read(ref failed) != 0 || display == IntPtr.Zero) throw new IOException("X11 剪贴板不可用。");
            if (owned is not null) Array.Clear(owned);
            owned = bytes;
            X11Native.XSetSelectionOwner(display, clipboard, window, 0);
            X11Native.XFlush(display);
            if (X11Native.XGetSelectionOwner(display, clipboard) != window)
                throw new IOException("无法取得 X11 剪贴板。");
            done.TrySetResult(true);
        }
        catch (Exception e)
        {
            done.TrySetException(e is IOException ? e : new IOException("X11 剪贴板写入失败。", e));
        }
    }

    private void Drain(IntPtr buffer)
    {
        while (display != IntPtr.Zero && X11Native.XPending(display) > 0)
        {
            X11Native.XNextEvent(display, buffer);
            var type = Marshal.ReadInt32(buffer);
            if (type == 29) // SelectionClear
            {
                if (ReadId(buffer, 40) == clipboard && owned is not null) { Array.Clear(owned); owned = null; }
            }
            else if (type == 30) Serve(buffer); // SelectionRequest
            else if (type == 31) FinishRead(buffer); // SelectionNotify
        }
    }

    private void Serve(IntPtr request)
    {
        var requestor = ReadId(request, 40);
        var selection = ReadId(request, 48);
        var target = ReadId(request, 56);
        var destination = ReadId(request, 64);
        nuint answered = 0;
        if (selection == clipboard && requestor != 0 && destination != 0)
        {
            if (target == targets)
            {
                WriteAtoms(requestor, destination, utf8, textAtom, latin1, targets);
                answered = destination;
            }
            else if ((target == utf8 || target == textAtom || target == latin1) && owned is not null)
            {
                X11Native.XChangeProperty(display, requestor, destination, target, 8, X11Native.PropModeReplace, owned, owned.Length);
                answered = destination;
            }
        }
        var notify = Marshal.AllocHGlobal(192);
        try
        {
            for (var i = 0; i < 192; i++) Marshal.WriteByte(notify, i, 0);
            Marshal.WriteInt32(notify, 31);
            WriteId(notify, 32, requestor);
            WriteId(notify, 40, selection);
            WriteId(notify, 48, target);
            WriteId(notify, 56, answered);
            X11Native.XSendEvent(display, requestor, 0, 0, notify);
            X11Native.XFlush(display);
        }
        finally { Marshal.FreeHGlobal(notify); }
    }

    private void WriteAtoms(nuint requestor, nuint destination, params nuint[] atoms)
    {
        var data = new byte[atoms.Length * 8];
        for (var i = 0; i < atoms.Length; i++) BitConverter.GetBytes((uint)atoms[i]).CopyTo(data, i * 8);
        X11Native.XChangeProperty(display, requestor, destination, targets, 32, X11Native.PropModeReplace, data, atoms.Length);
    }

    private void FinishRead(IntPtr notify)
    {
        var done = pendingRead;
        pendingRead = null;
        if (done is null) return;
        var propertyId = ReadId(notify, 56);
        if (propertyId == 0) { done.TrySetResult(null); return; }
        done.TrySetResult(ReadProperty(propertyId));
    }

    private string? ReadProperty(nuint propertyId)
    {
        if (X11Native.XGetWindowProperty(display, window, propertyId, 0, 16384, 1, 0, out var type, out var format, out var count, out var rest, out var data) != 0)
            return null;
        try
        {
            if (data == IntPtr.Zero || type == incr || format != 8 || rest != 0 || count == 0 || count > 65536) return null;
            var bytes = new byte[(int)count];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            try { return Decode(bytes); }
            finally { Array.Clear(bytes); }
        }
        finally { if (data != IntPtr.Zero) X11Native.XFree(data); }
    }

    private static string? Decode(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    private static nuint ReadId(IntPtr buffer, int offset) => unchecked((nuint)Marshal.ReadInt64(buffer, offset));

    private static void WriteId(IntPtr buffer, int offset, nuint value) => Marshal.WriteInt64(buffer, offset, unchecked((long)value));
}
