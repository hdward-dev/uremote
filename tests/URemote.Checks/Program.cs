using URemote.Core;
using URemote.Media;
using URemote.Linux;
using System.Diagnostics;
using System.Text;

if (args.Contains("--desktop-environment"))
{
    static DesktopEnvironmentInfo Detect(params (string Key, string Value)[] values)
        => DesktopEnvironmentInfo.Detect(key => values.FirstOrDefault(v => v.Key == key).Value);
    var cases = new[]
    {
        (Detect(("XDG_CURRENT_DESKTOP", "KDE"), ("XDG_SESSION_TYPE", "wayland")), "KDE Plasma / Wayland"),
        (Detect(("XDG_CURRENT_DESKTOP", "niri"), ("WAYLAND_DISPLAY", "wayland-1")), "niri / Wayland"),
        (Detect(("XDG_CURRENT_DESKTOP", "GNOME"), ("XDG_SESSION_TYPE", "wayland")), "GNOME / Wayland"),
        (Detect(("XDG_CURRENT_DESKTOP", "ubuntu:GNOME"), ("DISPLAY", ":0")), "ubuntu · GNOME / X11"),
        (Detect(("XDG_CURRENT_DESKTOP", ""), ("XDG_SESSION_DESKTOP", "plasma"), ("XDG_SESSION_TYPE", "x11")), "KDE Plasma / X11"),
        (Detect(("DESKTOP_SESSION", "future-desktop")), "future-desktop / 未知会话"),
        (Detect(("XDG_CURRENT_DESKTOP", " "), ("XDG_SESSION_DESKTOP", ""), ("DESKTOP_SESSION", "gnome")), "gnome / 未知会话"),
        (Detect(), "未知桌面 / 未知会话")
    };
    foreach (var (actual, expected) in cases)
        if (actual.DisplayName != expected) throw new Exception($"Desktop detection: {actual.DisplayName} != {expected}");
    WaylandGlobal[] native = [new(1, "zwlr_screencopy_manager_v1", 1), new(2, "zwlr_virtual_pointer_manager_v1", 2), new(3, "zwp_virtual_keyboard_manager_v1", 1)];
    if (!DesktopBackend.HasNativeCapture(native) || !DesktopBackend.HasNativeInput(native)
        || DesktopBackend.HasNativeCapture([]) || DesktopBackend.HasNativeInput(native.Take(2))
        || DesktopBackend.HasNativeInput([new(1, "zwlr_virtual_pointer_manager_v1", 1), native[2]]))
        throw new Exception("Desktop capability selection failed.");
    Console.WriteLine("PASS: desktop/session detection and compositor capability selection");
    return;
}

if (args.Contains("--desktop-portal-check"))
{
    using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    Console.WriteLine("Desktop: " + DesktopEnvironmentInfo.Current.DisplayName);
    await using (var desktop = await DesktopBackend.OpenAsync(enableInput: true, stop.Token))
    {
        var globals = await DesktopBackend.DiscoverAsync(stop.Token);
        var outputs = globals.Where(g => g.Interface == "wl_output").Select(g => g.Name).ToArray();
        foreach (var output in outputs)
        {
            for (var i = 0; i < 12; i++)
            {
                var captureTime = Stopwatch.StartNew();
                var frame = await DesktopBackend.CaptureAsync(output, ct: stop.Token);
                try { if (frame.Pixels.Length == 0 || frame.Stride < frame.Width * 4) throw new Exception("Invalid capture.");
                    if (i > 0 && captureTime.Elapsed > TimeSpan.FromSeconds(2)) throw new Exception("Capture blocks on an unchanged desktop.");
                    Console.WriteLine($"PASS: capture {frame.Width}x{frame.Height}, stride={frame.Stride}"); }
                finally { Array.Clear(frame.Pixels); }
                await Task.Delay(1000, stop.Token);
            }
            await using var pointer = await DesktopBackend.CreatePointerAsync(output, stop.Token);
            // Zero scroll checks routing without moving the pointer or clicking.
            await pointer.ApplyAsync(new HostMouseMessage(MouseAction.Scroll, 0, 0, 0), PointerCoordinates.MacNormalized, 1, 1, stop.Token);
            for (var i = 0; i < 20; i++)
            {
                using var generation = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                generation.CancelAfter(TimeSpan.FromMilliseconds(1));
                try
                {
                    var frame = await DesktopBackend.CaptureAsync(output, ct: generation.Token);
                    Array.Clear(frame.Pixels);
                }
                catch (OperationCanceledException) when (generation.IsCancellationRequested) { }
                // Encoder restarts must not destroy the shared portal or corrupt
                // the next response even if cancellation interrupts frame transfer.
                await DesktopBackend.DiscoverAsync(stop.Token);
            }
            Console.WriteLine("PASS: repeated capture cancellation preserves desktop session and response framing");
        }
        await using var keyboard = await DesktopBackend.CreateKeyboardAsync(stop.Token);
        await keyboard.ApplyAsync(new HostKeyMessage(KeyAction.Click, 56), stop.Token);
        Console.WriteLine("PASS: input permission, neutral pointer event, Shift press/release");
    }
    Console.WriteLine("PASS: desktop session disposed");
    return;
}

