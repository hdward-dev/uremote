using System.Runtime.InteropServices;
using System.Threading.Channels;
using URemote.Core;

namespace URemote.Module;

public sealed class RdpDesktopSession(Func<DesktopCertificate, CancellationToken, Task<bool>> verifyCertificate) : ILocalDesktopSession
{
    private readonly Channel<Action<IntPtr>> input = Channel.CreateBounded<Action<IntPtr>>(512);
    private volatile bool ready;
    public event Action<LocalDesktopFrame>? Frame;
    public event Action<string>? Status;

    public Task RunAsync(LocalDesktopOptions options, CancellationToken token) => Task.Run(() =>
    {
        options.Validate(); token.ThrowIfCancellationRequested();
        Native.FrameCallback frame = (data, width, height, stride) =>
        {
            // No exceptions may cross the unmanaged callback boundary.
            try
            {
                if (width < 1 || height < 1 || width > 8192 || height > 8192 || (long)width * height > 16 * 1024 * 1024 || stride < width * 4) return;
                var pixels = new byte[checked(width * height * 4)];
                for (var row = 0; row < height; row++) Marshal.Copy(IntPtr.Add(data, checked(row * stride)), pixels, row * width * 4, width * 4);
                for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                Frame?.Invoke(new(width, height, pixels));
            }
            catch { }
        };
        Native.CertificateCallback certificate = (host, subject, issuer, fingerprint, changed) =>
        {
            try { return verifyCertificate(new(host, subject, issuer, fingerprint, changed != 0), token).GetAwaiter().GetResult() ? 1 : 0; }
            catch { return 0; }
        };
        IntPtr instance;
        try { instance = Native.Create(options.Host, (uint)options.Port, options.Username, options.Password, options.Domain, frame, certificate); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { throw new NotSupportedException("RDP 后端未安装或版本不兼容，请安装 U远程 FreeRDP 3 组件。", e); }
        if (instance == IntPtr.Zero) throw new InvalidOperationException("无法初始化 RDP 连接。");
        try
        {
            using var cancel = token.Register(() => Native.Abort(instance));
            Status?.Invoke("正在连接 RDP…");
            if (Native.Connect(instance) == 0)
            {
                token.ThrowIfCancellationRequested();
                throw new InvalidOperationException($"RDP 连接失败（{Native.Error(instance):X8}），请检查地址、账号、密码及证书。");
            }
            ready = true; Status?.Invoke("已连接 · RDP");
            while (!token.IsCancellationRequested)
            {
                // Protocol and input operations remain on the same FreeRDP thread.
                for (var i = 0; i < 128 && input.Reader.TryRead(out var action); i++) action(instance);
                var result = Native.Poll(instance);
                if (token.IsCancellationRequested) break;
                if (result == 0) break;
                if (result < 0) throw new IOException("RDP 连接已中断。");
            }
        }
        finally
        {
            ready = false; input.Writer.TryComplete(); Native.Free(instance);
            GC.KeepAlive(frame); GC.KeepAlive(certificate);
        }
    }, token);

    private void Queue(Action<IntPtr> action)
    {
        if (ready && !input.Writer.TryWrite(action)) throw new IOException("输入队列已满，请断开后重试。");
    }
    public void Pointer(int x, int y, int buttons, int wheel = 0) => Queue(instance =>
    {
        if (Native.Pointer(instance, (ushort)Math.Clamp(x, 0, 65535), (ushort)Math.Clamp(y, 0, 65535), buttons, wheel) == 0)
            throw new IOException("RDP 鼠标输入发送失败。");
    });
    public void Key(uint symbol, bool down) => Queue(instance =>
    {
        if (Native.Key(instance, symbol, down ? 1 : 0) == 0) throw new IOException("RDP 键盘输入发送失败。");
    });

    private static class Native
    {
        private const string Library = "uremote-rdp";
        static Native()
        {
            // Plugins live outside the host executable directory.
            NativeLibrary.SetDllImportResolver(typeof(RdpDesktopSession).Assembly, (name, assembly, path) =>
            {
                if (name != Library) return IntPtr.Zero;
                var file = OperatingSystem.IsWindows() ? "uremote-rdp.dll" : OperatingSystem.IsMacOS() ? "liburemote-rdp.dylib" : "liburemote-rdp.so";
                var directory = Path.GetDirectoryName(assembly.Location)!;
                var rid = RuntimeInformation.RuntimeIdentifier;
                if (NativeLibrary.TryLoad(Path.Combine(directory, "native", rid, file), out var handle)) return handle;
                return NativeLibrary.TryLoad(Path.Combine(directory, file), out handle) ? handle : IntPtr.Zero;
            });
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FrameCallback(IntPtr data, int width, int height, int stride);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CertificateCallback(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string host, [MarshalAs(UnmanagedType.LPUTF8Str)] string subject,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string issuer, [MarshalAs(UnmanagedType.LPUTF8Str)] string fingerprint, int changed);
        [DllImport(Library, EntryPoint = "urdp_create", CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr Create(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string host, uint port, [MarshalAs(UnmanagedType.LPUTF8Str)] string user,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string password, [MarshalAs(UnmanagedType.LPUTF8Str)] string domain, FrameCallback frame, CertificateCallback certificate);
        [DllImport(Library, EntryPoint = "urdp_connect", CallingConvention = CallingConvention.Cdecl)] internal static extern int Connect(IntPtr instance);
        [DllImport(Library, EntryPoint = "urdp_poll", CallingConvention = CallingConvention.Cdecl)] internal static extern int Poll(IntPtr instance);
        [DllImport(Library, EntryPoint = "urdp_error", CallingConvention = CallingConvention.Cdecl)] internal static extern uint Error(IntPtr instance);
        [DllImport(Library, EntryPoint = "urdp_abort", CallingConvention = CallingConvention.Cdecl)] internal static extern void Abort(IntPtr instance);
        [DllImport(Library, EntryPoint = "urdp_free", CallingConvention = CallingConvention.Cdecl)] internal static extern void Free(IntPtr instance);
        [DllImport(Library, EntryPoint = "urdp_pointer", CallingConvention = CallingConvention.Cdecl)] internal static extern int Pointer(IntPtr instance, ushort x, ushort y, int buttons, int wheel);
        [DllImport(Library, EntryPoint = "urdp_key", CallingConvention = CallingConvention.Cdecl)] internal static extern int Key(IntPtr instance, uint key, int down);
    }
}
