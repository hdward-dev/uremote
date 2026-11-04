using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using URemote.Core;

namespace URemote.Linux;

// Windows host desktop: DXGI desktop duplication for frames, SendInput for keyboard and pointer.
// Display ids are stable indexes into EnumDisplayMonitors order, surfaced as wl_output globals
// so the existing host session can treat Windows like the other desktop backends.
public static class WindowsDesktop
{
    private static readonly object Gate = new();
    private static readonly Dictionary<uint, Capture> Captures = [];
    private static readonly HashSet<ushort> Keys = [];
    private static bool hosting;

    public static Task<IReadOnlyList<WaylandGlobal>> DiscoverAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var ids = Monitors();
        if (ids.Count == 0) throw new IOException("无法读取 Windows 显示器。");
        return Task.FromResult<IReadOnlyList<WaylandGlobal>>(ids.Select(id => new WaylandGlobal(id, "wl_output", 1)).ToArray());
    }

    public static Task<IAsyncDisposable> OpenAsync(bool input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (Gate)
        {
            if (hosting) throw new InvalidOperationException("已有 Windows 桌面共享会话。");
            hosting = true;
        }
        return Task.FromResult<IAsyncDisposable>(new Lease());
    }

    public static Task<CapturedScreen> CaptureAsync(uint display, bool cursor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            // Desktop Duplication can return an all-zero texture on the first acquire, and a
            // second copy of the same output is refused while the host is already streaming.
            // Previews only need one still, so take that through GDI.
            if (!cursor) return CaptureStill(display);
            lock (Gate)
            {
                if (!Captures.TryGetValue(display, out var capture))
                {
                    capture = new Capture(display);
                    Captures.Add(display, capture);
                }
                var frame = capture.Next(cursor);
                if (!hosting) { capture.Dispose(); Captures.Remove(display); }
                return frame;
            }
        }, ct);
    }

    private static CapturedScreen CaptureStill(uint display)
    {
        var bounds = Bounds(display);
        var width = Math.Max(1, bounds.Right - bounds.Left);
        var height = Math.Max(1, bounds.Bottom - bounds.Top);
        var stride = width * 4;
        var dc = GetDC(0);
        var memory = CreateCompatibleDC(dc);
        var info = new BitmapInfo { Size = Marshal.SizeOf<BitmapInfo>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var bitmap = CreateDIBSection(memory, ref info, 0, out var bits, 0, 0);
        var previous = bitmap == 0 ? 0 : SelectObject(memory, bitmap);
        try
        {
            if (bitmap == 0 || !BitBlt(memory, 0, 0, width, height, dc, bounds.Left, bounds.Top, 0x00CC0020))
                throw new IOException("Windows 屏幕暂时没有新画面。");
            var pixels = new byte[stride * height];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return new CapturedScreen(display, (uint)width, (uint)height, (uint)stride, 0, false, pixels);
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (dc != 0) ReleaseDC(0, dc);
        }
    }

    public static Task<IReadOnlyList<DesktopOutput>> ReadOutputsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = new List<DesktopOutput>(); var index = 0u;
        MonitorEnumProc callback = (nint handle, nint _, ref Rect _, nint _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(handle, ref info))
            {
                var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
                var rate = EnumDisplaySettings(info.DeviceName, -1, ref mode) && mode.DisplayFrequency > 0 ? checked(mode.DisplayFrequency * 1000) : 0;
                result.Add(new(index, "windows:" + info.DeviceName, info.DeviceName, rate));
            }
            index++; return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0)) throw new Win32Exception();
        GC.KeepAlive(callback);
        return Task.FromResult<IReadOnlyList<DesktopOutput>>(result);
    }

    public static Task<IReadOnlyDictionary<uint, int>> RefreshAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = new Dictionary<uint, int>();
        var index = 0u;
        MonitorEnumProc callback = (nint handle, nint _, ref Rect _, nint _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(handle, ref info))
            {
                var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
                if (EnumDisplaySettings(info.DeviceName, -1, ref mode) && mode.DisplayFrequency > 0)
                    result[index] = checked(mode.DisplayFrequency * 1000);
            }
            index++;
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0)) throw new Win32Exception();
        GC.KeepAlive(callback);
        return Task.FromResult<IReadOnlyDictionary<uint, int>>(result);
    }

    public static string? WallpaperPath()
    {
        var buffer = new StringBuilder(32768);
        return SystemParametersInfo(0x0073, (uint)buffer.Capacity, buffer, 0) && buffer.Length > 0 ? buffer.ToString() : null;
    }

    public static Task TypeTextAsync(string text, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        HostControlInput.ValidateText(text);
        var inputs = new Input[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = Unicode(text[i], false);
            inputs[i * 2 + 1] = Unicode(text[i], true);
        }
        Send(inputs);
        return Task.CompletedTask;
    }

    public static Task<bool> ApplyDesktopActionAsync(HostDesktopAction action, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (action is not (HostDesktopAction.ShowDesktop or HostDesktopAction.ShowWindows)) return Task.FromResult(false);
        // Win+D toggles the desktop; Windows has no separate "show all windows" chord here.
        var chord = new[] { Key(0x5B, false), Key(0x44, false), Key(0x44, true), Key(0x5B, true) };
        return Task.FromResult(Send(chord) == chord.Length);
    }

    public sealed class Keyboard : IDesktopKeyboard
    {
        private readonly HashSet<ushort> held = [];
        public Task ApplyAsync(HostKeyMessage message, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (message.MacKey is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(message));
            if (MacToVirtual.From(message.MacKey) is not { } virtualKey) return Task.CompletedTask;
            lock (Gate)
            {
                if (message.Action != KeyAction.Release)
                {
                    held.Add(virtualKey); Keys.Add(virtualKey);
                    Send([Key(virtualKey, false)]);
                }
                if (message.Action != KeyAction.Press)
                {
                    held.Remove(virtualKey); Keys.Remove(virtualKey);
                    Send([Key(virtualKey, true)]);
                }
            }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            lock (Gate)
            {
                if (held.Count > 0) Send(held.Select(key => Key(key, true)).ToArray());
                foreach (var key in held) Keys.Remove(key);
                held.Clear();
            }
            return ValueTask.CompletedTask;
        }
    }

    public sealed class Pointer(uint display) : IDesktopPointer
    {
        private readonly HashSet<int> held = [];
        public Task ApplyAsync(HostMouseMessage message, PointerCoordinates coordinates, uint width, uint height, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var bounds = Bounds(display);
            var input = new Input { Type = 0 };
            input.Data.Mouse.Flags = 0x8000 | 0x0001; // ABSOLUTE | MOVE
            if (message.Action == MouseAction.MoveAbsolute)
            {
                var x = message.X; var y = message.Y;
                if (coordinates == PointerCoordinates.WindowsPixels)
                { x /= Math.Max(1, width - 1); y /= Math.Max(1, height - 1); }
                x = Math.Clamp(x, 0, 1); y = Math.Clamp(y, 0, 1);
                var pixelX = bounds.Left + x * Math.Max(1, bounds.Right - bounds.Left - 1);
                var pixelY = bounds.Top + y * Math.Max(1, bounds.Bottom - bounds.Top - 1);
                var desktop = Desktop();
                input.Data.Mouse.X = (int)Math.Round((pixelX - desktop.Left) * 65535d / Math.Max(1, desktop.Right - desktop.Left - 1));
                input.Data.Mouse.Y = (int)Math.Round((pixelY - desktop.Top) * 65535d / Math.Max(1, desktop.Bottom - desktop.Top - 1));
            }
            else if (message.Action == MouseAction.Scroll)
            {
                input.Data.Mouse.Flags = message.X != 0 ? 0x1000u : 0x0800u;
                input.Data.Mouse.Data = (uint)(short)Math.Clamp((message.X != 0 ? message.X : -message.Y) * 120, -32768, 32767);
            }
            else
            {
                var button = message.Button switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, 16 => 4, _ => 0 };
                var down = message.Action is MouseAction.Press or MouseAction.Click;
                var up = message.Action is MouseAction.Release or MouseAction.Click;
                input.Data.Mouse.Flags = button switch
                {
                    0 => (down ? 0x0002u : 0) | (up ? 0x0004u : 0),
                    1 => (down ? 0x0008u : 0) | (up ? 0x0010u : 0),
                    _ => (down ? 0x0020u : 0) | (up ? 0x0040u : 0)
                };
                if (button >= 2) input.Data.Mouse.Data = button == 3 ? 1u : 2u;
                if (down) held.Add(button);
                if (up) held.Remove(button);
            }
            Send([input]);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            if (held.Count == 0) return ValueTask.CompletedTask;
            var release = held.Select(button =>
            {
                var input = new Input { Type = 0 };
                input.Data.Mouse.Flags = button switch { 0 => 0x0004u, 1 => 0x0010u, _ => 0x0040u };
                if (button >= 2) input.Data.Mouse.Data = button == 3 ? 1u : 2u;
                return input;
            }).ToArray();
            held.Clear();
            Send(release);
            return ValueTask.CompletedTask;
        }
    }

    private static List<uint> Monitors()
    {
        var ids = new List<uint>();
        var index = 0u;
        MonitorEnumProc callback = (nint _, nint _, ref Rect _, nint _) => { ids.Add(index++); return true; };
        if (!EnumDisplayMonitors(0, 0, callback, 0)) throw new Win32Exception();
        GC.KeepAlive(callback);
        return ids;
    }

    private static Rect Bounds(uint display)
    {
        Rect? found = null;
        var index = 0u;
        MonitorEnumProc callback = (nint _, nint _, ref Rect rect, nint _) =>
        {
            if (index == display) found = rect;
            index++;
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0)) throw new Win32Exception();
        GC.KeepAlive(callback);
        return found ?? throw new ArgumentException("所选显示器不可用。");
    }

    private static Rect Desktop()
    {
        var left = GetSystemMetrics(76); var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78); var height = GetSystemMetrics(79);
        return new Rect { Left = left, Top = top, Right = left + width, Bottom = top + height };
    }

    private static uint Send(Input[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length) throw new Win32Exception();
        return sent;
    }

    private static Input Key(ushort virtualKey, bool up) => new()
    {
        Type = 1,
        Data = new InputData { Keyboard = new KeyInput { VirtualKey = virtualKey, Flags = up ? 0x0002u : 0, Scan = (ushort)MapVirtualKey(virtualKey, 0) } }
    };

    private static Input Unicode(char value, bool up) => new()
    {
        Type = 1,
        Data = new InputData { Keyboard = new KeyInput { Scan = value, Flags = 0x0004u | (up ? 0x0002u : 0) } }
    };

    private sealed class Lease : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            lock (Gate)
            {
                hosting = false;
                foreach (var capture in Captures.Values) capture.Dispose();
                Captures.Clear();
                if (Keys.Count > 0)
                {
                    try { Send(Keys.Select(key => Key(key, true)).ToArray()); } catch (Win32Exception) { }
                    Keys.Clear();
                }
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Capture : IDisposable
    {
        private readonly uint display;
        private readonly nint factory, adapter, output, output1, duplication, device, context, staging;
        private readonly uint width, height;
        private bool disposed;
        public Capture(uint display)
        {
            this.display = display;
            var bounds = Bounds(display);
            width = (uint)Math.Max(1, bounds.Right - bounds.Left);
            height = (uint)Math.Max(1, bounds.Bottom - bounds.Top);
            var factoryGuid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            Check(CreateDXGIFactory1(ref factoryGuid, out factory));
            adapter = AdapterFor(factory, bounds);
            var levels = new[] { 0xb000, 0xa100, 0xa000 };
            Check(D3D11CreateDevice(adapter, 0, 0, 0x20, levels, (uint)levels.Length, 7, out device, out _, out context));
            output = OutputFor(adapter, bounds);
            var output1Guid = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
            Check(Com.QueryInterface(output, ref output1Guid, out output1));
            var hr = Com.Call(output1, 22, device, out duplication);
            if (hr < 0) throw new DesktopSessionClosedException("Windows 屏幕采集无法启动，请确认当前会话可以访问桌面。");
            var description = new TextureDesc { Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = 87, SampleCount = 1, Usage = 3, CpuAccess = 0x20000 };
            Check(Com.Call(device, 5, ref description, 0, out staging));
        }
        public CapturedScreen Next(bool cursor)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var info = Marshal.AllocHGlobal(Marshal.SizeOf<OutduplFrameInfo>());
            nint resource = 0;
            try
            {
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    var hr = Com.Call(duplication, 8, 1000u, info, out resource);
                    if (hr == unchecked((int)0x887A0026)) throw new DesktopSessionClosedException("Windows 屏幕共享已结束，请重新开启被控。");
                    if (hr == unchecked((int)0x887A0027)) continue;
                    if (hr < 0) throw new Win32Exception(hr);
                    break;
                }
                if (resource == 0) throw new IOException("Windows 屏幕暂时没有新画面。");
                var resourceGuid = new Guid("dc8e63f3-d12b-4952-b47b-5e45026a862d");
                Check(Com.QueryInterface(resource, ref resourceGuid, out var texture));
                try { Com.Call(context, 47, staging, texture); }
                finally { Marshal.Release(texture); }
                var mapped = Marshal.AllocHGlobal(Marshal.SizeOf<MappedSubresource>());
                try
                {
                    Check(Com.Call(context, 14, staging, 0u, 1, 0u, mapped));
                    var value = Marshal.PtrToStructure<MappedSubresource>(mapped);
                    var stride = value.RowPitch;
                    var size = checked((int)(stride * height));
                    var pixels = new byte[size];
                    for (var row = 0; row < height; row++)
                        Marshal.Copy(value.Data + row * (int)stride, pixels, row * (int)stride, (int)stride);
                    Com.Call(context, 15, staging, 0u);
                    if (cursor) DrawCursor(pixels, stride);
                    return new CapturedScreen(display, width, height, stride, 0, false, pixels);
                }
                finally { Marshal.FreeHGlobal(mapped); }
            }
            finally
            {
                if (resource != 0) { Com.Call(duplication, 14); Marshal.Release(resource); }
                Marshal.FreeHGlobal(info);
            }
        }
        private void DrawCursor(byte[] pixels, uint stride)
        {
            var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
            if (!GetCursorInfo(ref info) || info.Flags != 1 || info.Handle == 0) return;
            var icon = new IconInfo();
            if (!GetIconInfo(info.Handle, ref icon)) return;
            nint dc = 0, color = 0, previous = 0;
            try
            {
                var bounds = Bounds(display);
                var originX = info.X - bounds.Left - icon.HotspotX;
                var originY = info.Y - bounds.Top - icon.HotspotY;
                if (!GetObject(icon.Color != 0 ? icon.Color : icon.Mask, Marshal.SizeOf<BitmapHeader>(), out var header) || header.Width <= 0 || header.Height == 0)
                    return;
                var cursorHeight = icon.Color != 0 ? header.Height : header.Height / 2;
                dc = CreateCompatibleDC(0);
                var bitmap = new BitmapInfo { Size = Marshal.SizeOf<BitmapInfo>(), Width = header.Width, Height = -cursorHeight, Planes = 1, BitCount = 32, Compression = 0 };
                color = CreateDIBSection(dc, ref bitmap, 0, out var bits, 0, 0);
                if (color == 0) return;
                previous = SelectObject(dc, color);
                DrawIconEx(dc, 0, 0, info.Handle, header.Width, cursorHeight, 0, 0, 3);
                var cursor = new byte[header.Width * cursorHeight * 4];
                Marshal.Copy(bits, cursor, 0, cursor.Length);
                for (var row = 0; row < cursorHeight; row++)
                {
                    var target = originY + row;
                    if (target < 0 || target >= height) continue;
                    for (var column = 0; column < header.Width; column++)
                    {
                        var targetX = originX + column;
                        if (targetX < 0 || targetX >= width) continue;
                        var source = (row * header.Width + column) * 4;
                        if (cursor[source + 3] < 32) continue;
                        var offset = target * (int)stride + targetX * 4;
                        pixels[offset] = cursor[source]; pixels[offset + 1] = cursor[source + 1]; pixels[offset + 2] = cursor[source + 2]; pixels[offset + 3] = 255;
                    }
                }
            }
            finally
            {
                if (previous != 0 && dc != 0) SelectObject(dc, previous);
                if (color != 0) DeleteObject(color);
                if (dc != 0) DeleteDC(dc);
                if (icon.Mask != 0) DeleteObject(icon.Mask);
                if (icon.Color != 0) DeleteObject(icon.Color);
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Release(staging); Release(duplication); Release(output1); Release(output); Release(context); Release(device); Release(adapter); Release(factory);
            static void Release(nint handle) { if (handle != 0) Marshal.Release(handle); }
        }
    }

    private static nint AdapterFor(nint factory, Rect bounds)
    {
        for (uint index = 0; ; index++)
        {
            var hr = Com.Call(factory, 12, index, out nint adapter);
            if (hr == unchecked((int)0x887A0002)) throw new ArgumentException("所选显示器不可用。");
            if (hr < 0) throw new Win32Exception(hr);
            if (HasOutput(adapter, bounds)) return adapter;
            Marshal.Release(adapter);
        }
    }

    private static nint OutputFor(nint adapter, Rect bounds)
    {
        for (uint index = 0; ; index++)
        {
            var hr = Com.Call(adapter, 7, index, out nint output);
            if (hr == unchecked((int)0x887A0002)) throw new ArgumentException("所选显示器不可用。");
            if (hr < 0) throw new Win32Exception(hr);
            if (Matches(output, bounds)) return output;
            Marshal.Release(output);
        }
    }

    private static bool HasOutput(nint adapter, Rect bounds)
    {
        for (uint index = 0; Com.Call(adapter, 7, index, out nint output) >= 0; index++)
        {
            var match = Matches(output, bounds);
            Marshal.Release(output);
            if (match) return true;
        }
        return false;
    }

    private static bool Matches(nint output, Rect bounds)
    {
        var description = Marshal.AllocHGlobal(Marshal.SizeOf<OutputDesc>());
        try
        {
            if (Com.Call(output, 7, description) < 0) return false;
            var value = Marshal.PtrToStructure<OutputDesc>(description);
            return value.Left == bounds.Left && value.Top == bounds.Top;
        }
        finally { Marshal.FreeHGlobal(description); }
    }

    private static void Check(int hr) { if (hr < 0) throw new Win32Exception(hr); }

    private static class MacToVirtual
    {
        private static readonly Dictionary<int, ushort> Map = new()
        {
            [51] = 0x08, [48] = 0x09, [36] = 0x0D, [56] = 0xA0, [60] = 0xA1, [59] = 0xA2,
            [62] = 0xA3, [58] = 0xA4, [61] = 0xA5, [57] = 0x14, [53] = 0x1B, [49] = 0x20,
            [116] = 0x21, [121] = 0x22, [119] = 0x23, [115] = 0x24, [123] = 0x25, [126] = 0x26,
            [124] = 0x27, [125] = 0x28, [114] = 0x2D, [117] = 0x2E, [122] = 0x70, [120] = 0x71,
            [99] = 0x72, [118] = 0x73, [96] = 0x74, [97] = 0x75, [98] = 0x76, [100] = 0x77,
            [101] = 0x78, [109] = 0x79, [103] = 0x7A, [111] = 0x7B, [55] = 0x5B, [54] = 0x5C,
            [71] = 0x90, [82] = 0x60, [83] = 0x61, [84] = 0x62, [85] = 0x63, [86] = 0x64,
            [87] = 0x65, [88] = 0x66, [89] = 0x67, [91] = 0x68, [92] = 0x69, [65] = 0x6E,
            [67] = 0x6A, [69] = 0x6B, [75] = 0x6F, [78] = 0x6D, [41] = 0xBA, [24] = 0xBB,
            [43] = 0xBC, [27] = 0xBD, [47] = 0xBE, [44] = 0xBF, [50] = 0xC0, [33] = 0xDB,
            [42] = 0xDC, [30] = 0xDD, [39] = 0xDE
        };
        private static readonly Dictionary<int, ushort> Letters = new()
        {
            [0] = 0x41, [11] = 0x42, [8] = 0x43, [2] = 0x44, [14] = 0x45, [3] = 0x46, [5] = 0x47,
            [4] = 0x48, [34] = 0x49, [38] = 0x4A, [40] = 0x4B, [37] = 0x4C, [46] = 0x4D, [45] = 0x4E,
            [31] = 0x4F, [35] = 0x50, [12] = 0x51, [15] = 0x52, [1] = 0x53, [17] = 0x54, [32] = 0x55,
            [9] = 0x56, [13] = 0x57, [7] = 0x58, [16] = 0x59, [6] = 0x5A
        };
        private static readonly Dictionary<int, ushort> Digits = new()
        {
            [29] = 0x30, [18] = 0x31, [19] = 0x32, [20] = 0x33, [21] = 0x34, [23] = 0x35,
            [22] = 0x36, [26] = 0x37, [28] = 0x38, [25] = 0x39
        };
        public static ushort? From(int mac) => Map.TryGetValue(mac, out var key) ? key : Letters.TryGetValue(mac, out key) ? key : Digits.TryGetValue(mac, out key) ? key : null;
    }

    private static class Com
    {
        public static int QueryInterface(nint instance, ref Guid iid, out nint result)
        {
            var address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), 0);
            var query = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(address);
            return query(instance, ref iid, out result);
        }
        public static int Call(nint instance, int slot, uint a, nint b, out nint c)
        {
            var address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
            var method = Marshal.GetDelegateForFunctionPointer<CallUintPtrOutDelegate>(address);
            return method(instance, a, b, out c);
        }
        public static int Call(nint instance, int slot, uint a, out nint b)
        {
            var address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
            var method = Marshal.GetDelegateForFunctionPointer<CallUintOutDelegate>(address);
            return method(instance, a, out b);
        }
        public static int Call(nint instance, int slot, nint a, out nint b)
        {
            var address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
            var method = Marshal.GetDelegateForFunctionPointer<CallPtrOutDelegate>(address);
            return method(instance, a, out b);
        }
        public static int Call(nint instance, int slot, ref TextureDesc a, nint b, out nint c)
        {
            var address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
            var method = Marshal.GetDelegateForFunctionPointer<CallTextureDelegate>(address);
            return method(instance, ref a, b, out c);
        }
        public static void Call(nint instance, int slot, nint a, nint b)
        {
            var address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
            Marshal.GetDelegateForFunctionPointer<CallPtrPtrDelegate>(address)(instance, a, b);
        }
        public static int Call(nint instance, int slot, nint a, uint b, int c, uint d, nint e)
        {
            var address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<CallMapDelegate>(address)(instance, a, b, c, d, e);
        }
        public static void Call(nint instance, int slot, nint a, uint b)
        {
            var address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
            Marshal.GetDelegateForFunctionPointer<CallPtrUintDelegate>(address)(instance, a, b);
        }
        public static int Call(nint instance, int slot, nint a)
        {
            var address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<CallPtrDelegate>(address)(instance, a);
        }
        public static int Call(nint instance, int slot)
        {
            var address = Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<CallDelegate>(address)(instance);
        }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterfaceDelegate(nint self, ref Guid iid, out nint result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CallUintPtrOutDelegate(nint self, uint a, nint b, out nint c);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CallUintOutDelegate(nint self, uint a, out nint b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CallPtrOutDelegate(nint self, nint a, out nint b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CallTextureDelegate(nint self, ref TextureDesc a, nint b, out nint c);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CallPtrPtrDelegate(nint self, nint a, nint b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CallMapDelegate(nint self, nint a, uint b, int c, uint d, nint e);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CallPtrUintDelegate(nint self, nint a, uint b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CallPtrDelegate(nint self, nint a);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CallDelegate(nint self);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DevMode { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName; public short SpecVersion, DriverVersion, Size, DriverExtra; public int Fields; public int PositionX, PositionY, DisplayOrientation, DisplayFixedOutput; public short Color, Duplex, YResolution, TTOption, Collate; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName; public short LogPixels; public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public int Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyInput { public ushort VirtualKey, Scan; public uint Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size; public int Flags; public nint Handle; public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool IsIcon; public int HotspotX, HotspotY; public nint Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader { public int Type; public int Width, Height; public int WidthBytes, Planes, BitCount; public nint Bits; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public int Size; public int Width, Height; public short Planes, BitCount; public int Compression, ImageSize; public int XPels, YPels, Used, Important; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct OutputDesc { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName; public int Left, Top, Right, Bottom; public int Attached; public uint Rotation; public nint Monitor; }
    [StructLayout(LayoutKind.Sequential)] private struct OutduplFrameInfo { public long LastPresentTime, LastMouseUpdateTime; public uint AccumulatedFrames; public int RectsCoalesced; public int ProtectedContentMaskedOut; public int PointerVisible, PointerX, PointerY; public uint TotalMetadata, PointerShape; }
    [StructLayout(LayoutKind.Sequential)] private struct TextureDesc { public uint Width, Height, MipLevels, ArraySize, Format; public uint SampleCount, SampleQuality, Usage, Bind, CpuAccess, Misc; }
    [StructLayout(LayoutKind.Sequential)] private struct MappedSubresource { public nint Data; public uint RowPitch, DepthPitch; }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, ref Rect rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode devMode);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint map);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SystemParametersInfo(uint action, uint param, StringBuilder buffer, uint winIni);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint cursor, ref IconInfo info);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("gdi32.dll")] private static extern bool GetObject(nint handle, int size, out BitmapHeader header);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint handle);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out nint factory);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, int driver, nint software, int flags, int[] levels, uint levelCount, uint sdk, out nint device, out int feature, out nint context);
}
