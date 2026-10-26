using System.Runtime.InteropServices;
using URemote.Linux;

var checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS: " + name); }
if (args.Contains("--desktop-actions")) { await DesktopActionChecks.RunAsync(Check); Console.WriteLine($"{checks} desktop action checks passed."); return; }
if (args.Contains("--desktop-kde-live")) { await DesktopActionChecks.LiveKdeAsync(Check); return; }
if (args.Contains("--touch-lifetime")) { await SharedTouchChecks.RunAsync(Check); return; }
if (args.FirstOrDefault() == "--input-probe")
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(130));
    await using var desktop = await PortalDesktopSession.OpenAsync(true, deadline.Token);
    await using var touch = await desktop.CreateTouchAsync(deadline.Token);
    Check(desktop.Outputs.Count > 0, "single combined authorization provides screen and EIS input; no input sent");
    Check(touch?.Available == true, "combined authorization provides mapped touch regions");
    return;
}
if (args.Contains("--input-only"))
{
    await EisTouchChecks.RunAsync(Check, fullInput:true, textCapability:true);
    await EisTouchChecks.RunAsync(Check, fullInput:true, textCapability:false);
    await SharedTouchChecks.RunAsync(Check);
    Console.WriteLine($"{checks} isolated combined EIS input checks passed."); return;
}
if (args.Contains("--touch-only")) { await EisTouchChecks.RunAsync(Check); Console.WriteLine($"{checks} isolated EIS checks passed."); return; }
if (args.Contains("--lock-input")) { await LockInputChecks.RunAsync(Check); Console.WriteLine($"{checks} lock input checks passed."); return; }
using (var text = PortalValue.String("fixture"))
using (var number = PortalValue.UInt(3))
using (var dict = PortalValue.Dict(("text", text), ("types", number)))
using (var read = dict.Get("types"))
{
    Check(dict.Type == "a{sv}" && read!.UInt() == 3, "native variants preserve typed Portal options");
    Check(dict.Get("missing") is null, "missing options remain absent");
    try { _ = text.UInt(); Check(false, "type mismatch"); } catch (FormatException) { Check(true, "variant type mismatch is rejected before native access"); }
}
Check(PortalCaptureHealth.IsLowBitrate(57, 0.05) && !PortalCaptureHealth.IsLowBitrate(57, 2.4) && !PortalCaptureHealth.IsLowBitrate(8, 0.05),
    "stuck capture is high frame rate with tiny encoded bitrate");
Check(PortalCaptureHealth.WindowIsStale(150, TimeSpan.FromSeconds(2.6), 30_000) && !PortalCaptureHealth.WindowIsStale(150, TimeSpan.FromSeconds(2.6), 975_000),
    "a multi-second near-black window is stale and a healthy window is not");
Check(!PortalCaptureHealth.WindowIsStale(30, TimeSpan.FromSeconds(3), 1_000) && PortalCaptureHealth.StreakTriggers(2) && !PortalCaptureHealth.StreakTriggers(1),
    "bitrate stale detection waits for two full windows");
Check(PortalCaptureHealth.BlackStreak(30, TimeSpan.FromSeconds(4)) && !PortalCaptureHealth.BlackStreak(10, TimeSpan.FromSeconds(4)),
    "near-black content must persist for several seconds");
var black = new byte[64 * 4];
var visible = new byte[64 * 4];
for (var i = 0; i < visible.Length; i += 4) { visible[i] = 40; visible[i + 1] = 90; visible[i + 2] = 180; visible[i + 3] = 255; }
Check(PortalCaptureHealth.NearBlack(black) && !PortalCaptureHealth.NearBlack(visible), "blank frames are near-black and a desktop frame is not");
Check(PortalVideoStream.ReuseCachedFrame(TimeSpan.FromMilliseconds(20)) && !PortalVideoStream.ReuseCachedFrame(TimeSpan.FromMilliseconds(251)),
    "portal capture does not replay a cached frame older than 250 ms");
var mapped = PortalOutputRefresh.Map([new PortalStreamBox(55, 0, 0, 3840, 2160)], [new WaylandOutputMode(7, 0, 0, 3840, 2160, 60000)]);
Check(mapped.Count == 1 && mapped[55] == 60000, "portal stream refresh follows the Wayland mode instead of the PipeWire node id");
var single = PortalOutputRefresh.Map([new PortalStreamBox(9, 0, 0, 0, 0)], [new WaylandOutputMode(3, 0, 0, 3840, 2160, 60000)]);
Check(single[9] == 60000, "a single shared monitor still supplies refresh when the stream has no size");
var placed = PortalOutputRefresh.Map(
    [new PortalStreamBox(4, 0, 0, 1920, 1080), new PortalStreamBox(8, 1920, 0, 2560, 1440)],
    [new WaylandOutputMode(2, 1920, 0, 2560, 1440, 120000), new WaylandOutputMode(1, 0, 0, 1920, 1080, 60000)]);
