using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using URemote.Core;
using URemote.Linux;

internal static class EisTouchChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var server = E.eis_new(0); nint seat = 0, device = 0;
        var events = new List<(int Type, double X, double Y)>();
        EisTouchSink? sink = null;
        try
        {
            check(E.eis_setup_backend_fd(server) == 0, "isolated EIS backend opens without desktop access");
            using var fd = new SafeFileHandle(E.eis_backend_fd_add_client(server), true);
            sink = new EisTouchSink(fd);
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
                                    E.eis_seat_configure_capability(seat, 8); E.eis_seat_add(seat); break;
                                case 3:
                                    device = E.eis_seat_new_device(seat);
                                    E.eis_device_configure_type(device, 1); E.eis_device_configure_name(device, "touch fixture");
                                    E.eis_device_configure_capability(device, 8);
                                    foreach (var (id, x, width, height) in new[] { ("screen-B", 1970u, 2259u, 1271u), ("screen-A", 0u, 1970u, 1108u) })
                                    {
                                        var region = E.eis_device_new_region(device);
                                        E.eis_region_set_offset(region, x, 0); E.eis_region_set_size(region, width, height);
                                        E.eis_region_set_mapping_id(region, id); E.eis_region_add(region); E.eis_region_unref(region);
                                    }
                                    E.eis_device_add(device); E.eis_device_resume(device); break;
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