if (args.Length == 2 && args[0] == "--video-recovery") { await RecoveryChecks.RunAsync(args[1]); return; }

if (args.Contains("--terminal-manager"))
{
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await using var manager = new HostTerminalManager();
    var session = manager.Create(80, 24);
    var received = new StringBuilder(); var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    session.Attach((type, id, bytes) =>
    {
        if (type != 5) return;
        if (received.Length + bytes.Length > 262144) throw new Exception("Test output limit exceeded.");
        received.Append(Encoding.UTF8.GetString(bytes));
        if (received.ToString().Contains("PERSIST_OK")) ready.TrySetResult();
    });
    await session.WriteAsync(Encoding.UTF8.GetBytes("UREMOTE_TEST_PERSIST=OK; printf 'PERSIST_%s\\n' \"$UREMOTE_TEST_PERSIST\"\n"), stop.Token);
    await ready.Task.WaitAsync(stop.Token);
    session.Detach();
    if (manager.Find(session.Id) != session || session.Exited) throw new Exception("Detach lost terminal session.");
    var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    received.Clear();
    session.Attach((type, id, bytes) =>
    {
        if (type != 5) return;
        received.Append(Encoding.UTF8.GetString(bytes));
        if (received.ToString().Contains("RESUME_OK")) resumed.TrySetResult();
    });
    await session.WriteAsync(Encoding.UTF8.GetBytes("printf 'RESUME_%s\\n' \"$UREMOTE_TEST_PERSIST\"\n"), stop.Token);
    await resumed.Task.WaitAsync(stop.Token);
    await manager.CloseAsync(session.Id);
    if (manager.Find(session.Id) is not null) throw new Exception("Closing session did not remove it.");
    received.Clear();
    Console.WriteLine("PASS: terminal detach/attach retains shell state, output resumes, and close cleans up");
    return;
}

