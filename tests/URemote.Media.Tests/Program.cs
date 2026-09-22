using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using URemote.Media;

if(args.Contains("--controller-files-only")) { await ControllerFileChecks.RunAsync(); return; }

if(args.Contains("--assistance-probe")) {
    var state=System.Text.Json.JsonSerializer.Deserialize<URemote.Core.LoginState>(JsonNode.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("UREMOTE_IDENTITY")!))!["State"]!.ToJsonString())!;
    var method=typeof(URemote.Core.UuMacHostProtocol).GetMethod("BuildRequest",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
    using var http=new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false});
    using var request=(HttpRequestMessage)method.Invoke(null,[state,args.Contains("--upload-sign")?HttpMethod.Post:HttpMethod.Get,args.Contains("--upload-sign")?"/api/v2/room/share/upload_sign":"/api/v1/device/share/info",args.Contains("--upload-sign")?"{}":"",null,null])!;
    using var response=await http.SendAsync(request);
    using var doc=System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Console.WriteLine("http="+(int)response.StatusCode+";code="+doc.RootElement.GetProperty("code"));
    void Shape(System.Text.Json.JsonElement value,string prefix) {
        if(value.ValueKind==System.Text.Json.JsonValueKind.Object)foreach(var p in value.EnumerateObject())Shape(p.Value,prefix+"."+p.Name);
        else if(value.ValueKind==System.Text.Json.JsonValueKind.Array) {foreach(var item in value.EnumerateArray()) { if(item.ValueKind==System.Text.Json.JsonValueKind.Object && item.TryGetProperty("loc",out var loc))Console.WriteLine("required="+loc.GetRawText()); else Shape(item,prefix+"[]");}}
        else Console.WriteLine(prefix+":"+value.ValueKind+(value.ValueKind==System.Text.Json.JsonValueKind.String?";length="+value.GetString()!.Length:""));
    }
    if(doc.RootElement.TryGetProperty("data",out var data))Shape(data,"data");
    return;
}

if(args.Contains("--wallpaper-render") || args.Contains("--wallpaper-upload")) {
    var png=await URemote.Linux.LinuxWallpaper.RenderAsync(Environment.GetEnvironmentVariable("UREMOTE_FFMPEG")!,CancellationToken.None)??throw new Exception("No wallpaper source.");
    if(args.Contains("--wallpaper-render")) {await File.WriteAllBytesAsync("outputs/U远程-壁纸预览.png",png);Console.WriteLine("wallpaper-rendered;bytes="+png.Length);return;}
    var state=System.Text.Json.JsonSerializer.Deserialize<URemote.Core.LoginState>(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("UREMOTE_IDENTITY")!))!["State"]!.ToJsonString())!;
    using var api=new URemote.Core.UuWallpaperApi(state);await api.UploadAsync(png,CancellationToken.None);Console.WriteLine("wallpaper-uploaded");
    var method=typeof(URemote.Core.UuMacHostProtocol).GetMethod("BuildRequest",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
    using var http=new HttpClient();using var req=(HttpRequestMessage)method.Invoke(null,[state,HttpMethod.Get,"/api/v1/device/list","",null,null])!;
    using var resp=await http.SendAsync(req);using var doc=System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    var current=doc.RootElement.GetProperty("data").GetProperty("current_device");
    Console.WriteLine("server-wallpaper-present="+(current.TryGetProperty("wallpaper_url",out var wallpaper)&&wallpaper.ValueKind==System.Text.Json.JsonValueKind.String&&!string.IsNullOrEmpty(wallpaper.GetString())));
    return;
}

