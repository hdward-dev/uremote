using System.Runtime.InteropServices;
using URemote.Linux;

var checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS: " + name); }
if (args.FirstOrDefault() == "--touch-probe")
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    var mappings = args.Skip(1).Select((name,index)=>(name,index)).ToDictionary(p=>(uint)p.index,p=>p.name);
    if (mappings.Count == 0) throw new ArgumentException("Provide display mapping IDs.");
    await using var touch = await PortalTouchSession.OpenAsync(mappings, deadline.Token);
    Check(touch.Available,"live touch-only Portal session maps all requested outputs; no input was sent");
    return;
}
if (args.Contains("--touch-only")) { await EisTouchChecks.RunAsync(Check); Console.WriteLine($"{checks} isolated EIS checks passed."); return; }
using (var text = PortalValue.String("fixture"))
using (var number = PortalValue.UInt(3))
using (var dict = PortalValue.Dict(("text", text), ("types", number)))
using (var read = dict.Get("types"))
{
    Check(dict.Type == "a{sv}" && read!.UInt() == 3, "native variants preserve typed Portal options");
    Check(dict.Get("missing") is null, "missing options remain absent");
    try { _ = text.UInt(); Check(false, "type mismatch"); } catch (FormatException) { Check(true, "variant type mismatch is rejected before native access"); }
}
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
