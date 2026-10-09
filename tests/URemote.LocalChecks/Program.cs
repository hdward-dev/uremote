using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using URemote.Core;
using URemote.Module;
using Avalonia.Input;

var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    passed++; Console.WriteLine("PASS: " + message);
}
foreach (var host in new[] { "127.0.0.1", "::1", "desktop.local" })
    new LocalDesktopOptions(LocalDesktopProtocol.Vnc, host, 5900).Validate();
foreach (var host in new[] { "", "-bad", "rdp://example.com", "host:5900", "a/b", "user@host", "host\narg" })
{
    try { new LocalDesktopOptions(LocalDesktopProtocol.Vnc, host, 5900).Validate(); }
    catch (ArgumentException) { Check(true, "invalid endpoint rejected"); continue; }
    throw new Exception("FAIL: invalid endpoint accepted");
}
foreach (var port in new[] { 0, -1, 65536 })
{
    try { new LocalDesktopOptions(LocalDesktopProtocol.Vnc, "localhost", port).Validate(); }
    catch (ArgumentException) { Check(true, "invalid port rejected"); continue; }
    throw new Exception("FAIL: invalid port accepted");
}
var challenge = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
foreach (var password in new[] { "password", "secret", "12345678", "longpassword", "é" })
{
    var key = new byte[8]; var bytes = Encoding.Latin1.GetBytes(password);
    for (var i = 0; i < Math.Min(8, bytes.Length); i++)
        for (var bit = 0; bit < 8; bit++) key[i] |= (byte)(((bytes[i] >> bit) & 1) << (7 - bit));
    using var des = DES.Create(); des.Key = key;
    Check(VncAuthentication.Respond(password, challenge).SequenceEqual(des.EncryptEcb(challenge, PaddingMode.None)), "VNC authentication agrees with independent DES implementation");
}
Check(Convert.ToHexString(VncAuthentication.Respond("", new byte[16])) == "8CA64DE9C1B123A78CA64DE9C1B123A7", "weak DES key accepted with known standard vector");
Check(VncAuthentication.Respond("12345678extra", challenge).SequenceEqual(VncAuthentication.Respond("12345678", challenge)), "VNC password uses first eight bytes");
Check(LocalDesktopKeys.Map(Key.A, KeyModifiers.None, LocalDesktopProtocol.Rdp) == 0x1e &&
    LocalDesktopKeys.Map(Key.RightCtrl, KeyModifiers.None, LocalDesktopProtocol.Rdp) == 0x11d &&
    LocalDesktopKeys.Map(Key.Delete, KeyModifiers.None, LocalDesktopProtocol.Rdp) == 0x153, "RDP scan codes preserve extended keys");
Check(LocalDesktopKeys.Map(Key.A, KeyModifiers.Shift, LocalDesktopProtocol.Vnc) == 'A' &&
    LocalDesktopKeys.Map(Key.D1, KeyModifiers.Shift, LocalDesktopProtocol.Vnc) == '!' &&
    LocalDesktopKeys.Map(Key.RightCtrl, KeyModifiers.None, LocalDesktopProtocol.Vnc) == 0xffe4, "VNC keysyms preserve shifted characters and modifiers");

foreach (var version in new[] { 3, 7, 8 })
    foreach (var authenticated in new[] { false, true }) await VncLoopback(version, authenticated);
await InvalidRectangle();
await RdpFailure();
if (args.Contains("--rdp-server")) await RdpLoopback();
Console.WriteLine($"PASS: {passed} local desktop checks");

