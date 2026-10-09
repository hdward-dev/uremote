using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace URemote.Linux;

internal sealed class PortalBus : IAsyncDisposable
{
    internal const string InterfacePrefix = "org.freedesktop.portal";
    internal const string Destination = InterfacePrefix + ".Desktop", Desktop = "/org/freedesktop/portal/desktop";
    internal const string ScreenCast = InterfacePrefix + ".ScreenCast", RemoteDesktop = InterfacePrefix + ".RemoteDesktop";
    private readonly PortalDispatcher dispatcher = new();
    private readonly Dictionary<uint, NativeGlib.Signal> callbacks = [];
    private nint connection;
    internal string SessionPath(string token) => Desktop + "/session/" +
        Marshal.PtrToStringUTF8(NativeGlib.g_dbus_connection_get_unique_name(connection))!.TrimStart(':').Replace('.', '_') + "/" + token;
    internal bool IsClosed => connection == 0 || NativeGlib.g_dbus_connection_is_closed(connection) != 0;
    internal static async Task<PortalBus> ConnectAsync(CancellationToken ct)
    {
        var bus = new PortalBus();
        try
        {
            bus.connection = await Task.Run(() => WithCancellation(ct, cancel =>
            {
                var ptr = NativeGlib.g_bus_get_sync(2, cancel, out var error);
                NativeGlib.Check(error, "连接 D-Bus", ct);
                if (ptr == 0) throw new IOException("无法连接桌面 D-Bus。");
                return ptr;
            }), ct);
            return bus;
        }
        catch { await bus.DisposeAsync(); throw; }
    }
    private static T WithCancellation<T>(CancellationToken ct, Func<nint, T> action)
    {
        ct.ThrowIfCancellationRequested();
        var cancel = NativeGlib.g_cancellable_new();
        try { using var registration = ct.Register(() => NativeGlib.g_cancellable_cancel(cancel)); return action(cancel); }
        finally { NativeGlib.g_object_unref(cancel); }
    }
    internal Task<PortalValue> CallAsync(string iface, string method, PortalValue args, CancellationToken ct, string path = Desktop)
        => Task.Run(() => WithCancellation(ct, cancel =>
        {
            var value = NativeGlib.g_dbus_connection_call_sync(connection, Destination, path, iface, method, args.Handle, 0, 0, 10000, cancel, out var error);
            NativeGlib.Check(error, method, ct);
            return new PortalValue(value);
        }), ct);
    internal async Task<uint> PropertyAsync(string iface, string name, CancellationToken ct)
    {
        using var i = PortalValue.String(iface); using var n = PortalValue.String(name); using var args = PortalValue.Tuple(i, n);
        using var result = await CallAsync("org.freedesktop.DBus.Properties", "Get", args, ct);
        using var field = result.Child(0); using var value = field.Unbox(); return value.UInt();
    }
    internal async Task<uint> SubscribeAsync(string iface, string member, string path, Action<PortalValue> onSignal)
    {
        return await dispatcher.InvokeAsync(() =>
        {
            NativeGlib.Signal callback = (_, _, _, _, _, args, _) =>
            {
                // GIO owns args for the duration of this callback. Retain one reference while reading it.
                using var value = new PortalValue(NativeGlib.g_variant_ref(args));
                try { onSignal(value); } catch { /* Never unwind through native callbacks. */ }
            };
            var id = NativeGlib.g_dbus_connection_signal_subscribe(connection, Destination, iface, member, path, 0, 0, callback, 0, 0);
            if (id == 0) throw new IOException("无法订阅桌面授权状态。");
            callbacks.Add(id, callback);
            return id;
        });
    }
    internal async Task UnsubscribeAsync(uint id)
    {
        await dispatcher.InvokeAsync(() =>
        {
            NativeGlib.g_dbus_connection_signal_unsubscribe(connection, id);
            callbacks.Remove(id); return 0;
        });
    }
    // Subscribe before issuing the request so a fast Response cannot be lost.
    internal async Task<PortalValue> RequestAsync(string iface, string method, string? session, bool start,
        IReadOnlyList<(string Key, PortalValue Value)> options, CancellationToken ct)
    {
        var token = "u" + Guid.NewGuid().ToString("N");
        var sender = Marshal.PtrToStringUTF8(NativeGlib.g_dbus_connection_get_unique_name(connection))!;
        var path = Desktop + "/request/" + sender.TrimStart(':').Replace('.', '_') + "/" + token;
        var completion = new TaskCompletionSource<PortalValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = await SubscribeAsync(InterfacePrefix + ".Request", "Response", path, value =>
        {
            try
            {
                value.Require("(ua{sv})");
                using var code = value.Child(0);
                if (code.UInt() != 0) { completion.TrySetException(new InvalidOperationException("桌面共享授权已取消或被拒绝。")); return; }
                var result = value.Child(1);
                if (!completion.TrySetResult(result)) result.Dispose();
            }
            catch (Exception e) { completion.TrySetException(e); }
        });
        var delivered = false;
        try
        {
            using var handleToken = PortalValue.String(token);
            using var opts = PortalValue.Dict([..options, ("handle_token", handleToken)]);
            using var sessionValue = session is null ? null : PortalValue.Path(session);
            using var parent = PortalValue.String("");
            using var args = sessionValue is null ? PortalValue.Tuple(opts)
                : start ? PortalValue.Tuple(sessionValue, parent, opts) : PortalValue.Tuple(sessionValue, opts);
            using var reply = await CallAsync(iface, method, args, ct);
            using var returned = reply.Child(0);
            if (returned.Text() != path) throw new FormatException("Portal 未返回预期的授权请求路径。");
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(120), ct);
            delivered = true;
            return result;
        }
        catch
        {
            // Close the request on timeout/cancel, including cancellation while waiting for the method reply.
            try { using var args = PortalValue.Tuple(); using var close = await CallAsync(InterfacePrefix + ".Request", "Close", args, CancellationToken.None, path); }
            catch (IOException) { }
            throw;
        }
        finally
        {
            await UnsubscribeAsync(subscription);
            // A raced Response must not leak a GVariant when cancellation wins WaitAsync.
            if (!delivered && completion.Task.IsCompletedSuccessfully) completion.Task.Result.Dispose();
        }
    }
    internal Task<SafeFileHandle> OpenPipeWireAsync(string session, CancellationToken ct)
        => OpenDescriptorAsync(ScreenCast, "OpenPipeWireRemote", session, ct);
    internal Task<SafeFileHandle> OpenEisAsync(string session, CancellationToken ct)
        => OpenDescriptorAsync(RemoteDesktop, "ConnectToEIS", session, ct);
    private Task<SafeFileHandle> OpenDescriptorAsync(string iface, string method, string session, CancellationToken ct) => Task.Run(() => WithCancellation(ct, cancel =>
    {
        using var path = PortalValue.Path(session); using var options = PortalValue.Dict(); using var args = PortalValue.Tuple(path, options);
        var result = NativeGlib.g_dbus_connection_call_with_unix_fd_list_sync(connection, Destination, Desktop, iface, method, args.Handle, 0, 0, 10000, 0, out var fds, cancel, out var error);
        try
        {
            NativeGlib.Check(error, method, ct);
            using var value = new PortalValue(result); using var index = value.Child(0);
            if (fds == 0) throw new FormatException("Portal 未返回 Portal 文件描述符。");
            var fd = NativeGlib.g_unix_fd_list_get(fds, index.FdIndex(), out var fdError);
            NativeGlib.Check(fdError, "读取 Portal 文件描述符", ct);
            if (fd < 0) throw new IOException("无效的 Portal 文件描述符。");
            return new SafeFileHandle(fd, ownsHandle: true);
        }
        finally { if (fds != 0) NativeGlib.g_object_unref(fds); }
    }), ct);
    public async ValueTask DisposeAsync()
    {
        foreach (var id in callbacks.Keys.ToArray()) await UnsubscribeAsync(id);
        if (connection != 0) { NativeGlib.g_object_unref(connection); connection = 0; }
        await dispatcher.DisposeAsync();
    }
}