if (args.Contains("--pty-only"))
{
    using var ptyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await using var terminal = new LinuxTerminal(100, 35);
    var captured = new StringBuilder();
    var read = terminal.ReadAsync(chunk =>
    {
        if (captured.Length + chunk.Length > 262144) throw new Exception("PTY test output exceeded limit.");
        captured.Append(Encoding.UTF8.GetString(chunk.Span)); return Task.CompletedTask;
    }, ptyDeadline.Token);
    await terminal.WriteAsync(Encoding.UTF8.GetBytes("test -t 0 && printf 'PTY_%s\\n' OK; stty size; exit 7\n"), ptyDeadline.Token);
    await read.WaitAsync(ptyDeadline.Token);
    if (!captured.ToString().Contains("PTY_OK") || !captured.ToString().Contains("35 100"))
        throw new Exception("PTY controlling terminal or geometry failed.");
    captured.Clear();
    Console.WriteLine("PASS: native PTY provides an interactive controlling terminal with requested rows and columns");
    await terminal.DisposeAsync();
    if (terminal.ExitCode != 7) throw new Exception("Shell exit status was not retained.");
    await using var interactive = new LinuxTerminal();
    interactive.Resize(120, 40);
    var readyForInterrupt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var interactiveRead = interactive.ReadAsync(chunk =>
    {
        if (captured.Length + chunk.Length > 262144) throw new Exception("PTY test output exceeded limit.");
        captured.Append(Encoding.UTF8.GetString(chunk.Span));
        if (captured.ToString().Contains("40 120")) readyForInterrupt.TrySetResult();
        return Task.CompletedTask;
    }, ptyDeadline.Token);
    await interactive.WriteAsync(Encoding.UTF8.GetBytes("stty size; sleep 30\n"), ptyDeadline.Token);
    await readyForInterrupt.Task.WaitAsync(ptyDeadline.Token);
    await Task.Delay(100, ptyDeadline.Token);
    await interactive.WriteAsync(new byte[] { 3 }, ptyDeadline.Token);
    await Task.Delay(100, ptyDeadline.Token);
    await interactive.WriteAsync(Encoding.UTF8.GetBytes("printf 'INTERRUPT_%s\\n' OK; exit\n"), ptyDeadline.Token);
    await interactiveRead.WaitAsync(ptyDeadline.Token);
    if (!captured.ToString().Contains("INTERRUPT_OK")) throw new Exception("Ctrl+C did not return control to shell.");
    captured.Clear();
    Console.WriteLine("PASS: resize, foreground Ctrl+C, exit status and terminal cleanup");
    return;
}