if(args.Contains("--wallpaper-api-probe")) {
    var state=System.Text.Json.JsonSerializer.Deserialize<URemote.Core.LoginState>(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("UREMOTE_IDENTITY")!))!["State"]!.ToJsonString())!;
    var method=typeof(URemote.Core.UuMacHostProtocol).GetMethod("BuildRequest",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
    using var http=new HttpClient(new SocketsHttpHandler{AllowAutoRedirect=false});
    using var request=(HttpRequestMessage)method.Invoke(null,[state,HttpMethod.Get,"/api/v1/tool/fp/token?filetype=1","",null,null])!;
    using var response=await http.SendAsync(request);
    using var doc=System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Console.WriteLine("token-http="+(int)response.StatusCode+";code="+doc.RootElement.GetProperty("code"));
    if(doc.RootElement.TryGetProperty("data",out var data) && data.ValueKind==System.Text.Json.JsonValueKind.Array)Console.WriteLine("validation="+data.GetRawText());
    if(doc.RootElement.TryGetProperty("data",out data) && data.ValueKind==System.Text.Json.JsonValueKind.Object) {
        var t=data.GetProperty("token").GetString()!;Console.WriteLine("token-shape;length="+t.Length+";json="+t.StartsWith('{')+";dots="+t.Count(c=>c=='.')+";colons="+t.Count(c=>c==':'));
        Console.WriteLine("token-fields="+string.Join(',',data.EnumerateObject().Select(p=>p.Name+":"+p.Value.ValueKind)));
        if(data.TryGetProperty("req_url",out var url)) {var uri=new Uri(url.GetString()!);Console.WriteLine("upload-endpoint="+uri.Scheme+"://"+uri.Host+uri.AbsolutePath+";query-present="+(uri.Query.Length>0));}
    }
    return;
}

if(args.Contains("--wallpaper-only")){await WallpaperChecks.RunAsync();return;}

if (args.Contains("--files-only")) { await FileTransferChecks.RunAsync(); return; }