Check(placed[4] == 60000 && placed[8] == 120000, "multiple portal streams match Wayland outputs by position and size");
Check(PortalOutputRefresh.Map([new PortalStreamBox(1, 0, 0, 100, 100)], []).Count == 0, "missing Wayland modes stay empty for the 60 FPS fallback");
await LockInputChecks.RunAsync(Check);
Check(PortalVideoStream.ValidatePlane(2, 2, 12, 4, 24) == 16, "padded BGRA plane bounds");
Check(PortalVideoStream.ValidatePlane(2, 2, -8, 8, 16) == 16, "negative stride plane bounds");
foreach (var plane in new[] { (2,2,4,(nuint)0,(nuint)16), (2,2,-8,(nuint)0,(nuint)16), (2,2,8,(nuint)4,(nuint)16), (16384,16384,65536,(nuint)0,(nuint)1073741824) })
{
    try { PortalVideoStream.ValidatePlane(plane.Item1,plane.Item2,plane.Item3,plane.Item4,plane.Item5); Check(false,"invalid plane"); }
    catch (FormatException) { Check(true,"out-of-bounds video plane rejected"); }
}
NativeGst.Probe();
for (var pass = 0; pass < 3; pass++)
{
    await using var stream = new PortalVideoStream(7,
        "videotestsrc is-live=true pattern=red ! video/x-raw,format=BGRA,width=17,height=13,framerate=1/1 ! appsink name=frames max-buffers=1 drop=true sync=false");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var frame = await stream.CaptureAsync(timeout.Token);
    Check(frame.Width == 17 && frame.Height == 13 && frame.Stride == 68 && frame.Pixels.Length == 884,
        "native GStreamer ABI reads exact frame dimensions and packed stride");
    Check(frame.Pixels.Chunk(4).All(p => p[0] == 0 && p[1] == 0 && p[2] == 255 && p[3] == 255), "native capture reads BGRA pixels correctly");
    Array.Clear(frame.Pixels);
    var cached = await stream.CaptureAsync(timeout.Token);
    Check(cached.Pixels[2] == 255, "static frame cache is independent of caller-owned pixel buffers");
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    try { await stream.CaptureAsync(canceled.Token); Check(false,"cancel capture"); } catch (OperationCanceledException) { Check(true,"capture cancellation leaves stream reusable"); }
    Check((await stream.CaptureAsync(timeout.Token)).Width == 17,"capture continues after a canceled read");
}
if (args.Contains("--live-probe"))
{
    await PortalDesktopSession.ProbeAsync(CancellationToken.None);
    Console.WriteLine("PASS: current desktop Portal and native plugins available (no authorization requested).");
    return;
}
if (Environment.GetEnvironmentVariable("UREMOTE_ISOLATED_TEST_BUS") != "1") throw new Exception("Run D-Bus checks in dbus-run-session with UREMOTE_ISOLATED_TEST_BUS=1.");
await using var server = await MockPortal.CreateAsync();
await using var bus = await PortalBus.ConnectAsync(CancellationToken.None);
Check(await bus.PropertyAsync(PortalBus.ScreenCast, "AvailableSourceTypes", CancellationToken.None) == 1,"native D-Bus property call");
using (var token = PortalValue.String("fixture"))
using (var result = await bus.RequestAsync(PortalBus.RemoteDesktop,"CreateSession",null,false,[("session_handle_token",token)],CancellationToken.None))
using (var path = result.Get("session_handle"))
    Check(path!.Text() == MockPortal.Session,"Response arriving before method reply is not lost");
using (var types = PortalValue.UInt(3))
using (var result = await bus.RequestAsync(PortalBus.RemoteDesktop,"SelectDevices",MockPortal.Session,false,[("types",types)],CancellationToken.None))
    Check(result.Type == "a{sv}","request carries session and typed options");
using (var result = await bus.RequestAsync(PortalBus.RemoteDesktop,"Start",MockPortal.Session,true,[],CancellationToken.None))
using (var devices = result.Get("devices"))
    Check(devices!.UInt() == 3,"start uses parent-window string and receives granted input types");
server.Calls.Clear(); server.GrantedTypes = 7;
using (var combined = await PortalAuthorization.StartAsync(bus, MockPortal.Session, true, CancellationToken.None))
{
    Check(server.SelectedTypes == 7, "single request selects keyboard, pointer and touchscreen together");
    Check(server.Calls.SequenceEqual(new[] {"SelectDevices","SelectSources","Start"}), "screen and input share one Start consent request");
}
server.GrantedTypes = 3;
using (var partial = await PortalAuthorization.StartAsync(bus, MockPortal.Session, true, CancellationToken.None))
using (var devices = partial.Get("devices"))
    Check(devices!.UInt() == 3, "declining touch does not force a second authorization");
await PersistenceChecks.RunAsync(server, bus, Check);
server.Deny = true;
try { using var denied = await bus.RequestAsync(PortalBus.RemoteDesktop,"Start",MockPortal.Session,true,[],CancellationToken.None); Check(false,"denial"); }
catch (InvalidOperationException) { Check(true,"authorization denial propagates"); }
server.Deny = false;
server.Hold = true;
using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
{
    try { using var pending = await bus.RequestAsync(PortalBus.RemoteDesktop,"Start",MockPortal.Session,true,[],cancel.Token); Check(false,"cancel request"); }
    catch (OperationCanceledException) { Check(server.RequestClosed > 0,"cancellation closes pending Portal request"); }
}
server.Hold = false;
using (var fd = await bus.OpenPipeWireAsync(MockPortal.Session,CancellationToken.None))
{
    var marker = new byte[4]; var count = RandomAccess.Read(fd,marker,0);
    Check(count == 4 && marker.SequenceEqual(new byte[]{1,2,3,4}),"Unix FD list index resolves to owned descriptor surviving reply disposal");
}
using (var fd = await bus.OpenEisAsync(MockPortal.Session, CancellationToken.None))
{
    var marker = new byte[4];
    Check(RandomAccess.Read(fd,marker,0) == 4 && marker[3] == 4, "ConnectToEIS resolves the returned FD list index");
}
var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var subscription = await bus.SubscribeAsync(PortalBus.InterfacePrefix+".Session","Closed",MockPortal.Session,_=>closed.TrySetResult());
await server.EmitClosedAsync();
await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
Check(true,"session closure is delivered on native signal dispatcher");
await bus.UnsubscribeAsync(subscription);
Console.WriteLine($"{checks} native Portal checks passed. Only synthetic video and an isolated D-Bus service were used.");
