using System.Runtime.InteropServices;

namespace URemote.Linux;

internal sealed partial class PortalBus
{
    private readonly Dictionary<uint, Export> exports = [];
    internal string UniqueName => Marshal.PtrToStringUTF8(NativeGlib.g_dbus_connection_get_unique_name(connection))!;

    // A private, nonce-named callback used to acknowledge that KWin actually applied
    // capture exclusion. Only the current owner of org.kde.KWin may invoke it.
    internal Task<Export> ExportStringAsync(string path, string sender, Action<string> receive) => dispatcher.InvokeAsync(() =>
    {
        var export = new Export(this, path, sender, receive);
        exports.Add(export.Id, export);
        return export;
    });

    internal sealed class Export : IAsyncDisposable
    {
        internal const string Interface = "org.asterdock.URemote.Privacy";
        private readonly PortalBus bus;
        private readonly Method callback;
        private nint node, table;
        internal uint Id { get; private set; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void Method(nint connection, nint sender, nint path, nint iface, nint method, nint args, nint invocation, nint data);
        internal Export(PortalBus bus, string path, string sender, Action<string> receive)
        {
            this.bus = bus;
            callback = (_, caller, _, _, _, args, invocation, _) =>
            {
                try
                {
                    if (Marshal.PtrToStringUTF8(caller) != sender) throw new IOException();
                    using var value = new PortalValue(NativeGlib.g_variant_ref(args));
                    value.Require("(s)"); using var text = value.Child(0);
                    var message = text.Text(); if (message.Length > 64) throw new FormatException();
                    receive(message);
                    using var empty = PortalValue.Tuple();
                    g_dbus_method_invocation_return_value(invocation, empty.Handle);
                }
                catch { g_dbus_method_invocation_return_dbus_error(invocation, Interface + ".Rejected", "Rejected"); }
            };
            try
            {
                node = g_dbus_node_info_new_for_xml("<node><interface name='" + Interface + "'><method name='Report'><arg name='result' type='s' direction='in'/></method></interface></node>", out var error);
                NativeGlib.Check(error, "创建防窥回调");
                var iface = g_dbus_node_info_lookup_interface(node, Interface);
                // GDBusInterfaceVTable: three callbacks followed by eight reserved pointers.
                table = Marshal.AllocHGlobal(11 * nint.Size);
                for (var i = 0; i < 11; i++) Marshal.WriteIntPtr(table, i * nint.Size, 0);
                Marshal.WriteIntPtr(table, Marshal.GetFunctionPointerForDelegate(callback));
                Id = g_dbus_connection_register_object(bus.connection, path, iface, table, 0, 0, out error);
                NativeGlib.Check(error, "注册防窥回调");
                if (Id == 0) throw new IOException("无法注册防窥回调。");
            }
            catch { Free(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            await bus.dispatcher.InvokeAsync(() =>
            {
                if (Id != 0) { g_dbus_connection_unregister_object(bus.connection, Id); bus.exports.Remove(Id); Id = 0; }
                Free(); return 0;
            });
        }
        private void Free()
        {
            if (table != 0) { Marshal.FreeHGlobal(table); table = 0; }
            if (node != 0) { g_dbus_node_info_unref(node); node = 0; }
        }
        [DllImport(NativeGlib.Gio)] private static extern nint g_dbus_node_info_new_for_xml(string xml, out nint error);
        [DllImport(NativeGlib.Gio)] private static extern nint g_dbus_node_info_lookup_interface(nint node, string name);
        [DllImport(NativeGlib.Gio)] private static extern void g_dbus_node_info_unref(nint node);
        [DllImport(NativeGlib.Gio)] private static extern uint g_dbus_connection_register_object(nint connection, string path, nint iface, nint table, nint data, nint destroy, out nint error);
        [DllImport(NativeGlib.Gio)] private static extern int g_dbus_connection_unregister_object(nint connection, uint id);
        [DllImport(NativeGlib.Gio)] private static extern void g_dbus_method_invocation_return_value(nint invocation, nint result);
        [DllImport(NativeGlib.Gio)] private static extern void g_dbus_method_invocation_return_dbus_error(nint invocation, string error, string message);
    }
}