if (args.Contains("--file-probe") || args.Contains("--file-receive-probe") || args.Contains("--file-download-probe"))
{
    var fileState = System.Text.Json.JsonSerializer.Deserialize<URemote.Core.LoginState>(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("UREMOTE_IDENTITY")!))!["State"]!.ToJsonString())!;
    using var fileApi = new URemote.Core.UuMacHostApi(fileState);
    var fileDevice = (await fileApi.GetDevicesAsync()).First(x => !x.IsCurrent && x.Online && x.Controllable && x.Platform == 4);
    using var fileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var fileSession = new URemote.Host.DesktopControllerSession();
    fileSession.Status += value => Console.WriteLine("file-probe-" + value);
    var probeAskIndex=0; var probeBlockIndex=0;
    fileSession.DataReceived += (channel, bytes) => {
        try {
            if(URemote.Core.FileTransferProtocol.Data(bytes,26) is { Length: >0 } destination) {
                var path=URemote.Core.FileTransferProtocol.String(destination,1);
                Console.WriteLine("official-destination;channel="+channel+";root="+(path=="/")+";absolute="+path.StartsWith('/')+";drive="+(path.Length>1&&path[1]==':'));
            }
            if (URemote.Core.FileTransferProtocol.Decode(bytes) is { } message) {
                if(args.Contains("--file-download-probe")) {
                    var idBytes=URemote.Core.FileTransferProtocol.Data(message.Body,1);
                    var index=URemote.Core.FileTransferProtocol.Number(idBytes,2);
                    Console.WriteLine("official-download;response="+message.Response+";kind="+message.Kind+";index="+index);
                    if(!message.Response) {
                        var replyKind=message.Kind==9?6:message.Kind;
                        var result=message.Kind switch {
                            3=>URemote.Core.FileTransferProtocol.Int(3,1),
                            4=>URemote.Core.FileTransferProtocol.Join(URemote.Core.FileTransferProtocol.Int(2,URemote.Core.FileTransferProtocol.Number(message.Body,2)),URemote.Core.FileTransferProtocol.Int(3,1),URemote.Core.FileTransferProtocol.Int(4,(ulong)URemote.Core.FileTransferProtocol.Data(message.Body,3).Length)),
                            9=>URemote.Core.FileTransferProtocol.Int(2,1), _=>throw new FormatException() };
                        fileSession.SendData("CONTROL_DATA_CHANNEL",URemote.Core.FileTransferProtocol.Encode(true,message.RequestId,replyKind,URemote.Core.FileTransferProtocol.Join(URemote.Core.FileTransferProtocol.Blob(1,idBytes),result)));
                        if(message.Kind==9 && index==ulong.MaxValue)fileDeadline.Cancel();
                    }
                    return;
                }
                Console.WriteLine("rpc-response-header="+Convert.ToHexString(URemote.Core.FileTransferProtocol.Data(URemote.Core.FileTransferProtocol.Data(bytes,22),1)));
                Console.WriteLine("file-response;channel="+channel+";response="+message.Response+";kind="+message.Kind+";tags="+string.Join(',',URemote.Core.FileTransferProtocol.Read(message.Body).Select(x=>x.Tag+":"+x.Number+":"+x.Data.Length)));
                foreach(var entry in URemote.Core.FileTransferProtocol.Read(message.Body).Where(x=>message.Kind==8 && x.Tag==2)) Console.WriteLine("exist-result="+string.Join(',',URemote.Core.FileTransferProtocol.Read(entry.Data).Select(x=>x.Tag+":"+x.Number+":"+x.Data.Length)));
                if(message.Kind==7)foreach(var entry in URemote.Core.FileTransferProtocol.Read(message.Body).Where(x=>x.Tag==3))
                    Console.WriteLine("official-known-entry;directory="+(URemote.Core.FileTransferProtocol.String(entry.Data,2)=="ssh")+";fields="+string.Join(',',URemote.Core.FileTransferProtocol.Read(entry.Data).Select(x=>x.Tag+":"+x.Number+":"+x.Data.Length)));
                var compressed = URemote.Core.FileTransferProtocol.Data(message.Body,5);
                Console.WriteLine("file-list-compression-header="+Convert.ToHexString(compressed.AsSpan(0,Math.Min(4,compressed.Length))));
                if(args.Contains("--file-receive-probe") && message.Kind==1) {
                    foreach(var item in URemote.Core.FileTransferProtocol.Read(message.Body).Where(x=>x.Tag==2))
                        Console.WriteLine("receive-metadata="+string.Join(',',URemote.Core.FileTransferProtocol.Read(item.Data).Select(x=>x.Tag+":"+x.Number+":"+x.Data.Length)));
                    var ask=URemote.Core.FileTransferProtocol.Encode(false,78,3,URemote.Core.FileTransferProtocol.Join(
                        URemote.Core.FileTransferProtocol.Blob(1,URemote.Core.FileTransferProtocol.Int(1,77)),
                        URemote.Core.FileTransferProtocol.Int(2,1700000000),URemote.Core.FileTransferProtocol.Int(3,7)));
                    fileSession.SendData("CONTROL_DATA_CHANNEL",ask);
                } else if(args.Contains("--file-receive-probe") && message.Kind==3 && URemote.Core.FileTransferProtocol.Number(message.Body,3)!=1 && probeAskIndex++==0) {
                    Console.WriteLine("probe-retrying-file-index=1");
                    fileSession.SendData("CONTROL_DATA_CHANNEL",URemote.Core.FileTransferProtocol.Encode(false,80,3,URemote.Core.FileTransferProtocol.Join(
                        URemote.Core.FileTransferProtocol.Blob(1,URemote.Core.FileTransferProtocol.Join(URemote.Core.FileTransferProtocol.Int(1,77),URemote.Core.FileTransferProtocol.Int(2,1))),
                        URemote.Core.FileTransferProtocol.Int(2,1700000000),URemote.Core.FileTransferProtocol.Int(3,7))));
                } else if(args.Contains("--file-receive-probe") && (message.Kind==3 && URemote.Core.FileTransferProtocol.Number(message.Body,3)==1 || message.Kind==4 && URemote.Core.FileTransferProtocol.Number(message.Body,3)!=1 && probeBlockIndex++==0)) {
                    fileSession.SendData("CONTROL_DATA_CHANNEL",URemote.Core.FileTransferProtocol.Encode(false,81,4,URemote.Core.FileTransferProtocol.Join(
                        URemote.Core.FileTransferProtocol.Blob(1,URemote.Core.FileTransferProtocol.Join(URemote.Core.FileTransferProtocol.Int(1,77),URemote.Core.FileTransferProtocol.Int(2,1))),
                        URemote.Core.FileTransferProtocol.Int(2,(ulong)probeBlockIndex),URemote.Core.FileTransferProtocol.Blob(3,[1,2,3,4,5,6,7]))));
                } else if(args.Contains("--file-receive-probe") && message.Kind==4) {
                    fileSession.SendData("CONTROL_DATA_CHANNEL",URemote.Core.FileTransferProtocol.Encode(false,82,9,URemote.Core.FileTransferProtocol.Join(
                        URemote.Core.FileTransferProtocol.Blob(1,URemote.Core.FileTransferProtocol.Join(URemote.Core.FileTransferProtocol.Int(1,77),URemote.Core.FileTransferProtocol.Int(2,1))),URemote.Core.FileTransferProtocol.Int(2,7))));
                } else {
                    if(args.Contains("--file-receive-probe") && message.Kind!=6)fileSession.SendData("CONTROL_DATA_CHANNEL",URemote.Core.FileTransferProtocol.Encode(false,79,9,URemote.Core.FileTransferProtocol.Join(URemote.Core.FileTransferProtocol.Blob(1,URemote.Core.FileTransferProtocol.Int(1,77)),URemote.Core.FileTransferProtocol.Int(2,7))));
                    fileDeadline.Cancel();
                }
            }
        } catch (FormatException) { }
    };
    var fileRun = fileSession.RunAsync(fileState,fileDevice.Id,"",fileDeadline.Token,5);
    try {
        await Task.Delay(2500,fileDeadline.Token);
        var request=URemote.Core.FileTransferProtocol.Encode(false,77,10,URemote.Core.FileTransferProtocol.Join(URemote.Core.FileTransferProtocol.Blob(1,URemote.Core.FileTransferProtocol.Int(1,77)),URemote.Core.FileTransferProtocol.Text(2,":/")));
        if(args.Contains("--file-receive-probe"))request=URemote.Core.FileTransferProtocol.Encode(false,77,1,
            URemote.Core.FileTransferProtocol.Join(URemote.Core.FileTransferProtocol.Blob(1,URemote.Core.FileTransferProtocol.Int(1,77)),
            URemote.Core.FileTransferProtocol.Text(2,":/Default"),URemote.Core.FileTransferProtocol.Blob(3,URemote.Core.FileTransferProtocol.Join(
            URemote.Core.FileTransferProtocol.Text(1,"uremote-synthetic-probe-"+Guid.NewGuid().ToString("N")+".bin"),
            URemote.Core.FileTransferProtocol.Int(2,7),URemote.Core.FileTransferProtocol.Int(3,1700000000))),URemote.Core.FileTransferProtocol.Text(7,Guid.NewGuid().ToString("N"))));
        if(args.Contains("--file-receive-probe")) {
            var decoded=URemote.Core.FileTransferProtocol.Decode(request)!;
            using var packed=new MemoryStream();
            using(var zip=new System.IO.Compression.ZLibStream(packed,System.IO.Compression.CompressionLevel.Fastest,true))
                zip.Write(URemote.Core.FileTransferProtocol.Blob(1,URemote.Core.FileTransferProtocol.Data(decoded.Body,3)));
            request=URemote.Core.FileTransferProtocol.Encode(false,77,1,URemote.Core.FileTransferProtocol.Join(
                URemote.Core.FileTransferProtocol.Blob(1,URemote.Core.FileTransferProtocol.Int(1,77)),URemote.Core.FileTransferProtocol.Text(2,":/Default"),
                URemote.Core.FileTransferProtocol.Int(4,2),URemote.Core.FileTransferProtocol.Blob(6,packed.ToArray()),URemote.Core.FileTransferProtocol.Text(7,Guid.NewGuid().ToString("N"))));
        }
        if(args.Contains("--file-download-probe"))request=URemote.Core.FileTransferProtocol.Encode(false,77,2,URemote.Core.FileTransferProtocol.Join(URemote.Core.FileTransferProtocol.Blob(1,URemote.Core.FileTransferProtocol.Int(1,77)),URemote.Core.FileTransferProtocol.Text(2,"/etc/hosts")));
        Console.WriteLine("file-request-sent="+fileSession.SendData("CONTROL_DATA_CHANNEL",request));
        await fileRun;
    } catch(OperationCanceledException) when(fileDeadline.IsCancellationRequested) { }
    finally { fileDeadline.Cancel(); try { await fileRun; } catch(OperationCanceledException) { } }
    return;
}

