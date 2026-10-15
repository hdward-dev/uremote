using URemote.Core;

namespace URemote.Linux;

internal sealed partial class EisTouchSink
{
    private readonly Dictionary<(int Capability, uint Code), nint> heldInput = [];
    internal bool Connected { get { lock (gate) return context != 0 && !disconnected; } }
    private nint FindDevice(int capability) => context == 0 || disconnected ? 0
        : devices.FirstOrDefault(pair => pair.Value && NativeEi.ei_device_has_capability(pair.Key, capability)).Key;
    internal bool HasCapability(int capability) { lock (gate) return FindDevice(capability) != 0; }
    internal bool CanPoint(string id) { lock (gate) return Resolve(id, 2) is not null; }
    internal bool Point(string id, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x > 1 || y < 0 || y > 1) throw new ArgumentException("Invalid pointer position.");
        lock (gate)
        {
            if (Resolve(id, 2) is not { } target) return false;
            var width = NativeEi.ei_region_get_width(target.Region); var height = NativeEi.ei_region_get_height(target.Region);
            NativeEi.ei_device_pointer_motion_absolute(target.Device,
                NativeEi.ei_region_get_x(target.Region) + Math.Min(x * width, width - 1),
                NativeEi.ei_region_get_y(target.Region) + Math.Min(y * height, height - 1));
            NativeEi.ei_device_frame(target.Device, NativeEi.ei_now(context));
            return true;
        }
    }
    internal bool Key(uint code, bool down, bool button = false)
    {
        lock (gate)
        {
            var cap = button ? 32 : 4; var key = (cap, code);
            if (down && heldInput.ContainsKey(key)) return true;
            nint device;
            if (down)
            {
                device = FindDevice(cap); if (device == 0) return false;
                heldInput.Add(key, device);
            }
            else if (!heldInput.Remove(key, out device)) return true;
            if (button) NativeEi.ei_device_button_button(device, code, down);
            else NativeEi.ei_device_keyboard_key(device, code, down);
            NativeEi.ei_device_frame(device, NativeEi.ei_now(context));
            return true;
        }
    }
    internal bool Scroll(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentException("Invalid scroll delta.");
        lock (gate)
        {
            var device = FindDevice(16); if (device == 0) return false;
            if (x == 0 && y == 0) NativeEi.ei_device_scroll_stop(device, true, true);
            else NativeEi.ei_device_scroll_delta(device, x, y);
            NativeEi.ei_device_frame(device, NativeEi.ei_now(context));
            return true;
        }
    }
    internal bool Text(string text)
    {
        HostControlInput.ValidateText(text);
        lock (gate)
        {
            var device = FindDevice(64); if (device == 0) return false;
            // Only resolve this libei >= 1.6 entry point when the server offers text.
            NativeEi.ei_device_text_utf8(device, text);
            NativeEi.ei_device_frame(device, NativeEi.ei_now(context));
            return true;
        }
    }
    private void ForgetInput(nint device)
    {
        foreach (var key in heldInput.Where(p => p.Value == device).Select(p => p.Key).ToArray()) heldInput.Remove(key);
    }
    private void ReleaseInput()
    {
        foreach (var ((cap, code), device) in heldInput)
        {
            if (disconnected || !devices.GetValueOrDefault(device)) continue;
            if (cap == 32) NativeEi.ei_device_button_button(device, code, false);
            else NativeEi.ei_device_keyboard_key(device, code, false);
        }
        foreach (var device in heldInput.Values.Distinct())
            if (!disconnected && devices.GetValueOrDefault(device)) NativeEi.ei_device_frame(device, NativeEi.ei_now(context));
        heldInput.Clear();
    }
}
