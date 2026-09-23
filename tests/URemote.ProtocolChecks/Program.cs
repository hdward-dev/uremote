using System.Net;
using System.Text.Json.Nodes;
using URemote.Core;
using URemote.Probe;

var passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    passed++;
    Console.WriteLine("PASS: " + label);
}
void Reject(Action action, string label)
{
    try { action(); }
    catch (Exception e) when (e is ArgumentException or FormatException or System.Text.Json.JsonException)
    { Check(true, label); return; }
    throw new Exception("FAIL: " + label);
}
var bundle = new BundledIceCandidates("v=0\r\na=group:BUNDLE 0 1\r\nm=video 9 UDP/TLS/RTP/SAVPF 98\r\na=mid:0\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=mid:1\r\n");
var bundledCandidate = new JsonObject { ["candidate"] = "fixture", ["sdpMLineIndex"] = 1, ["sdpMid"] = "1" };
Check(bundle.Normalize(bundledCandidate)["sdpMLineIndex"]!.GetValue<int>() == 0 && bundledCandidate["sdpMLineIndex"]!.GetValue<int>() == 1, "bundled candidates map to shared transport without mutating signal data");
Check(new BundledIceCandidates("m=video 9 UDP/TLS/RTP/SAVPF 98\na=mid:0\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\na=mid:1").Normalize(bundledCandidate)["sdpMLineIndex"]!.GetValue<int>() == 1, "nonbundled transport is never remapped");
using (var previews = System.Text.Json.JsonDocument.Parse("""
{"data":{"desktop_devices":[{"device_id":"a","wallpaper_url":"https://example.com/preview.png?signature=abc"},{"device_id":"b","wallpaper_url":"file:///etc/passwd"},{"device_id":"c","wallpaper_url":"https://user:secret@example.com/image"},{"device_id":"d"}]}}
"""))
{
    var parsed = UuDeviceCatalog.Parse(previews.RootElement, "a");
    Check(parsed[0].WallpaperUrl == "https://example.com/preview.png?signature=abc", "catalog preserves signed HTTPS preview URL");
    Check(parsed.Skip(1).All(d => d.WallpaperUrl.Length == 0), "catalog rejects file and credential URLs and supports missing previews");
}
using (var catalog = System.Text.Json.JsonDocument.Parse("""
{"data":{"desktop_devices":[{"device_id":"self","alias":"Fixture Linux","platform":4,"status":"CONNECTED","controllable":true,"controlled_support":true},{"device_id":"offline","name":"Fixture Windows","platform":"1","status":"DISCONNECTED","controllable":true}],"mobile_devices":[{"device_id":"mobile","platform":3,"status":"DISCONNECTED"},{"device_id":"self"}],"tv_devices":[]}}
"""))
{
    var devices = UuDeviceCatalog.Parse(catalog.RootElement, "self");
    Check(devices.Count == 3 && devices[0].IsCurrent && devices[0].Online, "device catalog groups, deduplication and current device identity");
    Check(!devices[1].Online && devices[1].Controllable && devices[1].PlatformName == "Windows", "offline device is not inferred online from controllable flag");
    Check(devices[2].Name == "未命名设备" && devices[2].Category == "mobile", "device catalog missing label and mobile classification");
    Check(!devices[0].ToString().Contains("Fixture") && !devices[0].ToString().Contains("self"), "catalog diagnostic text redacts device identity");
}
Check(HostAssistance.Sign("fixture-salt", "ABCDEFG2") == "de38978801bc9ec305467429f51e3acacc46567e8d8b13f8d788db62f51107e4", "assistance response uses SHA256 of salt followed by temporary code");
Check(HostAssistance.Challenge(JsonNode.Parse("{\"data\":{\"control_id\":\"fixture\",\"salt\":\"challenge\"}}")) is { ControlId: "fixture", Salt: "challenge" }, "assistance challenge extracts bounded routing fields");
var temporary = HostAssistance.Code("fixture-local");
Check(temporary.Length == 8 && temporary.All(c => "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Contains(c)) && HostAssistance.Code("fixture-local") == temporary, "temporary assistance code is stable in one process and uses official alphabet");
Check(HostAssistance.Challenge(JsonNode.Parse("{\"control_id\":\"fixture\",\"salt\":\"\"}")) is null, "empty assistance challenge rejected");
var allowedToken = HostAssistance.PermissionToken("fixture-local");
HostAssistance.SetEnabled("fixture-local", false);
Check(allowedToken.IsCancellationRequested && !HostAssistance.IsEnabled("fixture-local"), "disabling assistance revokes its active permission token");
Check(HostAssistance.Code("fixture-local") != temporary, "disabling assistance invalidates the previous temporary code");
HostAssistance.SetEnabled("fixture-local", true);
Check(HostAssistance.IsEnabled("fixture-local") && allowedToken.IsCancellationRequested, "reenabling assistance gives a fresh permission without reviving old sessions");
// Synthetic secrets only; no real account or network is used here.
HostAssistance.Configure("fixture-custom", "Fixture123", true);
Check(HostAssistance.DisplayCode("fixture-custom") == "Fixture123" && HostAssistance.RotateAfterConnection("fixture-custom") && HostAssistance.DisplayCode("fixture-custom") == "Fixture123", "custom code survives a successful assistance connection");
HostAssistance.Configure("fixture-custom", "Fixture123", false);
var beforeRotation = HostAssistance.DisplayCode("fixture-custom");
Check(HostAssistance.RotateAfterConnection("fixture-custom") && HostAssistance.DisplayCode("fixture-custom") != beforeRotation, "temporary mode rotates on successful connection");
HostAssistance.Configure("fixture-custom", "Fixture123", true);
Check(HostAssistance.DisplayCode("fixture-custom") == "Fixture123", "switching modes preserves the custom code");
foreach (var invalid in new[] { "short1", "onlyletters", "123456789", "Space 123", "中文测试12345", new string('a', 33) + "1" })
    Reject(() => HostAssistance.ValidateCustomCode(invalid), "invalid custom code rejected");
var secretDir = Path.Combine(Path.GetTempPath(), "uremote-code-check-" + Guid.NewGuid().ToString("N"));
var secretPath = Path.Combine(secretDir, "secret.json");
try
{
    var saved = new AssistanceCodeSettings("fixture-custom", "Fixture123", true);
    saved.Save(secretPath);
    Check(AssistanceCodeSettings.Load(secretPath, "fixture-custom") == saved, "custom code persists across reload");
    Check(!saved.ToString().Contains("Fixture123"), "custom code is redacted from diagnostics");
    Check(!AssistanceCodeSettings.Load(secretPath, "other-device").UseCustom, "custom code is bound to its device");
    if (OperatingSystem.IsLinux())
    {
        Check(File.GetUnixFileMode(secretPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "secret file is created with owner-only permissions");
        File.SetUnixFileMode(secretPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        var rejected = false;
        try { AssistanceCodeSettings.Load(secretPath, "fixture-custom"); } catch (IOException) { rejected = true; }
        Check(rejected, "publicly readable custom code file is rejected");
    }
    new AssistanceCodeSettings("fixture-custom", "", false).Save(secretPath);
    Check(AssistanceCodeSettings.Load(secretPath, "fixture-custom").CustomCode == "", "clearing custom code persists");
}
finally { Directory.Delete(secretDir, true); }
var controllerOptions = StreamerConnectOptions.Decode(ControllerProtocol.ConnectOptions("fixture-controller"));
Check(controllerOptions.CaptureType == 1 && controllerOptions.ClientType == 4 && controllerOptions.DeviceId == "fixture-controller"
    && controllerOptions.DecoderCapabilities.Single().CodecType == 1, "native controller H264 capabilities and Mac identity wire options");
Check(HostControlEcho.Reply(ControllerProtocol.Echo(7), 8, 9, true) is not null, "native controller heartbeat is accepted by host protocol");
var controllerCapture = HostCaptureProtocol.Decode(ControllerProtocol.Capture(2, 1));
Check(controllerCapture is { Screen: 1, Quality: 3, Fps: 60 }, "native controller screen selection routes to requested display");
var state = new LoginState("fixture-token", "fixture-user", "fixture-client", "fixture-device");
var headers = UuSigning.BuildHeaders(state, "POST", "/api/v1/login/by_mobile", "{\"mobile\":\"测试<&>\"}", 1700000000);
Check(headers["X-Param-SIGN"] == "38f485211c4d36d301b26682d807c1a9a5d705b2fdd7edf679929dfa8fd12a27", "signature matches independent upstream JavaScript fixture");
Check(headers["Authorization"] == "Bearer fixture-token", "bearer authentication");
Check(!state.ToString().Contains("fixture-token"), "state ToString redacts credentials");
Reject(() => UuSigning.ValidatePath("https://other.example/api/v1/test"), "absolute API URL rejected");
Reject(() => UuSigning.ValidatePath("/api/v1/%2e%2e/login"), "encoded traversal rejected");
Reject(() => UuSigning.ValidatePath("/api/v2/unknown"), "unknown v2 endpoint rejected");
var wire = "52-/remote,17[\"soac\",{\"value\":true}]";
var packet = SocketIoCodec.Parse(wire);
Check(packet.Type == 5 && packet.Attachments == 2 && packet.Namespace == "/remote" && packet.Id == 17, "upstream binary packet fixture");
Check(SocketIoCodec.Encode(packet) == wire, "binary packet round trip");
Reject(() => SocketIoCodec.Parse("511-[\"event\"]"), "excessive binary attachments rejected");
Reject(() => SocketIoCodec.Parse("x"), "invalid packet rejected");
var clues = System.Text.Json.JsonSerializer.Serialize(ProtocolClues.Extract(
    "{\"token\":\"SECRET_TOKEN\",\"device_id\":\"SECRET_DEVICE\",\"SECRET_KEY\":\"192.0.2.1\"} /api/v1/room/create be-controlled"));
Check(!clues.Contains("SECRET") && !clues.Contains("192.0.2.1"), "probe drops values and unknown keys");
Check(clues.Contains("be-controlled") && clues.Contains("room/create") && clues.Contains("device_id"), "probe keeps known protocol clues");
var pointer = HostMouseMessage.Parse("{\"action\":\"mouse_move_absolute\",\"abs_x\":0.5,\"abs_y\":0.25}", PointerCoordinates.MacNormalized);
Check(pointer.Action == MouseAction.MoveAbsolute && pointer.X == 0.5 && pointer.Y == 0.25, "Mac mouse schema with synthetic coordinates");
var button = HostMouseMessage.Parse("{\"action\":\"mouse_press\",\"button\":1,\"screen_id\":0}", PointerCoordinates.MacNormalized);
Check(button.Action == MouseAction.Press && button.Button == 1 && button.ScreenId == 0, "mouse button and optional display");
Reject(() => HostMouseMessage.Parse("{\"action\":\"mouse_press\",\"button\":3}", PointerCoordinates.MacNormalized), "invalid mouse button rejected");
Reject(() => HostMouseMessage.Parse("{\"action\":\"mouse_move_absolute\",\"abs_x\":1.1,\"abs_y\":0}", PointerCoordinates.MacNormalized), "out of range normalized pointer rejected");
Reject(() => HostMouseMessage.Parse("{\"action\":\"mouse_press\",\"button\":1,\"button\":2}", PointerCoordinates.MacNormalized), "ambiguous duplicate input fields rejected");
Reject(() => HostMouseMessage.Parse("{\"action\":\"execute_command\",\"content\":\"fixture\"}", PointerCoordinates.MacNormalized), "unknown input action rejected");
using var hostRequest = UuMacHostProtocol.CreateRoomRequest(state, 0, 1700000000,
    Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
Check(hostRequest.Headers.GetValues("X-Param-SIGN").Single() ==
    "52b659657251c538f1ab58870f6adfa50de7eea0d9827cdc2ce372192ac4d216",
    "observed Mac header profile matches independent Python HMAC fixture");
Check(await hostRequest.Content!.ReadAsStringAsync() == "{\"last_controlled_interval\":0}"
    && hostRequest.Headers.Authorization?.Parameter == "fixture-token",
    "host request body and bearer authentication");
const string roomFixture = """
{"code":0,"data":{"signaling_server":"wss://signal.example.test/",
"signaling_list":["wss://signal.example.test/","wss://backup.example.test/"],
"token":"fixture-room-secret","ws_connect_timeout_ms":5000,"max_reconnect_delta":30,
"report_token":"fixture-report-secret","report_url":"https://report.example.test/",
"report_server_address":"192.0.2.1","streamer_retry_delta_ms":1000,"international_connect":false}}
""";
var assistFixture = JsonNode.Parse(roomFixture)!;
assistFixture["data"]!["publisher_platform"] = 4;
using (var doc = System.Text.Json.JsonDocument.Parse(assistFixture.ToJsonString()))
    Check(AssistanceRoom.Parse(doc.RootElement).Platform == 4, "assistance parses target platform with direct room configuration");
var nestedAssist = new JsonObject { ["code"] = 0, ["data"] = new JsonObject { ["publisher_platform"] = "1", ["room_config"] = JsonNode.Parse(roomFixture)!["data"]!.DeepClone() } };
using (var doc = System.Text.Json.JsonDocument.Parse(nestedAssist.ToJsonString()))
    Check(AssistanceRoom.Parse(doc.RootElement).Platform == 1, "assistance parses nested room and string platform");
var assistRequest = new AssistanceRequest("123456789", "Fixture123");
Check(!assistRequest.ToString().Contains("Fixture123") && assistRequest.TakeCode() == "Fixture123" && assistRequest.TakeCode() == "", "assistance credential is redacted and consumed once");
Reject(() => AssistanceRequest.Validate("not-an-id", "Fixture123"), "invalid partner id rejected");
Reject(() => AssistanceRequest.Validate("123456789", ""), "empty partner code rejected");
Check(StreamerConnectOptions.Decode(ControllerProtocol.ConnectOptions("fixture", assistance: true)).ControlConnectType == 2, "assistance connection is explicitly marked as assistance");
await AssistanceJoinTests.Run(Check, assistFixture.ToJsonString());
var room = UuMacHostProtocol.ParseRoomResponse(roomFixture);
Check(room.SignalingList.Count == 2 && room.WebSocketConnectTimeoutMs == 5000
    && room.Token == "fixture-room-secret", "observed room configuration schema parses synthetic data");
Check(!room.ToString().Contains("secret") && !room.ToString().Contains("example.test"),
    "room configuration ToString redacts tokens and endpoints");
try
{
    UuMacHostProtocol.ParseRoomResponse(roomFixture.Replace("wss://", "ws://"));
    throw new Exception("Insecure signaling accepted.");
}
catch (InvalidDataException) { Check(true, "unencrypted signaling endpoint rejected"); }
var signalReader = new UuSignalReader();
Check(signalReader.ReadText("0{\"sid\":\"fixture\",\"pingInterval\":15000,\"pingTimeout\":18000}")?.EngineType == 0,
    "Engine.IO open packet");
Check(signalReader.ReadText("451-[\"soac\",{\"data\":{\"gzip_sdp\":{\"_placeholder\":true,\"num\":0}}}]") is null,
    "binary event waits for attachment");
Check(signalReader.ReadText("2")?.EngineType == 2, "heartbeat can interleave with pending binary event");
using var zipped = new MemoryStream();
zipped.WriteByte(4);
using (var compressor = new System.IO.Compression.GZipStream(zipped, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
    compressor.Write(System.Text.Encoding.UTF8.GetBytes("v=0\r\ns=fixture\r\n"));
var signalFrame = signalReader.ReadBinary(zipped.ToArray())!;
Check(UuSignalReader.DecodeGzipSdp(signalFrame) == "v=0\r\ns=fixture\r\n", "UU binary prefix and gzip SDP decode");
Reject(() => signalReader.ReadBinary(new byte[] { 4, 1 }), "unsolicited binary attachment rejected");
Check(!signalFrame.ToString().Contains("fixture"), "signal frame logging redacts content");
using var handler = new FixtureHandler();
using var api = new UuControllerApi(state, handler);
await api.GetDevicesAsync();
Check(handler.Requests == 1, "device API sends direct .NET request");
HostSessionTests.Run(Check);
WaylandTests.Run(Check);
ControlInputTests.Run(Check);
DisplayInfoTests.Run(Check);
await SignalClientTests.Run(Check);
await HostApiTests.Run(Check);
Console.WriteLine($"{passed} checks passed. No live account or UU network calls were used.");

sealed class FixtureHandler : HttpMessageHandler
{
    public int Requests { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.ToString() != "https://api.nrd.nie.163.com/api/v1/device/groups/of/my"
            || request.Method != HttpMethod.Get || request.Headers.Authorization?.Parameter != "fixture-token"
            || !request.Headers.Contains("X-Param-SIGN")) throw new Exception("Unexpected HTTP request.");
        Requests++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":0,\"data\":{}}") });
    }
}