void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS: " + name); }
if (args.Contains("--video-4k-checks"))
{
    await QualityChecks.RunAsync(args.Last(), 30, 3840, 2160, 60);
    return;
}
if (args.Contains("--video-quality-checks") || args.Contains("--video-144-checks"))
{
    await QualityChecks.RunAsync(args.Last(), args.Contains("--video-144-checks") ? 144 : 60);
    return;
}
if (args.Contains("--display-refresh-checks"))
{
    foreach (var item in await URemote.Linux.WaylandOutputRefresh.ReadAsync())
        Console.WriteLine($"output={item.Key};refresh-millihz={item.Value};fps-limit={HostDisplayInfo.FrameRateLimit(item.Value)}");
    return;
}
if (args.Contains("--video-loss-checks"))
{

// The current Windows menu names differ from the legacy protocol enum names.
var qualityProfiles = new HostVideoSettings([(1920,1080),(2560,1440)], frameRateLimits: [120,60]);
foreach (var (quality, bitrate, crf) in new[] { (2,8000000,23), (3,14000000,18), (4,30000000,14) })
{
    Check(qualityProfiles.Apply(new(-2,-1,-1,144,quality,1,0,1))
        && qualityProfiles.Get(0) is { Width:1920, Height:1080, Fps:120 }
        && qualityProfiles.Get(1) is { Width:2560, Height:1440, Fps:60 }
        && qualityProfiles.Get(0).Bitrate == bitrate && qualityProfiles.Get(1).Bitrate == bitrate
        && qualityProfiles.Get(0).Crf == crf,
        $"manual quality {quality} immediately uses its budget without a startup clamp or display/FPS changes");
}
qualityProfiles.Apply(new(-1,0,0,0,5,1,0,1,AutoQuality:3));
Check(qualityProfiles.Get(0) is { Bitrate:14000000, Crf:18 }, "automatic quality honors client-selected Ultra target");
qualityProfiles.Apply(new(-1,0,0,30,0,1,0,1));
Check(qualityProfiles.Get(0) is { Bitrate:14000000, Crf:18, Fps:30 }, "FPS-only update preserves automatic target");
qualityProfiles.Apply(new(-1,0,0,0,6,1,0,1,CustomBitrate:18000000));
Check(qualityProfiles.Get(0).Bitrate == 18000000, "custom bitrate is used in bits per second");
qualityProfiles.Apply(new(-1,0,0,60,0,1,0,1));
Check(qualityProfiles.Get(0).Bitrate == 18000000, "FPS update preserves custom bitrate");
var beforeInvalid = qualityProfiles.Get(0);
Check(!qualityProfiles.Apply(new(-1,0,0,0,6,1,0,1,CustomBitrate:40000001))
    && qualityProfiles.Get(0) == beforeInvalid, "unsupported custom bitrate is rejected without altering profile");
qualityProfiles.Apply(new(-1,0,0,0,4,1,0,1));
qualityProfiles.ObserveReceiver(1000,100,60);
var limitedRate = qualityProfiles.Get(0).Bitrate;
qualityProfiles.Apply(new(-1,0,0,0,4,1,0,1));
Check(limitedRate < 30000000 && qualityProfiles.Get(0).Bitrate == limitedRate,
    "manual quality requests do not bypass a measured congestion ceiling");
for (var n = 1; n <= 20; n++) qualityProfiles.ObserveReceiver(1000 + n*1000,100,60+n*60);
Check(qualityProfiles.Get(0).Bitrate == 30000000, "healthy feedback restores the selected Original budget");
// Minimal synthetic RPC capture request: custom=18 Mbps and auto target=Original.
var qualityRequest = HostCaptureProtocol.Decode(FileTransferProtocol.Blob(21,
    FileTransferProtocol.Join(FileTransferProtocol.Blob(1,FileTransferProtocol.Int(1,1)),
        FileTransferProtocol.Blob(2,FileTransferProtocol.Join(FileTransferProtocol.Int(2,6),
            FileTransferProtocol.Int(8,18000000),FileTransferProtocol.Int(12,4))))));
Check(qualityRequest is { Quality:6, CustomBitrate:18000000, AutoQuality:4 },
    "capture RPC decodes custom bitrate and automatic target independently");

var mixedRates = new HostVideoSettings([(1920,1080),(1920,1080)], frameRateLimits: [120,60]);
Check(mixedRates.Apply(new(-1,0,0,144,5,0,0,null)) && mixedRates.Get(0).Fps == 120 && mixedRates.Get(1).Fps == 60,
    "144 FPS request is capped independently by each active screen refresh rate");
Check(mixedRates.Apply(new(-1,0,0,30,5,0,0,null)) && mixedRates.Get(0).Fps == 30 && mixedRates.Get(1).Fps == 30,
    "lower requested FPS is preserved on high-refresh screens");
var mobileRates = new HostVideoSettings([(1920,1080),(1920,1080)], true, [120,60]);
mobileRates.Apply(new(1,0,0,144,5,0,0,null));
Check(mobileRates.SelectedScreen == 1 && mobileRates.Get(1).Fps == 60, "mobile display switching preserves the selected display FPS ceiling");
var highRate = new HostVideoSettings([(1920,1080)]);
Check(highRate.Apply(new(0,1920,1080,144,5,0,0,null)) && highRate.Get(0).Fps == 144, "144 FPS request reaches video profile without a 60 FPS clamp");
Check(highRate.Apply(new(0,1920,1080,120,5,0,0,null)) && highRate.Get(0).Fps == 120, "explicit 120 FPS remains supported");
Check(highRate.Apply(new(0,1920,1080,240,5,0,0,null)) && highRate.Get(0).Fps == 144, "requests above supported range are bounded");
var congested = new HostVideoSettings([(1920, 1080)]);
var beforeLoss = congested.Get(0);
Check(congested.ObserveReceiver(15079, 1325, 19), "receiver packet loss triggers recovery");
Check(congested.Get(0).Bitrate < beforeLoss.Bitrate && congested.Get(0).Recovery == 1, "recovery lowers bitrate and restarts encoder for an IDR");
var afterLoss = congested.Get(0);
Check(!congested.ObserveReceiver(15079, 1325, 19) && congested.Get(0) == afterLoss, "duplicate receiver counters do not trigger recovery");
Check(congested.ObserveReceiver(29938, 3606, 19), "stalled decoder with arriving packets triggers recovery");
Check(!congested.ObserveReceiver(10, 0, 1), "reset counters establish a new baseline");

var constrained = new HostVideoSettings([(1920,1080)]);
long recv=0, loss=0, frames=0;
for (var n=0;n<8;n++) constrained.ObserveReceiver(recv+=1000,loss+=100,frames+=10);
var floor = constrained.Get(0);
Check(floor.Bitrate==1000000 && !constrained.ObserveReceiver(recv+=1000,loss+=100,frames+=10)
    && constrained.Get(0)==floor, "loss at floor does not restart advancing decoder");
constrained.ObserveReceiver(recv+=1000,loss,frames+=60);
constrained.ObserveReceiver(recv+=1000,loss,frames+=60);
Check(constrained.Get(0).Bitrate>floor.Bitrate, "healthy feedback restores bitrate gradually");
var recoveredRate=constrained.Get(0).Bitrate;
constrained.Apply(new(-1,0,0,60,6,0,0,null));
Check(constrained.Get(0).Bitrate==recoveredRate, "quality request preserves current network ceiling");
// Independently reconstruct single-NAL and fragmented packets; marker only on the last packet of the frame.
foreach(var length in new[]{1,1099,1100,1101,4000})
{
    var nal=new byte[length]; nal[0]=0x65; Array.Fill(nal,(byte)0x55,1,length-1);
    byte[] au=[0,0,0,1,0x67,0x42,0,0,0,1,..nal];
    var packets=H264RtpPayloads.Create(au).ToArray();
    Check(packets.All(p=>p.Bytes.Length<=1100) && packets.Count(p=>p.Last)==1 && packets[^1].Last,
        "RTP bounded payloads and single final marker length="+length);
    var reconstructed=new List<byte>();
    foreach(var packet in packets.Skip(1))
    {
        if((packet.Bytes[0]&31)==28)
        {
            if((packet.Bytes[1]&0x80)!=0) reconstructed.Add((byte)((packet.Bytes[0]&0xe0)|(packet.Bytes[1]&31)));
            reconstructed.AddRange(packet.Bytes.Skip(2));
        }
        else reconstructed.AddRange(packet.Bytes);
    }
    Check(reconstructed.SequenceEqual(nal), "FU-A reassembles original NAL length="+length);
}
    return;
}
Check(HostTerminalProtocol.DecodeFrame(Convert.FromHexString("5445524D010900000000000000000000")) is { Type: 9, SessionId: 0, Payload.Length: 0 },
    "official terminal list request fixture");
