using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using URemote.Core;

namespace URemote.Linux;

// Portal sessions call ConnectToEIS, after which NotifyKeyboardKeysym is rejected.
// KDE often has no EI_DEVICE_CAP_TEXT, and kscreenlocker ignores EIS text the same
// way it ignores portal keys. Keysyms go through KWin's fake-input keyboard, which
// the lock filter still delivers to the greeter. Clipboard paste is the last resort.
internal enum CommittedTextRoute { Eis, Keysym, VirtualKeyboard, Paste }

internal static class CommittedText
{
    internal const int EisBytes = 254;

    internal static CommittedTextRoute Select(bool? locked, bool eisText, bool keysym, bool virtualKeyboard)
    {
        // EIS text is skipped unless the session is known to be unlocked.
        if (locked == false && eisText) return CommittedTextRoute.Eis;
        if (keysym) return CommittedTextRoute.Keysym;
        if (locked != true && virtualKeyboard) return CommittedTextRoute.VirtualKeyboard;
        return CommittedTextRoute.Paste;
    }

    internal static string RouteName(CommittedTextRoute route) => route switch
    {
        CommittedTextRoute.Eis => "eis",
        CommittedTextRoute.Keysym => "kwin-keysym",
        CommittedTextRoute.VirtualKeyboard => "virtual-keyboard",
        _ => "paste"
    };

    internal static string Normalize(string text)
    {
        HostControlInput.ValidateText(text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
    }

    internal static uint Keysym(Rune rune) => rune.Value switch
    {
        10 or 13 => 0xff0du, // XK_Return
        9 => 0xff09u, // XK_Tab
        <= 255 => (uint)rune.Value,
        _ => 0x01000000u | (uint)rune.Value
    };

    internal static uint[] Keysyms(string text) => Normalize(text).EnumerateRunes().Select(Keysym).ToArray();

    internal static string[] EisChunks(string text)
    {
        var normalized = Normalize(text);
        var chunks = new List<string>();
        var current = new StringBuilder();
        var bytes = 0;
        foreach (var rune in normalized.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (size > EisBytes) throw new FormatException("Committed text exceeds limit.");
            if (bytes + size > EisBytes)
            {
                chunks.Add(current.ToString());
                current.Clear();
                bytes = 0;
            }
            current.Append(rune);
            bytes += size;
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks.ToArray();
    }
}

internal static class CommittedTextInjector
{
    private static string? lastRoute;

    internal static void ResetRouteForTests() => lastRoute = null;

    // Order matches CommittedText.Select. Keysym availability is discovered by trying the protocol.
    internal static async Task InjectPortalAsync(PortalDesktopSession session, string text, CancellationToken ct)
    {
        HostControlInput.ValidateText(text);
        ct.ThrowIfCancellationRequested();
        if (SessionLock.IsLocked == false && session.SupportsCommittedText)
        {
            await session.TypeTextAsync(text, ct).ConfigureAwait(false);
            Note(CommittedTextRoute.Eis);
            return;
        }
        try
        {
            if (await KWinFakeInput.TryTypeAsync(text, ct).ConfigureAwait(false))
            {
                Note(CommittedTextRoute.Keysym);
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or InvalidOperationException or SocketException or TimeoutException)
        { throw Unavailable(e); }
        if (SessionLock.IsLocked != true)
        {
            try
            {
                await WaylandVirtualKeyboard.TypeTextAsync(text, ct).ConfigureAwait(false);
                Note(CommittedTextRoute.VirtualKeyboard);
                return;
            }
            catch (NotSupportedException) { }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is IOException or PlatformNotSupportedException or InvalidOperationException
                or SocketException or OperationCanceledException or FileNotFoundException or TimeoutException)
            { throw Unavailable(e); }
        }
        try
        {
            await CommittedTextPaste.InjectAsync(text, ct).ConfigureAwait(false);
            Note(CommittedTextRoute.Paste);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (NotSupportedException) { throw; }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException or InvalidOperationException
            or UnauthorizedAccessException or FileNotFoundException or SocketException or TimeoutException or ArgumentException)
        { throw Unavailable(e); }
    }

    private static void Note(CommittedTextRoute route)
    {
        var name = CommittedText.RouteName(route);
        if (Interlocked.Exchange(ref lastRoute, name) == name) return;
        Console.WriteLine("committed-text-route=" + name);
    }

    private static NotSupportedException Unavailable(Exception cause)
        => new("当前桌面未提供文字输入，请使用被控端输入法或复制粘贴。", cause);
}

internal static class KWinFakeInput
{
    internal const string Interface = "org_kde_kwin_fake_input";
    internal const uint MinimumVersion = 6;
    internal const ushort AuthenticateOpcode = 0;
    internal const ushort KeysymOpcode = 12;
    internal const uint Pressed = 1;
    internal const uint Released = 0;

    internal static byte[] WaylandString(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("Wayland string rejected.");
        var raw = Encoding.UTF8.GetBytes(value + "\0");
        var padded = (raw.Length + 3) & ~3;
        var payload = new byte[4 + padded];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)raw.Length);
        raw.CopyTo(payload, 4);
        return payload;
    }

    internal static byte[] AuthenticatePayload(string application, string reason)
    {
        var left = WaylandString(application);
        var right = WaylandString(reason);
        var payload = new byte[left.Length + right.Length];
        left.CopyTo(payload, 0);
        right.CopyTo(payload, left.Length);
        return payload;
    }

