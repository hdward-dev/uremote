using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using URemote.Core;

namespace URemote.Linux;

// All libei access, including dispatch and teardown, is serialized. A remote
// packet is a single touch frame; never dispatch halfway through its contacts.
internal sealed class EisTouchSink : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<nint, bool> devices = [];
    private readonly Dictionary<uint, nint> contacts = [];
    private readonly TouchFrameState state = new();
    private nint context, selectedDevice;
    private string? mapping;
    private bool disconnected;
    private uint sequence;

    internal EisTouchSink(SafeFileHandle fd)
    {
        context = NativeEi.ei_new_sender(0);
        if (context == 0) throw new IOException("无法创建 EIS 触屏通道。");
        try
        {
            NativeEi.ei_configure_name(context, "AsterDock touch");
            var descriptor = checked((int)fd.DangerousGetHandle());
            // setup takes ownership, including on failure.
            var result = NativeEi.ei_setup_backend_fd(context, descriptor);
            fd.SetHandleAsInvalid();
            if (result != 0) throw new IOException("无法打开 EIS 触屏通道。");
        }
        catch { Dispose(); throw; }
    }
    internal void Dispatch()
    {
        lock (gate)
        {
            if (context == 0 || disconnected) return;
            NativeEi.ei_dispatch(context);
            nint ev;
            while ((ev = NativeEi.ei_get_event(context)) != 0)
            {
                try
                {
                    var device = NativeEi.ei_event_get_device(ev);
                    switch (NativeEi.ei_event_get_type(ev))
                    {
                        case 2: disconnected = true; DropContacts(false); break;
                        case 3: NativeEi.ei_seat_bind_capabilities(NativeEi.ei_event_get_seat(ev), 8, 0); break;
                        case 5:
                            if (NativeEi.ei_device_has_capability(device, 8)) devices.Add(NativeEi.ei_device_ref(device), false);
                            break;
                        case 6:
                            if (selectedDevice == device) DropContacts(false);
                            if (devices.Remove(device)) NativeEi.ei_device_unref(device);
                            break;
                        case 7:
                            if (selectedDevice == device) DropContacts(false);
                            if (devices.ContainsKey(device)) devices[device] = false;
                            break;
                        case 8:
                            if (devices.ContainsKey(device))
                            {
                                NativeEi.ei_device_start_emulating(device, ++sequence);
                                devices[device] = true;
                            }
                            break;
                    }
                }
                finally { NativeEi.ei_event_unref(ev); }
            }
        }
    }
    private (nint Device, nint Region)? Resolve(string id)
    {
        if (context == 0 || disconnected || string.IsNullOrEmpty(id)) return null;
        (nint, nint)? found = null;
        foreach (var (device, ready) in devices)
        {
            if (!ready) continue;
            for (nuint index = 0; ; index++)
            {
                var region = NativeEi.ei_device_get_region(device, index);
                if (region == 0) break;
                if (Marshal.PtrToStringUTF8(NativeEi.ei_region_get_mapping_id(region)) != id) continue;
                if (found is not null) return null; // Ambiguous mappings must never target another screen.
                if (NativeEi.ei_region_get_width(region) == 0 || NativeEi.ei_region_get_height(region) == 0) return null;
                found = (device, region);
            }
        }
        return found;
    }
    internal bool CanMap(string id) { lock (gate) return Resolve(id) is not null; }
    internal bool Apply(string id, HostTouchEvent input)
    {
        lock (gate)
        {
            if (mapping != id) { DropContacts(true); mapping = id; }
            if (Resolve(id) is not { } target) { DropContacts(true); return false; }
            if (selectedDevice != 0 && selectedDevice != target.Device) DropContacts(true);
            selectedDevice = target.Device;
            var changes = state.Apply(input);
            if (changes.Count == 0) return false;
            var x = NativeEi.ei_region_get_x(target.Region); var y = NativeEi.ei_region_get_y(target.Region);
            var width = NativeEi.ei_region_get_width(target.Region); var height = NativeEi.ei_region_get_height(target.Region);
            var released = new List<nint>();
            try
            {
                foreach (var change in changes)
                {
                    var px = x + Math.Min(change.X * width, width - 1);
                    var py = y + Math.Min(change.Y * height, height - 1);
                    switch (change.Kind)
                    {
                        case TouchChangeKind.Down:
                            var touch = NativeEi.ei_device_touch_new(target.Device);
                            if (touch == 0) throw new IOException("无法创建触点。");
                            contacts.Add(change.Id, touch);
                            NativeEi.ei_touch_down(touch, px, py);
                            break;
                        case TouchChangeKind.Motion: NativeEi.ei_touch_motion(contacts[change.Id], px, py); break;
                        case TouchChangeKind.Up:
                            var ended = contacts[change.Id]; contacts.Remove(change.Id);
                            NativeEi.ei_touch_up(ended); released.Add(ended); break;
                    }
                }
                NativeEi.ei_device_frame(target.Device, NativeEi.ei_now(context));
                return true;
            }
            catch { DropContacts(true); throw; }
            finally { foreach (var touch in released) NativeEi.ei_touch_unref(touch); }
        }
    }
    internal void Reset() { lock (gate) { DropContacts(true); mapping = null; } }
    private void DropContacts(bool send)
    {
        send &= context != 0 && !disconnected && selectedDevice != 0 && devices.GetValueOrDefault(selectedDevice);
        foreach (var touch in contacts.Values)
        {
            if (send) NativeEi.ei_touch_up(touch);
        }
        if (send && contacts.Count > 0) NativeEi.ei_device_frame(selectedDevice, NativeEi.ei_now(context));
        foreach (var touch in contacts.Values) NativeEi.ei_touch_unref(touch);
        contacts.Clear(); state.Reset(); selectedDevice = 0;
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (context == 0) return;
            DropContacts(true);
            foreach (var (device, ready) in devices)
            {
                if (ready && !disconnected) NativeEi.ei_device_stop_emulating(device);
                NativeEi.ei_device_unref(device);
            }
            devices.Clear(); NativeEi.ei_unref(context); context = 0;
        }
    }
}
