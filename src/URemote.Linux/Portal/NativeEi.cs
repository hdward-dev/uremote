using System.Runtime.InteropServices;
namespace URemote.Linux;
internal static class NativeEi
{
    private const string Lib = "libei.so.1";
    [DllImport(Lib)] internal static extern nint ei_new_sender(nint data);
    [DllImport(Lib)] internal static extern nint ei_unref(nint ei);
    [DllImport(Lib)] internal static extern void ei_configure_name(nint ei,string name);
    [DllImport(Lib)] internal static extern int ei_setup_backend_fd(nint ei,int fd);
    [DllImport(Lib)] internal static extern void ei_dispatch(nint ei);
    [DllImport(Lib)] internal static extern nint ei_get_event(nint ei);
    [DllImport(Lib)] internal static extern int ei_event_get_type(nint ev);
    [DllImport(Lib)] internal static extern nint ei_event_get_seat(nint ev);
    [DllImport(Lib)] internal static extern nint ei_event_get_device(nint ev);
    [DllImport(Lib)] internal static extern nint ei_event_unref(nint ev);
    // A fixed touch-only invocation of the C variadic API, terminated by NULL.
    [DllImport(Lib)] internal static extern void ei_seat_bind_capabilities(nint seat,int touch,nint end);
    [DllImport(Lib)] internal static extern nint ei_device_ref(nint device);
    [DllImport(Lib)] internal static extern nint ei_device_unref(nint device);
    [DllImport(Lib)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool ei_device_has_capability(nint device,int cap);
    [DllImport(Lib)] internal static extern void ei_device_start_emulating(nint device,uint seq);
    [DllImport(Lib)] internal static extern void ei_device_stop_emulating(nint device);
    [DllImport(Lib)] internal static extern void ei_device_frame(nint device,ulong time);
    [DllImport(Lib)] internal static extern ulong ei_now(nint ei);
    [DllImport(Lib)] internal static extern nint ei_device_get_region(nint device,nuint index);
    [DllImport(Lib)] internal static extern nint ei_region_get_mapping_id(nint region);
    [DllImport(Lib)] internal static extern uint ei_region_get_x(nint region);
    [DllImport(Lib)] internal static extern uint ei_region_get_y(nint region);
    [DllImport(Lib)] internal static extern uint ei_region_get_width(nint region);
    [DllImport(Lib)] internal static extern uint ei_region_get_height(nint region);
    [DllImport(Lib)] internal static extern nint ei_device_touch_new(nint device);
    [DllImport(Lib)] internal static extern void ei_touch_down(nint touch,double x,double y);
    [DllImport(Lib)] internal static extern void ei_touch_motion(nint touch,double x,double y);
    [DllImport(Lib)] internal static extern void ei_touch_up(nint touch);
    [DllImport(Lib)] internal static extern nint ei_touch_unref(nint touch);
}