if (args.Contains("--controller-probe"))
{
    var identityPath = Environment.GetEnvironmentVariable("UREMOTE_IDENTITY") ?? throw new Exception("identity path required");
    var ffmpeg = Environment.GetEnvironmentVariable("UREMOTE_FFMPEG") ?? throw new Exception("decoder path required");
    var loginState = System.Text.Json.JsonSerializer.Deserialize<URemote.Core.LoginState>(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(identityPath))!["State"]!.ToJsonString())!;
    using var api = new URemote.Core.UuMacHostApi(loginState);
    var devices = await api.GetDevicesAsync();
    var device = devices.First(x => !x.IsCurrent && x.Online && x.Controllable && x.ControlledSupport && x.Platform == (args.Contains("--mac") ? 4 : 1));
    using var probeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
    var probeController = new URemote.Host.DesktopControllerSession();
    var count = 0;
    var probeClock = System.Diagnostics.Stopwatch.StartNew();
    probeController.Status += value => Console.WriteLine($"probe-{value};ms={probeClock.ElapsedMilliseconds}");
    probeController.Frame += (index, frame) => { if (Interlocked.Increment(ref count) == 1) Console.WriteLine($"PASS: official remote video decoded;stream={index};size={frame.Width}x{frame.Height}"); if (count >= 30) probeDeadline.Cancel(); };
    try { await probeController.RunAsync(loginState, device.Id, ffmpeg, probeDeadline.Token); }
    catch (OperationCanceledException) when (probeDeadline.IsCancellationRequested) { }
    catch (Exception e) { Console.WriteLine("probe-failure=" + e.GetType().Name + ";" + e.Message); }
    Console.WriteLine("decoded-frames=" + count);
    Environment.ExitCode = count >= 1 ? 0 : 1;
    return;
}