    // Returns false when this compositor has no keysym request. A failure after keys were sent is not retried.
    internal static async Task<bool> TryTypeAsync(string text, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var token = timeout.Token;
        WaylandConnection connection;
        try { connection = await WaylandConnection.OpenAsync(token).ConfigureAwait(false); }
        catch (Exception e) when (!ct.IsCancellationRequested && e is InvalidOperationException or IOException
            or PlatformNotSupportedException or SocketException or OperationCanceledException or TimeoutException)
        { return false; }
        using (connection)
        {
            var global = connection.Globals.FirstOrDefault(g => g.Interface == Interface);
            if (global is not { Version: >= MinimumVersion }) return false;
            var sent = false;
            try
            {
                var id = await connection.BindAsync(global, MinimumVersion, token).ConfigureAwait(false);
                await connection.SendAsync(id, AuthenticateOpcode, AuthenticatePayload("AsterDock", "remote committed text"), token).ConfigureAwait(false);
                await connection.RoundtripAsync(token).ConfigureAwait(false);
                foreach (var keysym in CommittedText.Keysyms(text))
                {
                    token.ThrowIfCancellationRequested();
                    await connection.SendAsync(id, KeysymOpcode, WaylandConnection.Words(keysym, Pressed), token).ConfigureAwait(false);
                    await connection.SendAsync(id, KeysymOpcode, WaylandConnection.Words(keysym, Released), token).ConfigureAwait(false);
                    sent = true;
                }
                try { await connection.RoundtripAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (!sent && e is IOException or InvalidOperationException or OperationCanceledException
                or SocketException or FormatException or ArgumentException or TimeoutException)
            { return false; }
        }
    }
}

internal static class CommittedTextPaste
{
    // Mac virtual keys. UInputKeyboard maps them to evdev Left Ctrl and V.
    internal static readonly HostKeyMessage[] CtrlV =
    [
        new(KeyAction.Press, 59),
        new(KeyAction.Press, 9),
        new(KeyAction.Release, 9),
        new(KeyAction.Release, 59)
    ];

    internal static async Task InjectAsync(string text, CancellationToken ct)
    {
        var normalized = CommittedText.Normalize(text);
        var copy = Tool("wl-copy");
        var paste = Tool("wl-paste");
        var previous = await CaptureAsync(paste, ct).ConfigureAwait(false);
        var wrote = false;
        try
        {
            await WriteAsync(copy, normalized, ct).ConfigureAwait(false);
            wrote = true;
            await Task.Delay(80, ct).ConfigureAwait(false);
            await using var keyboard = await UInputKeyboard.CreateAsync(ct).ConfigureAwait(false);
            foreach (var key in CtrlV)
                await keyboard.ApplyAsync(key, ct).ConfigureAwait(false);
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        finally
        {
            if (wrote)
            {
                try
                {
                    if (previous is null) await ClearAsync(copy, CancellationToken.None).ConfigureAwait(false);
                    else await WriteAsync(copy, previous, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or FileNotFoundException or ArgumentException or OperationCanceledException) { }
            }
        }
    }

    private static async Task<string?> CaptureAsync(string paste, CancellationToken ct)
    {
        var (code, types) = await RunAsync(paste, ["--list-types"], null, ct).ConfigureAwait(false);
        if (code != 0 || string.IsNullOrWhiteSpace(types)) return null;
        var offered = types.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!offered.Any(IsText))
            throw new NotSupportedException("剪贴板里已有非文本内容，已取消粘贴回退。");
        var (read, text) = await RunAsync(paste, ["--type", "text/plain"], null, ct).ConfigureAwait(false);
        if (read != 0 || text is null) throw new IOException("无法读取原剪贴板。");
        if (Encoding.UTF8.GetByteCount(text) > 65536) throw new NotSupportedException("原剪贴板过大，已取消粘贴回退。");
        return text;
    }

    private static bool IsText(string type) =>
        type.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)
        || type.Equals("UTF8_STRING", StringComparison.OrdinalIgnoreCase)
        || type.Equals("TEXT", StringComparison.OrdinalIgnoreCase)
        || type.Equals("STRING", StringComparison.OrdinalIgnoreCase);

    private static Task ClearAsync(string copy, CancellationToken ct) => RunCheckedAsync(copy, ["--clear"], null, ct);
    private static Task WriteAsync(string copy, string text, CancellationToken ct)
        => RunCheckedAsync(copy, [], Encoding.UTF8.GetBytes(text), ct);

    private static async Task RunCheckedAsync(string exe, string[] args, byte[]? stdin, CancellationToken ct)
    {
        var (code, _) = await RunAsync(exe, args, stdin, ct).ConfigureAwait(false);
        if (code != 0) throw new IOException("剪贴板写入失败。");
    }

    private static string Tool(string name)
    {
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, name)).FirstOrDefault(File.Exists);
        return path ?? throw new FileNotFoundException("需要 wl-clipboard 才能回退到粘贴。", name);
    }

    private static async Task<(int Code, string? Output)> RunAsync(string exe, string[] args, byte[]? stdin, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardInput = stdin is not null, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法启动剪贴板工具。");
        using var cancel = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var errors = process.StandardError.ReadToEndAsync();
        var buffer = new byte[65537];
        try
        {
            if (stdin is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(stdin, timeout.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            var read = 0;
            var stdout = process.StandardOutput.BaseStream;
            while (read < buffer.Length)
            {
                var count = await stdout.ReadAsync(buffer.AsMemory(read), timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                read += count;
            }
            if (read == buffer.Length)
            {
                try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
                return (1, null);
            }
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) return (process.ExitCode, null);
            try { return (0, new UTF8Encoding(false, true).GetString(buffer, 0, read)); }
            catch (DecoderFallbackException) { throw new IOException("无法读取原剪贴板。"); }
        }
        finally
        {
            Array.Clear(buffer);
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            try { await errors.ConfigureAwait(false); } catch (Exception) { }
            if (stdin is not null) Array.Clear(stdin);
        }
    }
}
