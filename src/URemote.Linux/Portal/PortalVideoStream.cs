using System.Diagnostics;
using System.Runtime.InteropServices;

namespace URemote.Linux;

internal static class NativeGst
{
    private const string Gst = "libgstreamer-1.0.so.0", App = "libgstapp-1.0.so.0", Video = "libgstvideo-1.0.so.0";
    [DllImport(Gst)] internal static extern int gst_init_check(nint argc, nint argv, out nint error);
    [DllImport(Gst)] internal static extern nint gst_element_factory_find(string name);
    [DllImport(Gst)] internal static extern nint gst_parse_launch(string description, out nint error);
    [DllImport(Gst)] internal static extern nint gst_bin_get_by_name(nint bin, string name);
    [DllImport(Gst)] internal static extern int gst_element_set_state(nint element, int state);
    [DllImport(Gst)] internal static extern void gst_object_unref(nint value);
    [DllImport(Gst)] internal static extern void gst_mini_object_unref(nint value);
    [DllImport(Gst)] internal static extern nint gst_element_get_bus(nint element);
    [DllImport(Gst)] internal static extern nint gst_bus_pop_filtered(nint bus, uint types);
    [DllImport(App)] internal static extern nuint gst_app_sink_get_type();
    [DllImport(App)] internal static extern nint gst_app_sink_try_pull_sample(nint sink, ulong timeout);
    [DllImport(App)] internal static extern int gst_app_sink_is_eos(nint sink);
    [DllImport(Gst)] internal static extern nint gst_sample_get_caps(nint sample);
    [DllImport(Gst)] internal static extern nint gst_sample_get_buffer(nint sample);
    [DllImport(Gst)] internal static extern nint gst_caps_get_structure(nint caps, uint index);
    [DllImport(Gst)] internal static extern nint gst_structure_get_string(nint structure, string name);
    [DllImport(Gst)] internal static extern nuint gst_buffer_get_size(nint buffer);
    [DllImport(Gst)] internal static extern nuint gst_buffer_extract(nint buffer, nuint offset, byte[] destination, nuint size);
    [DllImport(Video)] internal static extern nint gst_video_info_new();
    [DllImport(Video)] internal static extern int gst_video_info_from_caps(nint info, nint caps);
    [DllImport(Video)] internal static extern void gst_video_info_free(nint info);
    [DllImport(Video)] internal static extern nint gst_buffer_get_video_meta(nint buffer);