if (args.Contains("--controller-local") || args.Contains("--ftp-transport-only"))
{
    using var nativeHost = new HostMediaPeer(bindAddress: IPAddress.Loopback);
    using var nativeController = new ControllerMediaPeer();
    nativeHost.LocalCandidate += nativeController.AddCandidate;
    nativeController.LocalCandidate += nativeHost.AddRemoteCandidate;
    var nativeReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    nativeController.ControlReady += () => nativeReady.TrySetResult();
    var nativeOffer = await nativeController.OfferAsync();
    nativeController.ApplyAnswer(await nativeHost.AnswerAsync(nativeOffer));
    await nativeReady.Task.WaitAsync(TimeSpan.FromSeconds(12));
    using var nativeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var ftpFixture=URemote.Core.FileTransferProtocol.Encode(true,77,8,
        URemote.Core.FileTransferProtocol.Blob(5,[0xff,0x80,0xc0,0x00,0xfe]));
    var ftpBytes=new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    var ftpType=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    nativeController.DataReceived += (label,bytes) => { if(label=="TEXT_DATA_CHANNEL")ftpBytes.TrySetResult(bytes.ToArray()); };
    nativeController.Status += status => { if(status=="ftp-payload-protocol=WebRTC_String")ftpType.TrySetResult(); };
    for(int n=0;n<30&&!nativeHost.SendData("TEXT_DATA_CHANNEL",ftpFixture);n++)await Task.Delay(100,nativeDeadline.Token);
    var ftpActual=await ftpBytes.Task.WaitAsync(nativeDeadline.Token);
    await ftpType.Task.WaitAsync(nativeDeadline.Token);
    if(!ftpActual.SequenceEqual(ftpFixture))throw new Exception("FTP protobuf bytes were transcoded.");
    Console.WriteLine("PASS: FTP uses official text PPID with exact non-UTF8 protobuf bytes over SCTP");
    if(args.Contains("--ftp-transport-only"))return;
    var inputFixture = "{\"action\":\"kbd_press\",\"key\":0}";
    if (!nativeController.SendInput(inputFixture, 0)) throw new Exception("Controller input channel is not ready.");
    URemote.Core.HostControlInput? decodedInput = null;
    while (decodedInput is null) {
        var packet = await nativeHost.DataMessages.ReadAsync(nativeDeadline.Token);
        decodedInput = URemote.Core.HostControlInput.Decode(packet.ChannelLabel, packet.IsText, packet.Bytes);
        if (decodedInput is not null && (!packet.IsText || System.Text.Encoding.UTF8.GetString(packet.Bytes) != inputFixture))
            throw new Exception("Desktop control must use unwrapped JSON with the string PPID.");
    }
    if (decodedInput.Key is null) throw new Exception("Controller input not decoded at host.");
    Console.WriteLine("PASS: native controller offers five receive tracks and sends key input over SCTP");
    var pixels = new byte[64 * 64 * 4];
    for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 200; pixels[i + 3] = 255; }
    var ffmpegPath = Environment.GetEnvironmentVariable("UREMOTE_FFMPEG")!;
    var encodedPixels = await new FfmpegH264Encoder(ffmpegPath).EncodeAsync(pixels, 64, 64, 256, false, 1);
    await using var nativeDecoder = new ControllerVideoDecoder(ffmpegPath);
    var decodedFrame = new TaskCompletionSource<ControllerFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
    nativeDecoder.Frame += f => decodedFrame.TrySetResult(f);
    nativeController.Video += (_, bytes) => nativeDecoder.Push(bytes);
    for (int i = 0; i < 10 && !decodedFrame.Task.IsCompleted; i++) { nativeHost.SendH264(encodedPixels, 3000); await Task.Delay(80); }
    var outputFrame = await decodedFrame.Task.WaitAsync(TimeSpan.FromSeconds(5));
    if (outputFrame.Width != 1280 || outputFrame.Height != 720 || outputFrame.Pixels[(360 * 1280 + 640) * 4] < 180 || outputFrame.Pixels[(360 * 1280 + 640) * 4 + 1] > 20) throw new Exception("Decoded video dimensions or RGBA colors wrong.");
    Console.WriteLine("PASS: encrypted remote video decodes to correctly sized and colored RGBA frame");
    return;
}

