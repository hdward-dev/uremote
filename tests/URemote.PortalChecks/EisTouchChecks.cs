using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using URemote.Core;
using URemote.Linux;

internal static class EisTouchChecks
{
    internal static async Task RunAsync(Action<bool, string> check, bool fullInput = false, bool textCapability = true)
    {
        var server = E.eis_new(0); nint seat = 0, device = 0;
        var events = new List<(int Type, double X, double Y)>();
        EisTouchSink? sink = null;
        try
        {
            check(E.eis_setup_backend_fd(server) == 0, "isolated EIS backend opens without desktop access");
            using var fd = new SafeFileHandle(E.eis_backend_fd_add_client(server), true);
            sink = new EisTouchSink(fd, fullInput);
            async Task Drain(Func<bool> done)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                do
                {
                    sink.Dispatch(); E.eis_dispatch(server);
                    nint ev;
                    while ((ev = E.eis_get_event(server)) != 0)
                    {
                        try
                        {
                            var type = E.eis_event_get_type(ev);
                            switch (type)
                            {
                                case 1:
                                    var client = E.eis_event_get_client(ev); E.eis_client_connect(client);
                                    seat = E.eis_client_new_seat(client, "fixture");
                                    foreach (var cap in fullInput ? new[] {2,4,8,16,32}.Concat(textCapability ? new[] {64} : []) : new[] {8})
                                        E.eis_seat_configure_capability(seat, cap);
                                    E.eis_seat_add(seat); break;
                                case 3:
                                    device = E.eis_seat_new_device(seat);
                                    E.eis_device_configure_type(device, 1); E.eis_device_configure_name(device, "touch fixture");
                                    foreach (var cap in fullInput ? new[] {2,4,8,16,32}.Concat(textCapability ? new[] {64} : []) : new[] {8})
                                        E.eis_device_configure_capability(device, cap);
                                    if (fullInput)
                                    {
                                        using var keymapFile = UnixFileDescriptor.CreateMemoryFile();
                                        var data = System.Text.Encoding.UTF8.GetBytes(WaylandVirtualKeyboard.Keymap() + "\0");
                                        RandomAccess.SetLength(keymapFile,data.Length); RandomAccess.Write(keymapFile,data,0);
                                        var keymap = E.eis_device_new_keymap(device,1,(int)keymapFile.DangerousGetHandle(),(nuint)data.Length);
                                        E.eis_keymap_add(keymap); E.eis_keymap_unref(keymap);
                                    }
                                    foreach (var (id, x, width, height) in new[] { ("screen-B", 1970u, 2259u, 1271u), ("screen-A", 0u, 1970u, 1108u) })
                                    {
                                        var region = E.eis_device_new_region(device);
                                        E.eis_region_set_offset(region, x, 0); E.eis_region_set_size(region, width, height);
                                        E.eis_region_set_mapping_id(region, id); E.eis_region_add(region); E.eis_region_unref(region);
                                    }
                                    E.eis_device_add(device); E.eis_device_resume(device); break;
                                case 700: events.Add((type,E.eis_event_keyboard_get_key(ev),E.eis_event_keyboard_get_key_is_press(ev) ? 1 : 0)); break;
                                case 500: events.Add((type,E.eis_event_button_get_button(ev),E.eis_event_button_get_is_press(ev) ? 1 : 0)); break;
                                case 400: events.Add((type,E.eis_event_pointer_get_absolute_x(ev),E.eis_event_pointer_get_absolute_y(ev))); break;
                                case 600: events.Add((type,E.eis_event_scroll_get_dx(ev),E.eis_event_scroll_get_dy(ev))); break;
                                case 601: events.Add((type,0,0)); break;
                                case 901:
                                    check(Marshal.PtrToStringUTF8(E.eis_event_text_get_utf8(ev)) == "测试🙂", "EIS UTF-8 preserves Chinese and emoji");
                                    events.Add((type,0,0)); break;
                                case 100: case 801: events.Add((type, 0, 0)); break;
                                case 800: case 802: events.Add((type, E.eis_event_touch_get_x(ev), E.eis_event_touch_get_y(ev))); break;
                            }
                        }
                        finally { E.eis_event_unref(ev); }
                    }
                    if (done()) return;
                    await Task.Delay(2, timeout.Token);
                } while (true);
            }
            await Drain(() => sink.CanMap("screen-A") && sink.CanMap("screen-B"));
            if (fullInput)
            {
                check(sink.HasCapability(4) && sink.CanPoint("screen-A"), "one EIS connection provides keyboard, mouse and touch");
                sink.Key(125,true); sink.Key(19,true); sink.Key(19,false); sink.Key(125,false);
                await Drain(() => events.Count >= 8);
                check(events.Where(e=>e.Type==700).Select(e=>(e.X,e.Y)).SequenceEqual(new[] {(125d,1d),(19d,1d),(19d,0d),(125d,0d)}), "Super+R preserves evdev codes, order and release");
                events.Clear(); sink.Point("screen-B",.5,.5); await Drain(()=>events.Count>=2);
                check(events[0].Type==400 && events[0].X==3099.5 && events[0].Y==635.5,"pointer uses selected logical screen region");
                events.Clear(); sink.Key(272,true,true); sink.Key(272,false,true); await Drain(()=>events.Count>=4);
                check(events.Where(e=>e.Type==500).Select(e=>e.Y).SequenceEqual(new[]{1d,0d}), "button press and release are framed");
                events.Clear(); sink.Scroll(2,-3); await Drain(()=>events.Count>=2);
                check(events[0]==(600,2d,-3d), "scroll direction and horizontal delta are preserved");
                events.Clear(); sink.Scroll(0,0); await Drain(()=>events.Count>=2);
                check(events[0].Type==601,"zero scroll emits stop instead of invalid empty delta");
                events.Clear();
                check(sink.Text("测试🙂")==textCapability,"native text depends on advertised server capability");
                if(textCapability) { await Drain(()=>events.Count>=2); events.Clear(); }
            }
            check(!sink.CanMap("unknown"), "unmapped screen is unavailable");
            sink.Apply("screen-A", new(1, [new(10, .25, .5), new(11, .75, .5)]));
            await Drain(() => events.Count >= 3);
            check(events.Select(e => e.Type).SequenceEqual(new[] { 800,800,100 }), "two simultaneous downs have exactly one EIS frame");
            check(Math.Abs(events[0].X - 492.5) < .01 && events[0].Y == 554, "mapping ID selects correct region despite reversed region order");
            events.Clear();
            sink.Apply("screen-A", new(2, [new(10, .3, .4), new(11, .7, .6)]));
            await Drain(() => events.Count >= 3);
            check(events.Select(e => e.Type).SequenceEqual(new[] {802,802,100}), "pinch contacts move in a single frame");
            events.Clear();
            sink.Apply("screen-B", new(2, [new(10,.5,.5)]));
            await Drain(() => events.Count >= 3);
            check(events.Select(e => e.Type).SequenceEqual(new[] {801,801,100}), "screen change releases old contacts without resurrecting a move");
            events.Clear();
            sink.Apply("screen-B", new(1, [new(10,.5,.5)]));
            await Drain(() => events.Count >= 2);
            check(Math.Abs(events[0].X - 3099.5) < .01 && Math.Abs(events[0].Y - 635.5) < .01, "second screen uses its own logical region including offset");
            events.Clear(); sink.Apply("screen-B", new(3, []));
            await Drain(() => events.Count >= 2);
            check(events.Select(e => e.Type).SequenceEqual(new[] {801,100}), "empty end packet releases all contacts and submits frame");
            events.Clear(); sink.Apply("screen-B", new(1, [new(1,0,0)]));
            await Drain(() => events.Count >= 2);
            events.Clear(); E.eis_device_pause(device);
            await Drain(() => !sink.CanMap("screen-B"));
            check(!sink.Apply("screen-B", new(2,[new(1,.3,.4)])), "paused devices reject touch");
            E.eis_device_resume(device); await Drain(() => sink.CanMap("screen-B"));
            check(!sink.Apply("screen-B", new(2,[new(1,.3,.4)])), "resume cannot continue stale contacts");
            sink.Apply("screen-B",new(1,[new(1,1,1)])); await Drain(() => events.Count >= 2);
            check(events[0].X == 4228 && events[0].Y == 1270, "edge coordinate stays inside region");
            events.Clear(); sink.Reset(); await Drain(() => events.Count >= 2);
            check(events.Select(e => e.Type).SequenceEqual(new[] {801,100}), "peer cleanup releases contacts in a complete frame");
            E.eis_device_remove(device); await Drain(() => !sink.CanMap("screen-B"));
            check(!sink.Apply("screen-B",new(1,[new(1,0,0)])), "removed devices cannot receive events");
        }
        finally
        {
            sink?.Dispose();
            if(device != 0) E.eis_device_unref(device);
            if(seat != 0) E.eis_seat_unref(seat);
            E.eis_unref(server);
        }
    }
    private static class E
    {
        [DllImport("libeis.so.1")] internal static extern nint eis_device_new_keymap(nint device,int type,int fd,nuint size);
        [DllImport("libeis.so.1")] internal static extern void eis_keymap_add(nint keymap);
        [DllImport("libeis.so.1")] internal static extern nint eis_keymap_unref(nint keymap);
        [DllImport("libeis.so.1")] internal static extern uint eis_event_keyboard_get_key(nint ev);
        [DllImport("libeis.so.1")] internal static extern uint eis_event_button_get_button(nint ev);
        [DllImport("libeis.so.1")] internal static extern double eis_event_pointer_get_absolute_x(nint ev);
        [DllImport("libeis.so.1")] internal static extern double eis_event_pointer_get_absolute_y(nint ev);
        [DllImport("libeis.so.1")] internal static extern double eis_event_scroll_get_dx(nint ev);
        [DllImport("libeis.so.1")] internal static extern double eis_event_scroll_get_dy(nint ev);
        [DllImport("libeis.so.1")] internal static extern nint eis_event_text_get_utf8(nint ev);
        [DllImport("libeis.so.1")] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool eis_event_keyboard_get_key_is_press(nint ev);
        [DllImport("libeis.so.1")] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool eis_event_button_get_is_press(nint ev);
        [DllImport("libeis.so.1")] internal static extern nint eis_new(nint data);
        [DllImport("libeis.so.1")] internal static extern nint eis_unref(nint ctx);
        [DllImport("libeis.so.1")] internal static extern int eis_setup_backend_fd(nint ctx);
        [DllImport("libeis.so.1")] internal static extern int eis_backend_fd_add_client(nint ctx);
        [DllImport("libeis.so.1")] internal static extern void eis_dispatch(nint ctx);
        [DllImport("libeis.so.1")] internal static extern nint eis_get_event(nint ctx);
        [DllImport("libeis.so.1")] internal static extern int eis_event_get_type(nint ev);
        [DllImport("libeis.so.1")] internal static extern nint eis_event_get_client(nint ev);
        [DllImport("libeis.so.1")] internal static extern nint eis_event_unref(nint ev);
        [DllImport("libeis.so.1")] internal static extern void eis_client_connect(nint client);
        [DllImport("libeis.so.1")] internal static extern nint eis_client_new_seat(nint client,string name);
        [DllImport("libeis.so.1")] internal static extern void eis_seat_configure_capability(nint seat,int cap);
        [DllImport("libeis.so.1")] internal static extern void eis_seat_add(nint seat);
        [DllImport("libeis.so.1")] internal static extern nint eis_seat_unref(nint seat);
        [DllImport("libeis.so.1")] internal static extern nint eis_seat_new_device(nint seat);
        [DllImport("libeis.so.1")] internal static extern void eis_device_configure_type(nint device,int type);
        [DllImport("libeis.so.1")] internal static extern void eis_device_configure_name(nint device,string name);
        [DllImport("libeis.so.1")] internal static extern void eis_device_configure_capability(nint device,int cap);
        [DllImport("libeis.so.1")] internal static extern nint eis_device_new_region(nint device);
        [DllImport("libeis.so.1")] internal static extern void eis_region_set_offset(nint region,uint x,uint y);
        [DllImport("libeis.so.1")] internal static extern void eis_region_set_size(nint region,uint width,uint height);
        [DllImport("libeis.so.1")] internal static extern void eis_region_set_mapping_id(nint region,string id);
        [DllImport("libeis.so.1")] internal static extern void eis_region_add(nint region);
        [DllImport("libeis.so.1")] internal static extern nint eis_region_unref(nint region);
        [DllImport("libeis.so.1")] internal static extern void eis_device_add(nint device);
        [DllImport("libeis.so.1")] internal static extern void eis_device_resume(nint device);
        [DllImport("libeis.so.1")] internal static extern void eis_device_pause(nint device);
        [DllImport("libeis.so.1")] internal static extern void eis_device_remove(nint device);
        [DllImport("libeis.so.1")] internal static extern nint eis_device_unref(nint device);
        [DllImport("libeis.so.1")] internal static extern double eis_event_touch_get_x(nint ev);
        [DllImport("libeis.so.1")] internal static extern double eis_event_touch_get_y(nint ev);
    }
}
