using URemote.Core;
using URemote.Media;
using URemote.Linux;
using System.Diagnostics;
using System.Text;

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
Check(dataBlock.Data.SequenceEqual(new byte[] { 0xe4, 0xb8, 0xad, 0xe6, 0x96, 0x87 }), "UU Unicode text format transports UTF8, not native Windows UTF16");
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