Check(HostTerminalProtocol.EncodeFrame(5, 1, "A"u8).SequenceEqual(Convert.FromHexString("5445524D01050000010000000100000041")),
    "terminal output frame uses bounded payload and little-endian fields");
try { HostTerminalProtocol.DecodeFrame(Convert.FromHexString("5445524D010500000100000001000000")); throw new Exception("Truncated terminal payload accepted"); }
catch (FormatException) { Console.WriteLine("PASS: truncated terminal frame rejected"); }
Check(HostTerminalProtocol.EnvironmentReply([0xaa, 1, 7, 0x0a, 2, 8, 1, 0xe2, 1, 0])!
    .SequenceEqual(new byte[] { 0xb2, 1, 9, 0x0a, 2, 8, 1, 0xc2, 1, 2, 8, 0 }),
    "terminal environment reply preserves RPC id and uses official field tags");
// Independently formed protobuf request: envelope21 -> header1/id7 + change10 -> format1/text2.
byte[] change = [0xaa, 1, 13, 0x0a, 2, 8, 7, 0x52, 7, 8, 1, 0x12, 3, 0x61, 0x62, 0x63];
var decoded = HostClipboardProtocol.Decode(change);
Check(decoded is { RequestId: 7, Format: 1, Text: "abc", IsRead: false }, "upstream clipboard text wire fixture");
Check(HostClipboardProtocol.Decode(HostClipboardProtocol.Change(8, "中文😀"))?.Text == "中文😀", "Unicode clipboard round trip");
try { HostClipboardProtocol.Decode([0xaa, 1, 0xff]); throw new Exception("accepted truncated packet"); } catch (FormatException) { Console.WriteLine("PASS: truncated clipboard rejected"); }
var responses = HostClipboardProtocol.ReadResponse(new(9, 1, "", "sample", true), new string('x', 40000)).ToList();
Check(responses.Count == 3 && responses[1].Channel == "FILE_DATA_CHANNEL", "clipboard replies use bounded 32 KiB blocks");
var wire = new byte[] { 0,0,0,1,9,0xf0,0,0,1,0x65,5, 0,0,0,1,9,0xf0,0,0,1,0x41,7 };
var parser = new AnnexBAccessUnits(); var units = new List<byte[]>();
foreach (var b in wire) units.AddRange(parser.Push([b]));
if (parser.Finish() is { } last) units.Add(last);
Check(units.Count == 2 && units[0].Length == 11 && units[1].Length == 11, "AUD parser survives one-byte pipe chunks");
// Legacy capture config: screen 1, 1920x1080, 30fps, high quality.
var config = HostCaptureProtocol.Decode([0x08, 7, 0x4a, 12, 0x10, 1, 0x18, 3, 0x28, 1, 0x30, 0x80, 0x0f, 0x38, 0xb8, 0x08]);
Check(config is { Screen: 1, Width: 1920, Height: 1080, Fps: 30, Quality: 3, Sequence: 7 }, "official capture config fields");
var profiles = new HostVideoSettings([(2560, 1440), (1920, 1080)]);
Check(profiles.Apply(new(-2, -1, -1, 60, 2, 1, 0, 1))
    && profiles.Get(0) is { Width: 2560, Height: 1440, Fps: 60, Crf: 23 }
    && profiles.Get(1) is { Width: 1920, Height: 1080, Fps: 60, Crf: 23 },
    "Windows quality sentinel applies to both displays without changing dimensions");