    // Public ABI prefixes through the first four planes. Pointer-sized fields follow native alignment.
    [StructLayout(LayoutKind.Sequential)] internal struct VideoInfo
    {
        internal nint Format;
        internal int Interlace, Flags, Width, Height;
        internal nuint Size;
        internal int Views, Chroma, Range, Matrix, Transfer, Primaries, ParN, ParD, FpsN, FpsD;
        internal nuint Offset0, Offset1, Offset2, Offset3;
        internal int Stride0, Stride1, Stride2, Stride3;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct VideoMeta
    {
        internal int MetaFlags;
        internal nint MetaInfo, Buffer;
        internal int Flags, Format, Id;
        internal uint Width, Height, Planes;
        internal nuint Offset0, Offset1, Offset2, Offset3;
        internal int Stride0, Stride1, Stride2, Stride3;
    }
    internal static void Initialize()
    {
        if (gst_init_check(0, 0, out var error) == 0) { NativeGlib.Check(error, "初始化 GStreamer"); throw new NotSupportedException("无法初始化 GStreamer。"); }
        NativeGlib.Check(error, "初始化 GStreamer");
    }
    internal static void Probe()
    {
        Initialize();
        foreach (var name in new[] { "pipewiresrc", "videoconvert", "appsink" })
        {
            var factory = gst_element_factory_find(name);
            if (factory == 0) throw new NotSupportedException("缺少 GStreamer 原生插件：" + name);
            gst_object_unref(factory);
        }
        // Check both native libraries as well as the plugins before showing an authorization dialog.
        var info = gst_video_info_new(); gst_video_info_free(info);
        _ = gst_app_sink_get_type();
    }
}

internal sealed class PortalVideoStream : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private nint pipeline, sink, bus;
    private CapturedScreen? cached;
    private readonly uint output;
    private bool disposed;
    internal PortalVideoStream(uint output, int fd) : this(output,
        $"pipewiresrc fd={fd} path={output} do-timestamp=true ! videoconvert ! video/x-raw,format=BGRA ! appsink name=frames max-buffers=1 drop=true sync=false") { }
    // Internal overload allows tests to use videotestsrc without a screen or Portal authorization.
    internal PortalVideoStream(uint output, string pipelineDescription)
    {
        this.output = output;
        NativeGst.Initialize();
        try
        {
            pipeline = NativeGst.gst_parse_launch(pipelineDescription, out var error);
            NativeGlib.Check(error, "创建屏幕管线");
            if (pipeline == 0) throw new IOException("无法创建屏幕管线。");
            sink = NativeGst.gst_bin_get_by_name(pipeline, "frames");
            bus = NativeGst.gst_element_get_bus(pipeline);
            if (sink == 0 || bus == 0) throw new IOException("无法创建屏幕接收器。");
            if (NativeGst.gst_element_set_state(pipeline, 4) == 0) throw new IOException("无法启动屏幕管线。");
        }
        catch { Close(); throw; }
    }
    internal async Task<CapturedScreen> CaptureAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await Task.Run(() => Capture(ct), ct);
        }
        finally { gate.Release(); }
    }
    private CapturedScreen Capture(CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        nint sample;
        do
        {
            ct.ThrowIfCancellationRequested();
            var error = NativeGst.gst_bus_pop_filtered(bus, 2); // GST_MESSAGE_ERROR
            if (error != 0) { NativeGst.gst_mini_object_unref(error); throw new IOException("PipeWire 屏幕管线已失败。"); }
            sample = NativeGst.gst_app_sink_try_pull_sample(sink, cached is null ? 50_000_000ul : 0ul);
            if (sample != 0) break;
            if (NativeGst.gst_app_sink_is_eos(sink) != 0) throw new DesktopSessionClosedException("屏幕共享已结束。");
            if (cached is not null) return cached with { Pixels = (byte[])cached.Pixels.Clone() };
            if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("等待首帧超时。");
        } while (true);
        try
        {
            var caps = NativeGst.gst_sample_get_caps(sample); var buffer = NativeGst.gst_sample_get_buffer(sample);
            if (caps == 0 || buffer == 0) throw new FormatException("无效的屏幕样本。");
            var structure = NativeGst.gst_caps_get_structure(caps, 0);
            if (Marshal.PtrToStringUTF8(NativeGst.gst_structure_get_string(structure, "format")) != "BGRA")
                throw new FormatException("屏幕未提供 BGRA 像素。");
            var infoPtr = NativeGst.gst_video_info_new();
            NativeGst.VideoInfo info;
            try
            {
                if (NativeGst.gst_video_info_from_caps(infoPtr, caps) == 0) throw new FormatException("无效的视频参数。");
                info = Marshal.PtrToStructure<NativeGst.VideoInfo>(infoPtr);
            }
            finally { NativeGst.gst_video_info_free(infoPtr); }
            var stride = info.Stride0; var offset = info.Offset0;
            var metaPtr = NativeGst.gst_buffer_get_video_meta(buffer);
            if (metaPtr != 0)
            {
                var meta = Marshal.PtrToStructure<NativeGst.VideoMeta>(metaPtr);
                if (meta.Planes != 1 || meta.Width != info.Width || meta.Height != info.Height) throw new FormatException("无效的视频平面。");
                stride = meta.Stride0; offset = meta.Offset0;
            }
            var size = NativeGst.gst_buffer_get_size(buffer);
            var length = ValidatePlane(info.Width, info.Height, stride, offset, size);
            var pixels = new byte[length];
            try
            {
                if (stride == info.Width * 4)
                {
                    if (NativeGst.gst_buffer_extract(buffer, offset, pixels, (nuint)length) != (nuint)length) throw new IOException("无法读取屏幕像素。");
                }
                else
                {
                    var storage = new byte[checked((int)size)];
                    try
                    {
                        if (NativeGst.gst_buffer_extract(buffer, 0, storage, size) != size) throw new IOException("无法读取屏幕像素。");
                        for (var row = 0; row < info.Height; row++)
                            Buffer.BlockCopy(storage, checked((int)((long)offset + (long)row * stride)), pixels, row * info.Width * 4, info.Width * 4);
                    }
                    finally { Array.Clear(storage); }
                }
                if (cached is not null) Array.Clear(cached.Pixels);
                cached = new(output, (uint)info.Width, (uint)info.Height, (uint)info.Width * 4, 1, false, pixels);
                return cached with { Pixels = (byte[])pixels.Clone() };
            }
            catch { Array.Clear(pixels); throw; }
        }
        finally { NativeGst.gst_mini_object_unref(sample); }
    }
    internal static int ValidatePlane(int width, int height, int stride, nuint offset, nuint size)
    {
        if (width is < 1 or > 16384 || height is < 1 or > 16384 || size > 268435456 || offset > size
            || Math.Abs((long)stride) < width * 4L) throw new FormatException("无效的视频缓冲区。");
        var last = (long)offset + (long)(height - 1) * stride;
        if (last < 0 || Math.Max((long)offset, last) + width * 4L > (long)size) throw new FormatException("视频平面超出缓冲区。");
        return checked((int)WaylandScreenCapture.ValidateBuffer((uint)width, (uint)height, (uint)width * 4));
    }
    private void Close()
    {
        if (pipeline != 0) NativeGst.gst_element_set_state(pipeline, 1);
        if (sink != 0) NativeGst.gst_object_unref(sink);
        if (bus != 0) NativeGst.gst_object_unref(bus);
        if (pipeline != 0) NativeGst.gst_object_unref(pipeline);
        pipeline = sink = bus = 0;
        if (cached is not null) Array.Clear(cached.Pixels);
        cached = null;
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (!disposed) { disposed = true; Close(); } }
        finally { gate.Release(); }
    }
}