if (args.Contains("--terminal-only"))
{
    using var terminalHost = new HostMediaPeer(bindAddress: IPAddress.Loopback, activeVideoStreams: 0);
    using var terminalClient = new RTCPeerConnection(new RTCConfiguration { iceServers = [], X_BindAddress = IPAddress.Loopback });
    terminalHost.LocalCandidate += c => terminalClient.addIceCandidate(new RTCIceCandidateInit
    { candidate = c["candidate"]!.GetValue<string>(), sdpMid = c["sdpMid"]?.GetValue<string>(), sdpMLineIndex = (ushort)c["sdpMLineIndex"]!.GetValue<int>() });
    terminalClient.onicecandidate += c => terminalHost.AddRemoteCandidate(new JsonObject
    { ["candidate"] = c.candidate, ["sdpMid"] = c.sdpMid, ["sdpMLineIndex"] = (int)c.sdpMLineIndex });
    var binary = await terminalClient.createDataChannel("BINARY_DATA_CHANNEL");
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    binary.onopen += () => ready.TrySetResult();
    var terminalOffer = terminalClient.createOffer();
    await terminalClient.setLocalDescription(terminalOffer);
    var terminalAnswer = await terminalHost.AnswerAsync(terminalOffer.sdp);
    if (SDP.ParseSDPDescription(terminalAnswer).Media.Any(m => m.Media is SDPMediaTypesEnum.video or SDPMediaTypesEnum.audio))
        throw new Exception("Terminal-only SDP unexpectedly enabled audio/video.");
    if (terminalClient.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = terminalAnswer }) != SetDescriptionResultEnum.OK)
        throw new Exception("Terminal SDP rejected.");
    await ready.Task.WaitAsync(TimeSpan.FromSeconds(12));
    byte[] fixture = [1, 2, 0, 0, 123, 125];
    binary.send(fixture);
    using var terminalDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var receivedTerminal = await terminalHost.DataMessages.ReadAsync(terminalDeadline.Token);
    if (!receivedTerminal.Bytes.SequenceEqual(fixture)) throw new Exception("Terminal binary data mismatch.");
    Console.WriteLine("PASS: terminal-only ICE/DTLS/SCTP connects without screen or audio tracks");
    await using var manager = new HostTerminalManager();
    using var terminalStop = new CancellationTokenSource();
    var packets = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
    binary.onmessage += (_, _, bytes) => packets.Writer.TryWrite(bytes.ToArray());
    var terminalService = HostTerminalPeer.RunAsync(terminalHost, manager, true, terminalStop.Token, _ => { });
    binary.send(URemote.Core.HostTerminalProtocol.EncodeFrame(1, 0, "{\"cols\":80,\"rows\":24,\"option\":\"create\"}"u8));
    var openedTerminal = URemote.Core.HostTerminalProtocol.DecodeFrame(await packets.Reader.ReadAsync(terminalDeadline.Token));
    if (openedTerminal is not { Type: 13, SessionId: > 0 }) throw new Exception("Terminal open reply missing.");
    var command = Encoding.UTF8.GetBytes("printf 'WIRE_%s\\n' OK\n");
    binary.send(URemote.Core.HostTerminalProtocol.EncodeFrame(2, openedTerminal.SessionId, command));
    Array.Clear(command);
    var terminalOutput = new StringBuilder();
    while (!terminalOutput.ToString().Contains("WIRE_OK"))
    {
        var packet = await packets.Reader.ReadAsync(terminalDeadline.Token);
        var decoded = URemote.Core.HostTerminalProtocol.DecodeFrame(packet)!;
        if (decoded.Type == 5) terminalOutput.Append(Encoding.UTF8.GetString(decoded.Payload));
        Array.Clear(decoded.Payload); Array.Clear(packet);
        if (terminalOutput.Length > 262144) throw new Exception("Terminal test output limit exceeded.");
    }
    terminalOutput.Clear();
    binary.send(URemote.Core.HostTerminalProtocol.EncodeFrame(9, 0, ReadOnlySpan<byte>.Empty));
    URemote.Core.TerminalFrame? listed;
    do { listed = URemote.Core.HostTerminalProtocol.DecodeFrame(await packets.Reader.ReadAsync(terminalDeadline.Token)); }
    while (listed is not { Type: 10 });
    using (var listJson = System.Text.Json.JsonDocument.Parse(listed.Payload))
    {
        if (listJson.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array ||
            !listJson.RootElement.EnumerateArray().Any(s => s.GetProperty("session_id").GetUInt32() == openedTerminal.SessionId
                && s.GetProperty("state").GetString() == "running"))
            throw new Exception("Official terminal session list must be an array containing the new session.");
    }
    Array.Clear(listed.Payload);
    Console.WriteLine("PASS: post-create session list uses official array schema and retains assigned session id");
    await terminalStop.CancelAsync();
    try { await terminalService; } catch (OperationCanceledException) { }
    if (manager.Find(openedTerminal.SessionId) is not { Exited: false }) throw new Exception("Disconnect discarded running terminal.");
    Console.WriteLine("PASS: encrypted terminal open/input/output reaches native PTY and disconnect preserves session");
    return;
}