var previous = profiles.Get(0);
Check(!profiles.Apply(new(-3, -1, -1, 60, 4, 1, 0, 1)) && profiles.Get(0) == previous,
    "invalid display sentinel leaves encoder settings unchanged");
Check(profiles.Apply(new(-2, -1, -1, 0, 4, 1, 0, 1)) && profiles.Get(0).Crf < previous.Crf
    && profiles.Get(0).Bitrate > previous.Bitrate, "higher quality changes compression and bitrate ceiling");
var mobileProfiles = new HostVideoSettings([(2560, 1440), (3840, 2160)], true);
Check(mobileProfiles.SelectedScreen == 0 && mobileProfiles.Apply(new(1, 3840, 2160, 30, 2, 1, 0, 1))
    && mobileProfiles.SelectedScreen == 1, "iOS capture request switches selected physical screen");
Check(mobileProfiles.Apply(new(-2, -1, -1, 60, 3, 1, 0, 1)) && mobileProfiles.SelectedScreen == 1,
    "iOS global quality change preserves selected screen");
Check(!mobileProfiles.Apply(new(2, 0, 0, 30, 2, 1, 0, 1)) && mobileProfiles.SelectedScreen == 1,
    "invalid iOS screen request cannot change video source");
Check(profiles.Apply(new(1, 1920, 1080, 30, 2, 1, 0, 1)) && !profiles.SingleVideoStream && profiles.SelectedScreen == 0,
    "Windows continues using independent video streams");
