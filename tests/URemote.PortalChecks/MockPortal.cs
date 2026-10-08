using System.Runtime.InteropServices;
using URemote.Linux;

internal sealed class MockPortal : IAsyncDisposable
{
    internal const string Session = "/org/freedesktop/portal/desktop/session/fixture/test";
    private readonly PortalDispatcher dispatcher = new();
    private readonly List<uint> objects = [];
    private nint connection, node;
    private readonly Method callback;
    private readonly GetProperty getter;
    internal volatile bool Hold, Deny;
    internal int RequestClosed;
    private static readonly string Xml = """
    <node>
      <interface name="org.freedesktop.portal.RemoteDesktop">
        <method name="CreateSession"><arg type="a{sv}" direction="in"/><arg type="o" direction="out"/></method>
        <method name="SelectDevices"><arg type="o" direction="in"/><arg type="a{sv}" direction="in"/><arg type="o" direction="out"/></method>
        <method name="Start"><arg type="o" direction="in"/><arg type="s" direction="in"/><arg type="a{sv}" direction="in"/><arg type="o" direction="out"/></method>
      </interface>
      <interface name="org.freedesktop.portal.ScreenCast">
        <property name="AvailableSourceTypes" type="u" access="read"/>
        <method name="OpenPipeWireRemote"><arg type="o" direction="in"/><arg type="a{sv}" direction="in"/><arg type="h" direction="out"/></method>
      </interface>
      <interface name="org.freedesktop.portal.Request"><method name="Close"/></interface>
    </node>
    """;
    private MockPortal() { callback = OnMethod; getter = (_,_,_,_,_,_,_) => NativeGlib.g_variant_new_uint32(1); }
    internal static async Task<MockPortal> CreateAsync()
    {
        var server = new MockPortal();
        await server.dispatcher.InvokeAsync(() =>
        {
            server.connection = NativeGlib.g_bus_get_sync(2,0,out var error); NativeGlib.Check(error,"test bus");
            server.node = g_dbus_node_info_new_for_xml(Xml,out error); NativeGlib.Check(error,"test introspection");
            using var name=PortalValue.String(PortalBus.Destination);using var flags=PortalValue.UInt(0);using var args=PortalValue.Tuple(name,flags);
            var result=NativeGlib.g_dbus_connection_call_sync(server.connection,"org.freedesktop.DBus","/org/freedesktop/DBus","org.freedesktop.DBus","RequestName",args.Handle,0,0,2000,0,out error);
            NativeGlib.Check(error,"test name");using var reply=new PortalValue(result);
            server.Register(PortalBus.Desktop,PortalBus.RemoteDesktop);
            server.Register(PortalBus.Desktop,PortalBus.ScreenCast);
            return 0;
        });
        return server;
    }
    private void Register(string path,string iface)
    {
        var table=new VTable { Method=Marshal.GetFunctionPointerForDelegate(callback),GetProperty=Marshal.GetFunctionPointerForDelegate(getter) };
        var id=g_dbus_connection_register_object(connection,path,g_dbus_node_info_lookup_interface(node,iface),ref table,0,0,out var error);
        NativeGlib.Check(error,"test export");objects.Add(id);
    }
    private void OnMethod(nint conn,nint senderPtr,nint path,nint iface,nint methodPtr,nint parameters,nint invocation,nint data)
    {
        try
        {
            var method=Marshal.PtrToStringUTF8(methodPtr)!;
            using var args=new PortalValue(NativeGlib.g_variant_ref(parameters));
            if(method=="Close") { Interlocked.Increment(ref RequestClosed);using var empty=PortalValue.Tuple();g_dbus_method_invocation_return_value(invocation,empty.Handle);return; }
            if(method=="OpenPipeWireRemote")
            {
                args.Require("(oa{sv})");
                using var file=File.OpenHandle("/tmp/uremote-native-fd-fixture",FileMode.Create,FileAccess.ReadWrite,FileShare.ReadWrite);
                RandomAccess.Write(file,new byte[]{1,2,3,4},0);
                var fds=g_unix_fd_list_new();
                try
                {
                    g_unix_fd_list_append(fds,(int)file.DangerousGetHandle(),out var error);NativeGlib.Check(error,"test fd padding");
                    var index=g_unix_fd_list_append(fds,(int)file.DangerousGetHandle(),out error);NativeGlib.Check(error,"test fd");
                    using var value=new PortalValue(NativeGlib.g_variant_ref_sink(g_variant_new_handle(index)));using var reply=PortalValue.Tuple(value);
                    g_dbus_method_invocation_return_value_with_unix_fd_list(invocation,reply.Handle,fds);
                }
                finally{NativeGlib.g_object_unref(fds);File.Delete("/tmp/uremote-native-fd-fixture");}
                return;
            }
            args.Require(method=="CreateSession"?"(a{sv})":method=="Start"?"(osa{sv})":"(oa{sv})");
            using var options=args.Child(args.Count-1);using var token=options.Get("handle_token")!;
            var sender=Marshal.PtrToStringUTF8(senderPtr)!;
            var request=PortalBus.Desktop+"/request/"+sender.TrimStart(':').Replace('.','_')+"/"+token.Text();
            Register(request,PortalBus.InterfacePrefix+".Request");
            if(!Hold)
            {
                using var code=PortalValue.UInt(Deny?1u:0u);using var session=PortalValue.Path(Session);using var types=PortalValue.UInt(3);
                using var result=method=="CreateSession"?PortalValue.Dict(("session_handle",session)):PortalValue.Dict(("devices",types));
                using var response=PortalValue.Tuple(code,result);
                if(g_dbus_connection_emit_signal(connection,sender,request,PortalBus.InterfacePrefix+".Request","Response",response.Handle,out var error)==0)NativeGlib.Check(error,"test response");
            }
            using var returned=PortalValue.Path(request);using var output=PortalValue.Tuple(returned);g_dbus_method_invocation_return_value(invocation,output.Handle);
        }
        catch { g_dbus_method_invocation_return_dbus_error(invocation,"org.freedesktop.DBus.Error.Failed","Mock request failed"); }
    }
    internal Task EmitClosedAsync()=>dispatcher.InvokeAsync(()=>
    {
        using var value=PortalValue.Tuple();g_dbus_connection_emit_signal(connection,null,Session,PortalBus.InterfacePrefix+".Session","Closed",value.Handle,out var error);NativeGlib.Check(error,"test closed");return 0;
    });
    public async ValueTask DisposeAsync()
    {
        await dispatcher.InvokeAsync(()=>
        {
            foreach(var id in objects)g_dbus_connection_unregister_object(connection,id);
            g_dbus_node_info_unref(node);NativeGlib.g_object_unref(connection);return 0;
        });
        await dispatcher.DisposeAsync();
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Method(nint connection,nint sender,nint path,nint iface,nint method,nint args,nint invocation,nint data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint GetProperty(nint connection,nint sender,nint path,nint iface,nint property,nint error,nint data);
    [StructLayout(LayoutKind.Sequential)] private struct VTable { internal nint Method,GetProperty,SetProperty,P0,P1,P2,P3,P4,P5,P6,P7; }
    [DllImport(NativeGlib.Gio)] private static extern nint g_dbus_node_info_new_for_xml(string xml,out nint error);
    [DllImport(NativeGlib.Gio)] private static extern nint g_dbus_node_info_lookup_interface(nint node,string iface);
    [DllImport(NativeGlib.Gio)] private static extern void g_dbus_node_info_unref(nint node);
    [DllImport(NativeGlib.Gio)] private static extern uint g_dbus_connection_register_object(nint connection,string path,nint iface,ref VTable table,nint data,nint destroy,out nint error);
    [DllImport(NativeGlib.Gio)] private static extern int g_dbus_connection_unregister_object(nint connection,uint id);
    [DllImport(NativeGlib.Gio)] private static extern void g_dbus_method_invocation_return_value(nint invocation,nint value);
    [DllImport(NativeGlib.Gio)] private static extern void g_dbus_method_invocation_return_value_with_unix_fd_list(nint invocation,nint value,nint fds);
    [DllImport(NativeGlib.Gio)] private static extern void g_dbus_method_invocation_return_dbus_error(nint invocation,string name,string message);
    [DllImport(NativeGlib.Gio)] private static extern int g_dbus_connection_emit_signal(nint connection,string? destination,string path,string iface,string signal,nint args,out nint error);
    [DllImport(NativeGlib.Gio)] private static extern nint g_unix_fd_list_new();
    [DllImport(NativeGlib.Gio)] private static extern int g_unix_fd_list_append(nint fds,int fd,out nint error);
    [DllImport(NativeGlib.Glib)] private static extern nint g_variant_new_handle(int index);
}
