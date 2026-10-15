using System.Runtime.InteropServices;
using URemote.Core;

namespace URemote.Linux;

public static class MacDesktop
{
    private const string Library = "uremote-mac";
    private static readonly object Gate = new();
    private static readonly Dictionary<(uint, bool), nint> Captures = [];
    private static readonly HashSet<int> Keys = [];
    private static bool hosting;
    static MacDesktop()
    {
        NativeLibrary.SetDllImportResolver(typeof(MacDesktop).Assembly, (name, assembly, path) =>
        {
            if (name != Library || !OperatingSystem.IsMacOS()) return 0;
            var directory = Path.GetDirectoryName(assembly.Location)!;
            var rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            var file = Path.Combine(directory, "native", rid, "liburemote-mac.dylib");
            return NativeLibrary.TryLoad(file, out var handle) ? handle : 0;
        });
    }
    public static Task<IReadOnlyList<WaylandGlobal>> DiscoverAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOSVersionAtLeast(12, 3)) throw new NotSupportedException("macOS 被控需要 macOS 12.3 或更新版本。");
        var ids = new uint[32]; var count = ur_displays(ids, ids.Length);
        if (count < 0) throw new IOException("无法读取 macOS 显示器。");
        return Task.FromResult<IReadOnlyList<WaylandGlobal>>(ids.Take(count).Select(id => new WaylandGlobal(id, "wl_output", 1)).ToArray());
    }
    public static Task<IAsyncDisposable> OpenAsync(bool input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var permission = ur_permissions(input ? 1 : 0, 1);
        if ((permission & 1) == 0) throw new NotSupportedException("请在 macOS 系统设置 → 隐私与安全性 → 屏幕录制中允许星栈，然后重新开启被控；系统可能要求重启应用。");
        if (input && (permission & 2) == 0) throw new NotSupportedException("请在 macOS 系统设置 → 隐私与安全性 → 辅助功能中允许星栈，然后重新开启被控。");
        lock(Gate) { if(hosting) throw new InvalidOperationException("已有 macOS 桌面共享会话。"); hosting=true; }
        return Task.FromResult<IAsyncDisposable>(new Lease());
    }
    public static async Task<CapturedScreen> CaptureAsync(uint display, bool cursor, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(25));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var frame = await Task.Run(() =>
            {
                lock (Gate)
                {
                    if (!Captures.TryGetValue((display,cursor), out var handle))
                    {
                        handle = ur_capture_open(display,cursor ? 1 : 0);
                        if (handle == 0) throw new DesktopSessionClosedException("macOS 屏幕采集无法启动，请检查屏幕录制权限。");
                        Captures.Add((display,cursor),handle);
                    }
                    var size = ur_capture_copy(handle, null, 0, out var width, out var height);
                    if (size < 0) throw new DesktopSessionClosedException("macOS 屏幕共享已结束。");
                    if (size == 0) return null;
                    if (width == 0 || height == 0 || (ulong)width*height*4 != (ulong)size || size > 256*1024*1024)
                        throw new InvalidDataException("Invalid Mac capture dimensions.");
                    var pixels = new byte[size];
                    var copied = ur_capture_copy(handle,pixels,size,out var actualWidth,out var actualHeight);
                    if (copied != size || width != actualWidth || height != actualHeight) { Array.Clear(pixels); return null; }
                    if (!hosting) { ur_capture_close(handle); Captures.Remove((display,cursor)); }
                    return new CapturedScreen(display,width,height,width*4,0,false,pixels);
                }
            }, timeout.Token);
            if (frame is not null) return frame;
            await Task.Delay(10,timeout.Token);
        }
    }
    // DesktopBackend uses millihertz; CoreGraphics returns hertz.
    public static async Task<IReadOnlyDictionary<uint,int>> RefreshAsync(CancellationToken ct)
        => (await DiscoverAsync(ct)).ToDictionary(x => x.Name,x => checked(ur_refresh(x.Name) * 1000));
    public static string? WallpaperPath()
    {
        var buffer = new byte[32768];
        var length = ur_wallpaper_path(buffer, buffer.Length);
        return length > 1 && length <= buffer.Length
            ? System.Text.Encoding.UTF8.GetString(buffer, 0, length - 1) : null;
    }
    [DllImport(Library)] private static extern int ur_wallpaper_path([Out] byte[] buffer, int capacity);
    public static void RenderWallpaper(string source, string destination)
    {
        if (ur_wallpaper_render(source, destination) != 1) throw new IOException("macOS wallpaper rendering failed.");
    }
    [DllImport(Library)] private static extern int ur_wallpaper_render(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
    public static async Task<bool> ApplyDesktopActionAsync(HostDesktopAction action, CancellationToken ct)
    {
        // macOS Mission Control's own entry point: 0 = all windows, 1 = desktop.
        // This asks Dock directly and is independent of disabled/remapped function keys.
        var argument = action switch { HostDesktopAction.ShowDesktop => "1", HostDesktopAction.ShowWindows => "0",
            _ => throw new ArgumentOutOfRangeException(nameof(action)) };
        // The protected system executable is terminated when launched directly on macOS 26.
        // LaunchServices must launch its application bundle.
        var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/open")
            { UseShellExecute = false };
        start.ArgumentList.Add("-a");
        start.ArgumentList.Add("/System/Applications/Mission Control.app");
        start.ArgumentList.Add("--args");
        start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Cannot start macOS desktop action.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(timeout.Token); return process.ExitCode == 0; }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return false;
        }
    }
    private static ulong Flags => Keys.Aggregate(0UL,(flags,key) => flags | (key switch {
        56 or 60 => 1UL<<17, 59 or 62 => 1UL<<18, 58 or 61 => 1UL<<19, 54 or 55 => 1UL<<20, _ => 0UL }));
    public static Task TypeTextAsync(string text,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); HostControlInput.ValidateText(text);
        var chars=text.Select(c => (ushort)c).ToArray();
        try { lock(Gate) ur_text(chars,chars.Length); } finally { Array.Clear(chars); }
        return Task.CompletedTask;
    }
    public sealed class Keyboard : IDesktopKeyboard
    {
        private readonly HashSet<int> held=[];
        public Task ApplyAsync(HostKeyMessage message,CancellationToken ct=default)
        {
            ct.ThrowIfCancellationRequested();
            if (message.MacKey is <0 or >127) throw new ArgumentOutOfRangeException(nameof(message));
            lock(Gate)
            {
                if(message.Action != KeyAction.Release) { held.Add(message.MacKey); Keys.Add(message.MacKey); ur_key(message.MacKey,1,Flags); }
                if(message.Action != KeyAction.Press) { held.Remove(message.MacKey); Keys.Remove(message.MacKey); ur_key(message.MacKey,0,Flags); }
            }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            lock(Gate) { foreach(var key in held) { Keys.Remove(key); ur_key(key,0,Flags); } held.Clear(); }
            return ValueTask.CompletedTask;
        }
    }
    public sealed class Pointer(uint display) : IDesktopPointer
    {
        private readonly HashSet<int> held=[];
        public Task ApplyAsync(HostMouseMessage message,PointerCoordinates coordinates,uint width,uint height,CancellationToken ct=default)
        {
            ct.ThrowIfCancellationRequested();
            var x=message.X; var y=message.Y;
            if (message.Action==MouseAction.MoveAbsolute && coordinates==PointerCoordinates.WindowsPixels)
            { x/=Math.Max(1,width-1); y/=Math.Max(1,height-1); }
            var button=message.Button switch { 1=>0,2=>1,4=>2,8=>3,16=>4,_=>0 };
            lock(Gate)
            {
                if(message.Action is MouseAction.Press or MouseAction.Click) { held.Add(button); ur_pointer(display,1,0,0,button,-1,Flags); }
                if(message.Action is MouseAction.Release or MouseAction.Click) { held.Remove(button); ur_pointer(display,2,0,0,button,-1,Flags); }
                if(message.Action is MouseAction.MoveAbsolute or MouseAction.Scroll)
                    ur_pointer(display,(int)message.Action,x,y,button,held.Count>0?held.Min():-1,Flags);
            }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { lock(Gate) { foreach(var button in held) ur_pointer(display,2,0,0,button,-1,Flags); held.Clear(); } return ValueTask.CompletedTask; }
    }
    private sealed class Lease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { lock(Gate) { hosting=false; foreach(var handle in Captures.Values) ur_capture_close(handle); Captures.Clear(); } return ValueTask.CompletedTask; }
    }
    [DllImport(Library)] private static extern int ur_permissions(int input,int prompt);
    [DllImport(Library)] private static extern int ur_displays([Out] uint[] ids,int capacity);
    [DllImport(Library)] private static extern int ur_refresh(uint display);
    [DllImport(Library)] private static extern nint ur_capture_open(uint display,int cursor);
    [DllImport(Library)] private static extern int ur_capture_copy(nint handle,[Out] byte[]? target,int capacity,out uint width,out uint height);
    [DllImport(Library)] private static extern void ur_capture_close(nint handle);
    [DllImport(Library)] private static extern void ur_key(int key,int down,ulong flags);
    [DllImport(Library)] private static extern void ur_text([In] ushort[] text,int length);
    [DllImport(Library)] private static extern void ur_pointer(uint display,int action,double x,double y,int button,int dragging,ulong flags);
}