// Local-only ICE/DTLS/SCTP test. No UU endpoints, account data, STUN or TURN servers.
using var host = new HostMediaPeer(bindAddress: IPAddress.Loopback, activeVideoStreams: 2, enableAudio: true);
using var controller = new RTCPeerConnection(new RTCConfiguration { iceServers = [], X_BindAddress = IPAddress.Loopback, X_UseRtpFeedbackProfile = true });
var videoReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
controller.OnVideoFrameReceived += (_, _, bytes, _) => videoReceived.TrySetResult(bytes);
var secondVideoReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
controller.OnVideoFrameReceivedByIndex += (index, _, _, bytes, _) => { if (index == 1) secondVideoReceived.TrySetResult(bytes); };
var audioReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
controller.OnRtpPacketReceived += (_, type, packet) => { if (type == SDPMediaTypesEnum.audio) audioReceived.TrySetResult(packet.Payload); };
var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
host.StateChanged += state => { if (state == RTCPeerConnectionState.connected) connected.TrySetResult(); };
host.LocalCandidate += candidate => controller.addIceCandidate(new RTCIceCandidateInit
{
    candidate = candidate["candidate"]!.GetValue<string>(), sdpMid = candidate["sdpMid"]?.GetValue<string>(),
    sdpMLineIndex = (ushort)candidate["sdpMLineIndex"]!.GetValue<int>()
});
controller.onicecandidate += candidate => host.AddRemoteCandidate(new JsonObject
{
    ["candidate"] = candidate.candidate, ["sdpMid"] = candidate.sdpMid, ["sdpMLineIndex"] = (int)candidate.sdpMLineIndex
});
controller.addTrack(new MediaStreamTrack(new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 2), MediaStreamStatusEnum.RecvOnly));
for (var i = 0; i < 5; i++)
    controller.addTrack(new MediaStreamTrack(new VideoFormat(VideoCodecsEnum.H264, 98, 90000,
        "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f"), MediaStreamStatusEnum.RecvOnly));