async Task<byte[]> Read(NetworkStream stream, int size, CancellationToken token)
{
    var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, token); return bytes;
}
async Task Write(NetworkStream stream, byte[] bytes, CancellationToken token)
{
    // Exercise fragmented TCP reads, including headers and authentication.
    foreach (var value in bytes) await stream.WriteAsync(new[] { value }, token);
}
async Task Handshake(NetworkStream stream, int version, bool authentication, CancellationToken token)
{
    var banner = Encoding.ASCII.GetBytes($"RFB 003.00{version}\n");
    await Write(stream, banner, token); Check((await Read(stream, 12, token)).SequenceEqual(banner), "RFB version negotiation");
    var security = authentication ? (byte)2 : (byte)1;
    if (version == 3) await Write(stream, [0, 0, 0, security], token);
    else { await Write(stream, [1, security], token); Check((await Read(stream, 1, token))[0] == security, "RFB security selection"); }
    if (authentication)
    {
        await Write(stream, challenge, token);
        Check((await Read(stream, 16, token)).SequenceEqual(VncAuthentication.Respond("secret", challenge)), "VNC password challenge response");
    }
    if (authentication || version == 8) await Write(stream, [0, 0, 0, 0], token);
    Check((await Read(stream, 1, token))[0] == 1, "shared VNC connection");
    var init = new byte[24]; init[1] = 2; init[3] = 2;
    await Write(stream, init, token);
    var pixel = await Read(stream, 20, token); Check(pixel[4] == 32 && pixel[6] == 0 && pixel[7] == 1 && pixel[14] == 16, "32-bit BGRX pixel format requested");
    var enc = await Read(stream, 16, token); Check(enc[0] == 2 && enc[3] == 3 && BinaryPrimitives.ReadInt32BigEndian(enc.AsSpan(12)) == -223, "raw, CopyRect and resize negotiated");
}
byte[] Update(int x, int y, int width, int height, int encoding, byte[] data)
{
    var bytes = new byte[16 + data.Length]; bytes[3] = 1;
    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), (ushort)x); BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(6), (ushort)y);
    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(8), (ushort)width); BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(10), (ushort)height);
    BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), encoding); data.CopyTo(bytes, 16); return bytes;
}
async Task VncLoopback(int version, bool authenticated)
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
    var client = new VncDesktopSession();
    var frames = System.Threading.Channels.Channel.CreateUnbounded<LocalDesktopFrame>();
    client.Frame += frame => frames.Writer.TryWrite(frame);
    var running = client.RunAsync(new(LocalDesktopProtocol.Vnc, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, Password: "secret"), stop.Token);
    using var server = await listener.AcceptTcpClientAsync(timeout.Token); var stream = server.GetStream();
    await Handshake(stream, version, authenticated, timeout.Token);
    Check((await Read(stream, 10, timeout.Token))[1] == 0, "initial full framebuffer request");
    await Write(stream, Update(0, 0, 2, 2, 0, [1,2,3,0,4,5,6,0,7,8,9,0,10,11,12,0]), timeout.Token);
    var first = await frames.Reader.ReadAsync(timeout.Token);
    Check(first.Width == 2 && first.Height == 2 && first.Pixels.SequenceEqual(new byte[] { 1,2,3,255,4,5,6,255,7,8,9,255,10,11,12,255 }), "fragmented raw framebuffer decoded with opaque alpha");
    Check((await Read(stream, 10, timeout.Token))[1] == 1, "incremental framebuffer request");
    await Write(stream, Update(0, 1, 2, 1, 1, [0,0,0,0]), timeout.Token);
    var copy = await frames.Reader.ReadAsync(timeout.Token);
    Check(copy.Pixels.AsSpan(0, 8).SequenceEqual(copy.Pixels.AsSpan(8, 8)) && first.Pixels[8] == 7, "CopyRect handles overlap without mutating delivered frames");
    await Read(stream, 10, timeout.Token);
    await Write(stream, Update(0, 0, 3, 1, -223, []), timeout.Token);
    var resized = await frames.Reader.ReadAsync(timeout.Token);
    Check(resized.Width == 3 && resized.Height == 1 && resized.Pixels.Length == 12, "desktop resize changes framebuffer");
    var resizeRequest = await Read(stream, 10, timeout.Token);
    Check(resizeRequest[1] == 0 && BinaryPrimitives.ReadUInt16BigEndian(resizeRequest.AsSpan(6)) == 3, "resize requests full new framebuffer");
    client.Pointer(999, -1, 1); client.Pointer(2, 0, 0, -1); client.Key(0xffe3, true); client.Key(0xffe3, false);
    var pointer = await Read(stream, 6, timeout.Token);
    Check(pointer.SequenceEqual(new byte[] { 5,1,0,2,0,0 }), "pointer mapped and clamped to remote dimensions");
    Check((await Read(stream, 6, timeout.Token))[1] == 16 && (await Read(stream, 6, timeout.Token))[1] == 0, "wheel press followed by release");
    Check((await Read(stream, 8, timeout.Token))[1] == 1 && (await Read(stream, 8, timeout.Token))[1] == 0, "keyboard press and release transmitted");
    stop.Cancel();
    try { await running.WaitAsync(timeout.Token); } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException or SocketException) { }
    Check(running.IsCompleted, "cancel disconnects VNC and joins writer");
}
async Task InvalidRectangle()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var running = new VncDesktopSession().RunAsync(new(LocalDesktopProtocol.Vnc, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port), timeout.Token);
    using var server = await listener.AcceptTcpClientAsync(timeout.Token); var stream = server.GetStream();
    await Handshake(stream, 8, false, timeout.Token); await Read(stream, 10, timeout.Token);
    await Write(stream, Update(1, 1, 2, 2, 0, []), timeout.Token);
    try { await running; } catch (InvalidDataException) { Check(true, "out-of-bounds framebuffer rejected before allocation/read"); return; }
    throw new Exception("FAIL: out-of-bounds rectangle accepted");
}
async Task RdpFailure()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var client = new RdpDesktopSession((_, _) => Task.FromResult(false));
    try { await client.RunAsync(new(LocalDesktopProtocol.Rdp, "127.0.0.1", port, "fixture"), timeout.Token); }
    catch (NotSupportedException) when (!args.Contains("--require-rdp")) { Console.WriteLine("SKIP: native RDP bridge unavailable"); return; }
    catch (InvalidOperationException) { Check(true, "native FreeRDP connection failure returns without crash"); return; }
    throw new Exception("FAIL: unreachable RDP endpoint succeeded");
}
async Task RdpLoopback()
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var frame = new TaskCompletionSource<LocalDesktopFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
    var cert = false;
    var client = new RdpDesktopSession((_, _) => { cert = true; return Task.FromResult(true); });
    client.Frame += value => frame.TrySetResult(value);
    var run = client.RunAsync(new(LocalDesktopProtocol.Rdp, "127.0.0.1", 33989, "fixture", "fixture"), timeout.Token);
    var first = await Task.WhenAny(frame.Task, run);
    if (first == run) { await run; throw new Exception("FAIL: RDP disconnected before frame"); }
    var image = await frame.Task;
    Check(image.Width > 0 && image.Height > 0 && image.Pixels.Length == image.Width * image.Height * 4, "FreeRDP paints actual server framebuffer");
    Check(cert, "untrusted RDP certificate reaches verification callback");
    client.Pointer(20, 20, 1); client.Pointer(20, 20, 0); client.Key(0x1e, true); client.Key(0x1e, false);
    await Task.Delay(100); timeout.Cancel(); await run;
    Check(run.IsCompleted, "RDP cancellation settles native event loop and frees session");
    using var deniedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var denied = new RdpDesktopSession((_, _) => Task.FromResult(false));
    var deniedFrame = false; denied.Frame += _ => deniedFrame = true;
    try { await denied.RunAsync(new(LocalDesktopProtocol.Rdp, "127.0.0.1", 33989, "fixture", "fixture"), deniedTimeout.Token); }
    catch (InvalidOperationException) { Check(!deniedFrame, "declined RDP certificate aborts before displaying a desktop"); return; }
    throw new Exception("FAIL: declined RDP certificate connected");
}
