using System.Runtime.InteropServices;

namespace URemote.Linux;

// Stable GLib/GIO C ABI. No GI typelibs, Python runtime or command-line D-Bus tools.
internal static class NativeGlib
{
    internal const string Glib = "libglib-2.0.so.0", Gio = "libgio-2.0.so.0", Object = "libgobject-2.0.so.0";
    [DllImport(Object)] internal static extern void g_object_unref(nint value);
    [DllImport(Glib)] internal static extern void g_error_free(nint error);
    [DllImport(Glib)] internal static extern nint g_variant_ref_sink(nint value);
    [DllImport(Glib)] internal static extern nint g_variant_ref(nint value);
    [DllImport(Glib)] internal static extern void g_variant_unref(nint value);
    [DllImport(Glib)] internal static extern nint g_variant_new_string([MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Glib)] internal static extern nint g_variant_new_object_path([MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Glib)] internal static extern nint g_variant_new_uint32(uint value);
    [DllImport(Glib)] internal static extern nint g_variant_new_int32(int value);
    [DllImport(Glib)] internal static extern nint g_variant_new_boolean(int value);
    [DllImport(Glib)] internal static extern nint g_variant_new_double(double value);
    [DllImport(Glib)] internal static extern nint g_variant_new_variant(nint value);
    [DllImport(Glib)] internal static extern nint g_variant_new_tuple(nint[] children, nuint count);
    [DllImport(Glib)] internal static extern nint g_variant_new_dict_entry(nint key, nint value);
    [DllImport(Glib)] internal static extern nint g_variant_new_array(nint type, nint[] children, nuint count);
    [DllImport(Glib)] internal static extern nint g_variant_type_new([MarshalAs(UnmanagedType.LPUTF8Str)] string type);
    [DllImport(Glib)] internal static extern void g_variant_type_free(nint type);
    [DllImport(Glib)] internal static extern nint g_variant_get_type_string(nint value);
    [DllImport(Glib)] internal static extern nint g_variant_get_string(nint value, out nuint length);
    [DllImport(Glib)] internal static extern uint g_variant_get_uint32(nint value);
    [DllImport(Glib)] internal static extern int g_variant_get_int32(nint value);
    [DllImport(Glib)] internal static extern int g_variant_get_handle(nint value);
    [DllImport(Glib)] internal static extern nint g_variant_get_variant(nint value);
    [DllImport(Glib)] internal static extern nuint g_variant_n_children(nint value);
    [DllImport(Glib)] internal static extern nint g_variant_get_child_value(nint value, nuint index);
    [DllImport(Glib)] internal static extern nint g_variant_lookup_value(nint value, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, nint type);
    [DllImport(Glib)] internal static extern nint g_main_context_new();
    [DllImport(Glib)] internal static extern void g_main_context_unref(nint context);
    [DllImport(Glib)] internal static extern void g_main_context_push_thread_default(nint context);
    [DllImport(Glib)] internal static extern void g_main_context_pop_thread_default(nint context);
    [DllImport(Glib)] internal static extern nint g_main_loop_new(nint context, int running);
    [DllImport(Glib)] internal static extern void g_main_loop_run(nint loop);
    [DllImport(Glib)] internal static extern void g_main_loop_quit(nint loop);
    [DllImport(Glib)] internal static extern void g_main_loop_unref(nint loop);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Source(nint data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Destroy(nint data);
    [DllImport(Glib)] internal static extern void g_main_context_invoke_full(nint context, int priority, Source callback, nint data, Destroy destroy);
    [DllImport(Gio)] internal static extern nint g_cancellable_new();
    [DllImport(Gio)] internal static extern void g_cancellable_cancel(nint cancellable);
    [DllImport(Gio)] internal static extern nint g_bus_get_sync(int busType, nint cancellable, out nint error);
    [DllImport(Gio)] internal static extern nint g_dbus_connection_get_unique_name(nint connection);
    [DllImport(Gio)] internal static extern int g_dbus_connection_is_closed(nint connection);
    [DllImport(Gio)] internal static extern nint g_dbus_connection_call_sync(nint connection, string destination, string path, string iface, string method, nint args, nint replyType, int flags, int timeout, nint cancellable, out nint error);
    [DllImport(Gio)] internal static extern nint g_dbus_connection_call_with_unix_fd_list_sync(nint connection, string destination, string path, string iface, string method, nint args, nint replyType, int flags, int timeout, nint fdList, out nint outFds, nint cancellable, out nint error);
    [DllImport(Gio)] internal static extern int g_unix_fd_list_get(nint list, int index, out nint error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Signal(nint connection, nint sender, nint path, nint iface, nint signal, nint args, nint data);
    [DllImport(Gio)] internal static extern uint g_dbus_connection_signal_subscribe(nint connection, string sender, string iface, string signal, string path, nint arg0, int flags, Signal callback, nint data, nint destroy);
    [DllImport(Gio)] internal static extern void g_dbus_connection_signal_unsubscribe(nint connection, uint subscription);

    internal static void Check(nint error, string operation, CancellationToken ct = default)
    {
        if (error == 0) return;
        // Third-party error messages can contain session identifiers. Do not surface them.
        var code = Marshal.ReadInt32(error, 4);
        g_error_free(error);
        ct.ThrowIfCancellationRequested();
        throw new IOException($"桌面 Portal {operation}失败（错误码 {code}）。");
    }
}

internal sealed class PortalValue : IDisposable
{
    internal nint Handle { get; private set; }
    internal PortalValue(nint owned) { if (owned == 0) throw new FormatException("缺少 Portal 响应字段。"); Handle = owned; }
    private static PortalValue Float(nint value) => new(NativeGlib.g_variant_ref_sink(value));
    internal static PortalValue String(string text) => Float(NativeGlib.g_variant_new_string(text));
    internal static PortalValue Path(string text) => Float(NativeGlib.g_variant_new_object_path(text));
    internal static PortalValue UInt(uint value) => Float(NativeGlib.g_variant_new_uint32(value));
    internal static PortalValue Int(int value) => Float(NativeGlib.g_variant_new_int32(value));
    internal static PortalValue Bool(bool value) => Float(NativeGlib.g_variant_new_boolean(value ? 1 : 0));
    internal static PortalValue Double(double value) => Float(NativeGlib.g_variant_new_double(value));
    internal static PortalValue Tuple(params PortalValue[] values) => Float(NativeGlib.g_variant_new_tuple(values.Select(v => v.Handle).ToArray(), (nuint)values.Length));
    internal static PortalValue Dict(params (string Key, PortalValue Value)[] values)
    {
        var entries = new List<PortalValue>();
        var type = NativeGlib.g_variant_type_new("{sv}");
        try
        {
            foreach (var (name, value) in values)
            {
                using var key = String(name);
                using var variant = Float(NativeGlib.g_variant_new_variant(value.Handle));
                entries.Add(Float(NativeGlib.g_variant_new_dict_entry(key.Handle, variant.Handle)));
            }
            return Float(NativeGlib.g_variant_new_array(type, entries.Select(v => v.Handle).ToArray(), (nuint)entries.Count));
        }
        finally { NativeGlib.g_variant_type_free(type); foreach (var item in entries) item.Dispose(); }
    }
    internal string Type => Marshal.PtrToStringUTF8(NativeGlib.g_variant_get_type_string(Handle))!;
    internal void Require(string type) { if (Type != type) throw new FormatException("无效的 Portal 字段类型。"); }
    internal int Count => checked((int)NativeGlib.g_variant_n_children(Handle));
    internal PortalValue Child(int index)
    {
        if (index < 0 || index >= Count) throw new FormatException("Portal 字段缺失。");
        return new(NativeGlib.g_variant_get_child_value(Handle, (nuint)index));
    }
    internal PortalValue? Get(string key)
    { Require("a{sv}"); var value = NativeGlib.g_variant_lookup_value(Handle, key, 0); return value == 0 ? null : new(value); }
    internal string Text() { if (Type is not ("s" or "o")) throw new FormatException("无效的 Portal 字符串。"); return Marshal.PtrToStringUTF8(NativeGlib.g_variant_get_string(Handle, out _))!; }
    internal uint UInt() { Require("u"); return NativeGlib.g_variant_get_uint32(Handle); }
    internal int Int() { Require("i"); return NativeGlib.g_variant_get_int32(Handle); }
    internal int FdIndex() { Require("h"); return NativeGlib.g_variant_get_handle(Handle); }
    internal PortalValue Unbox() { Require("v"); return new(NativeGlib.g_variant_get_variant(Handle)); }
    public void Dispose() { if (Handle != 0) { NativeGlib.g_variant_unref(Handle); Handle = 0; } }
}

internal sealed class PortalDispatcher : IAsyncDisposable
{
    private readonly Thread thread;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint context, loop;
    private static readonly NativeGlib.Source Dispatch = data => { ((Action)GCHandle.FromIntPtr(data).Target!)(); return 0; };
    private static readonly NativeGlib.Destroy Free = data => GCHandle.FromIntPtr(data).Free();
    internal PortalDispatcher()
    {
        thread = new Thread(() =>
        {
            try
            {
                context = NativeGlib.g_main_context_new();
                NativeGlib.g_main_context_push_thread_default(context);
                loop = NativeGlib.g_main_loop_new(context, 0);
                ready.TrySetResult();
                NativeGlib.g_main_loop_run(loop);
            }
            catch (Exception e) { ready.TrySetException(e); }
            finally
            {
                if (loop != 0) NativeGlib.g_main_loop_unref(loop);
                if (context != 0) { NativeGlib.g_main_context_pop_thread_default(context); NativeGlib.g_main_context_unref(context); }
            }
        }) { IsBackground = true, Name = "URemote Portal signals" };
        thread.Start();
    }
    internal async Task<T> InvokeAsync<T>(Func<T> work)
    {
        await ready.Task;
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = GCHandle.Alloc((Action)(() => { try { result.SetResult(work()); } catch (Exception e) { result.SetException(e); } }));
        NativeGlib.g_main_context_invoke_full(context, 0, Dispatch, GCHandle.ToIntPtr(handle), Free);
        return await result.Task;
    }
    public async ValueTask DisposeAsync()
    {
        try { await ready.Task; } catch { await Task.Run(thread.Join); return; }
        if (ready.Task.IsCompletedSuccessfully) await InvokeAsync(() => { NativeGlib.g_main_loop_quit(loop); return 0; });
        await Task.Run(thread.Join);
    }
}