var formatPacket = HostClipboardTransfers.Advertise(42);
Check(HostClipboardTransfers.Decode(formatPacket) is { Kind: "formats", Format: 13, Id: 42 }, "Windows Unicode clipboard format negotiation");
var receivedAsk = HostClipboardProtocol.Decode(HostClipboardTransfers.Ask(9, "key", 13));
Check(receivedAsk is { IsRead: true, Format: 13, BlockKey: "key" }, "clipboard negotiated format read");
var transfer = HostClipboardProtocol.ReadResponse(receivedAsk!, "中文").ToList();
Check(HostClipboardTransfers.Decode(transfer[0].Data) is { Kind: "confirm", Count: 1, Result: 1 }, "clipboard block-count confirmation");
var dataBlock = HostClipboardTransfers.Decode(transfer[1].Data)!;
Check(dataBlock.Data.SequenceEqual(new byte[] { 0x2d, 0x4e, 0x87, 0x65, 0, 0 }), "Windows Unicode clipboard block uses null-terminated UTF16LE");
var utf8Transfer = HostClipboardProtocol.ReadResponse(new(9, 1, "", "key", true), "中文").ToList();
Check(HostClipboardTransfers.Decode(utf8Transfer[1].Data)!.Data.SequenceEqual(new byte[] { 0xe4, 0xb8, 0xad, 0xe6, 0x96, 0x87 }),
    "UTF8 clipboard block retains UTF8 for the requested text format");
if (OperatingSystem.IsLinux())
{
    var profile = LinuxDeviceProfile.Refresh(new("test", "test-client", "test-system", "", "", "", "", "", "", "", "", "", "", [], 96));
    Check(long.TryParse(profile.Memory, out var megabytes) && megabytes > 0 && profile.SystemVersion == File.ReadAllText("/proc/sys/kernel/osrelease").Trim(),
        "device metadata uses numeric MB and the real kernel version");
    Check(profile.ClientId == "test-client" && profile.SystemId == "test-system", "hardware refresh preserves stable device identifiers");
}
if (args.Length > 0)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    await using var encoder = new StreamingH264Encoder(args[0], 1280, 720, false, 1);
    var receive = Task.Run(async () => { var list = new List<byte[]>(); await foreach (var unit in encoder.ReadAsync(timeout.Token)) list.Add(unit); return list; });
    var pixels = new byte[1280 * 720 * 4]; var watch = Stopwatch.StartNew();
    for (int i = 0; i < 90; i++) { pixels[i * 4] = 255; await encoder.WriteAsync(pixels, 1280 * 4, timeout.Token); }
    encoder.CompleteInput(); var frames = await receive;
    Check(frames.Count == 90, "persistent encoder delivers every synthetic frame");
    Console.WriteLine($"Synthetic encoder throughput: {90 / watch.Elapsed.TotalSeconds:F1} fps (not desktop capture throughput)");
    var decode = new ProcessStartInfo(args[0]) { RedirectStandardInput = true, RedirectStandardError = true };
    foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-f", "h264", "-i", "pipe:0", "-f", "null", "-" }) decode.ArgumentList.Add(a);
    using var p = Process.Start(decode)!; var errors = p.StandardError.ReadToEndAsync();
    foreach (var f in frames) await p.StandardInput.BaseStream.WriteAsync(f, timeout.Token);
    p.StandardInput.Close(); await p.WaitForExitAsync(timeout.Token);
    Check(p.ExitCode == 0 && (await errors).Length == 0, "continuous H264 output decodes without errors");
}
if (args.Contains("--capture-benchmark"))
{
    var globals = await WaylandCapabilities.DiscoverAsync();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    await Task.WhenAll(globals.Where(x => x.Interface == "wl_output").Take(2).Select(async output =>
    {
        var watch = Stopwatch.StartNew(); int count = 0;
        try { while (true) { var frame = await WaylandScreenCapture.CaptureAsync(output.Name, ct: stop.Token); Array.Clear(frame.Pixels); count++; } }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        Console.WriteLine($"Capture output {output.Name}: {count / watch.Elapsed.TotalSeconds:F1} fps; pixel data discarded");
    }));
}