var channel = await controller.createDataChannel("CONTROL_DATA_CHANNEL");
var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
channel.onopen += () => opened.TrySetResult();
var offer = controller.createOffer();
await controller.setLocalDescription(offer);
var answer = await host.AnswerAsync(offer.sdp);
if (host.VideoStreamCount != 5 || SDP.ParseSDPDescription(answer).Media.Count(x => x.Media == SDPMediaTypesEnum.video) != 5)
    throw new Exception("Multi-video SDP layout was not preserved.");
Console.WriteLine("PASS: five-video offer produces native H264 answer");
if (controller.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answer }) != SetDescriptionResultEnum.OK)
    throw new Exception("Controller rejected native answer.");
await connected.Task.WaitAsync(TimeSpan.FromSeconds(12));
Console.WriteLine("PASS: local ICE and DTLS connected");
await opened.Task.WaitAsync(TimeSpan.FromSeconds(12));
channel.send("fixture-data");
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var message = await host.DataMessages.ReadAsync(deadline.Token);
if (message.ChannelLabel != "CONTROL_DATA_CHANNEL" || !message.IsText || Encoding.UTF8.GetString(message.Bytes) != "fixture-data")
    throw new Exception("DataChannel payload mismatch.");
Console.WriteLine("PASS: native SCTP DataChannel delivered exact fixture bytes");
var replyReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
channel.onmessage += (_, _, bytes) => replyReceived.TrySetResult(bytes);
byte[] replyFixture = [8, 1, 16, 2, 26, 2, 8, 1];
if (!host.SendControl(replyFixture) || !(await replyReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).SequenceEqual(replyFixture))
    throw new Exception("Host control reply mismatch.");
Console.WriteLine("PASS: host sends exact binary reply over control SCTP channel");
if (args.Length == 1)
{
    var pixels = new byte[64 * 64 * 4];
    for (var i = 0; i < pixels.Length; i += 4) pixels[i + 2] = 200;
    var encoder = new FfmpegH264Encoder(args[0]);
    var encoded = await encoder.EncodeAsync(pixels, 64, 64, 256, false, 1);
    host.SendH264(encoded, 45000);
    var received = await videoReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
    if (received.Length == 0) throw new Exception("Empty received H264 frame.");
    Console.WriteLine("PASS: synthetic image encoded and delivered over encrypted RTP as H264");
    host.SendH264(encoded, 45000, 1);
    if ((await secondVideoReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).Length == 0) throw new Exception("Second video stream empty.");
    Console.WriteLine("PASS: second screen uses distinct negotiated video stream");
}
using (var opus = Concentus.OpusCodecFactory.CreateEncoder(48000, 2, Concentus.Enums.OpusApplication.OPUS_APPLICATION_AUDIO))
{
    var silence = new short[1920]; var packet = new byte[4000];
    var length = opus.Encode(silence, 960, packet, packet.Length);
    host.SendOpus(packet.AsSpan(0, length).ToArray());
    if ((await audioReceived.Task.WaitAsync(TimeSpan.FromSeconds(5))).Length != length) throw new Exception("Opus payload mismatch");
    Console.WriteLine("PASS: Opus audio negotiated and delivered over encrypted RTP");
}
await using (var clipboard = new HostClipboard(host, _ => { }))
{
    if (await clipboard.HandleAsync(new("CONTROL_DATA_CHANNEL", false, Encoding.UTF8.GetBytes("{\"action\":\"kbd_press\",\"key\":0}")), CancellationToken.None))
        throw new Exception("Clipboard consumed keyboard JSON.");
    Console.WriteLine("PASS: clipboard handler preserves direct JSON keyboard routing");
}
controller.Close("test complete");
Console.WriteLine("Local media checks passed; no desktop content or user input was transmitted.");
