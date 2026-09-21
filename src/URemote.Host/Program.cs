using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using URemote.Core;
using URemote.Linux;

// Foreground experimental CLI. No background service is installed.
if (args.Length < 2 || (args[0] is "--host-preview" or "--host-control" ? args.Length != 4 : args.Length != 2)
    || args[0] is not ("--initialize" or "--login" or "--status" or "--signal-check" or "--host-preview" or "--host-control"))
{
    Console.WriteLine("U远程本机实验工具（尚不提供完整远控）\n用法：URemote.Host <命令> /private/path/identity.json\n--initialize  创建独立设备身份\n--login       在本机输入手机号并发送一次验证码，登录后私密保存令牌\n--status      只读显示初始化/登录状态，不输出身份或令牌\n--signal-check  短时创建被控房间并验证发布者信令，完成后关闭；不传屏、不接收输入。\n--host-preview identity.json /path/to/ffmpeg output-global-id  前台只读传屏，最长 5 分钟，Ctrl+C 停止。\n--host-control identity.json /path/to/ffmpeg output-global-id  同上，启用鼠标和常用物理键盘。");
    return;
}
if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("This bootstrap targets Linux.");
var path = Path.GetFullPath(args[1]);
try
{
    // Cooperating CLI processes cannot concurrently replace this identity or send duplicate SMS.
    await using var identityLock = new FileStream(path + ".lock", new FileStreamOptions
    { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None,
        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
    LocalIdentity identity;
    if (File.Exists(path))
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidOperationException("身份文件必须是私有普通文件（权限 600）。");
        identity = JsonSerializer.Deserialize<LocalIdentity>(await File.ReadAllTextAsync(path)) ?? throw new InvalidDataException("Invalid identity file.");
    }
    else
    {
        if (args[0] != "--initialize") throw new InvalidOperationException("请先初始化本机设备。");
        var client = Guid.NewGuid().ToString();
        var profile = new HostDeviceProfile(LinuxDeviceProfile.DeviceName, client, Guid.NewGuid().ToString(), Environment.OSVersion.VersionString,
            RuntimeInformation.ProcessArchitecture.ToString(), GC.GetGCMemoryInfo().TotalAvailableMemoryBytes.ToString(),
            "NixOS", "Linux desktop", "", "NixOS", "Linux", "", "", [], 96);
        identity = new(new(ClientId: client, Channel: "gwqd"), profile, "uninitialized");
        await SaveAsync(path, identity);
    }
    if (args[0] == "--status")
    {
        Console.WriteLine(JsonSerializer.Serialize(new { initialized = identity.State.DeviceId.Length > 0,
            authenticatedLocally = identity.State.IsAuthenticated, serverLoginValidated = false, remoteControlRunning = false }));
        return;
    }
    using var api = new UuMacHostApi(identity.State);
    if (args[0] == "--initialize")
    {
        if (identity.State.DeviceId.Length > 0) { Console.WriteLine("本机身份已初始化，此状态不代表被控在线。"); return; }
        await api.InitializeAsync(identity.Profile);
        await SaveAsync(path, identity with { State = api.State, Status = "initialized-not-logged-in" });
        Console.WriteLine("独立设备初始化成功；尚未登录、未创建房间、未启用远控。");
        return;
    }
    if (args[0] is "--host-preview" or "--host-control")
    {
        if (!identity.State.IsAuthenticated) throw new InvalidOperationException("请先完成登录。");
        if (!Path.IsPathFullyQualified(args[2]) || !File.Exists(args[2]) || (args[3] != "all" && !uint.TryParse(args[3], out _)))
            throw new ArgumentException("需要编码器绝对路径和 Wayland 输出编号。");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; deadline.Cancel(); };
        Console.CancelKeyPress += cancel;
        var restoreControlOff = false;
        try
        {
            var globals = await WaylandCapabilities.DiscoverAsync(deadline.Token);
            var outputs = args[3] == "all" ? globals.Where(g => g.Interface == "wl_output").Select(g => g.Name).Order().ToArray()
                : new[] { uint.Parse(args[3]) };
            if (outputs.Length is < 1 or > 5 || outputs.Any(output => !globals.Any(g => g.Interface == "wl_output" && g.Name == output)))
                throw new ArgumentException("所选显示器不可用。");
            var room = await api.CreateRoomAsync(0, deadline.Token);
            await using var signal = await UuSignalClient.ConnectAsync(room, deadline.Token);
            var info = await signal.GetRoomInfoAsync(deadline.Token);
            if (info.Packet?.Data?[0]?["you"]?["role"]?.GetValue<string>() != "publisher")
                throw new InvalidOperationException("服务器未确认发布者身份。");
            var availability = await api.GetHostAvailabilityAsync(deadline.Token);
            if (availability.Availability == "control_off")
            {
                restoreControlOff = true;
                await api.SetControllableAsync(true, deadline.Token);
                availability = await api.GetHostAvailabilityAsync(deadline.Token);
            }
            if (!availability.Controllable) throw new InvalidOperationException("设备仍不可被控。");
            Console.WriteLine(args[0] == "--host-control"
                ? "control-ready; controllable=true; keyboard-mouse-enabled; max-duration=300s"
                : "preview-ready; controllable=true; 720p; input-disabled; max-duration=300s");
            await HostPreview.RunAsync(signal, args[2], outputs, deadline.Token, enableInput: args[0] == "--host-control");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { Console.WriteLine("预览已停止，连接已关闭。"); }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (restoreControlOff)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await api.SetControllableAsync(false, cleanup.Token); Console.WriteLine("control-off-restored"); }
                catch { Console.WriteLine("WARNING: restore-control-off-failed; manual-check-required"); throw; }
            }
        }
        return;
    }
    if (args[0] == "--signal-check")
    {
        if (!identity.State.IsAuthenticated) throw new InvalidOperationException("请先完成登录。");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var room = await api.CreateRoomAsync(0, deadline.Token);
        await using var signal = await UuSignalClient.ConnectAsync(room, deadline.Token);
        var info = await signal.GetRoomInfoAsync(deadline.Token);
        var data = info.Packet?.Data as System.Text.Json.Nodes.JsonArray;
        var role = data is { Count: > 0 } ? data[0]?["you"]?["role"]?.GetValue<string>() : null;
        var publisher = role == "publisher";
        Console.WriteLine(JsonSerializer.Serialize(new { roomCreated = true, signalConnected = signal.IsConnected,
            publisherConfirmed = publisher, mediaStarted = false, inputEnabled = false }));
        if (!publisher) throw new InvalidOperationException("服务器未确认发布者身份，已关闭检查连接。");
        return;
    }
    if (identity.State.IsAuthenticated) { Console.WriteLine("本机已有登录令牌，未发送短信。令牌有效性需后续联网验证。"); return; }
    Console.WriteLine("输入手机号并回车后，将向 UU 请求发送一次登录验证码。手机号和验证码均不回显、不保存。");
    Console.Write("国家区号（直接回车为 86）：");
    var country = ReadHidden();
    if (country.Length == 0) country = "86";
    Console.Write("手机号：");
    var mobile = ReadHidden();
    await api.SendLoginCodeAsync(mobile, country);
    Console.WriteLine("验证码请求已被服务器接受。不会自动重发。");
    Console.Write("短信验证码：");
    var code = ReadHidden();
    await api.LoginAsync(mobile, code, country);
    await SaveAsync(path, identity with { State = api.State, Status = "logged-in-not-hosting" });
    Console.WriteLine("登录成功，令牌已保存为本机私有文件；未创建房间、未启动远控。");
}
catch (Exception e) when (e is IOException or HttpRequestException or InvalidOperationException or ArgumentException
    or JsonException or OperationCanceledException or UnauthorizedAccessException or FormatException or NotSupportedException or TimeoutException)
{
    // Never print upstream response bodies, argument values or serialized identities.
    Console.WriteLine("操作未完成：" + e.GetType().Name);
    Console.WriteLine("failure-site=" + string.Join(",", new System.Diagnostics.StackTrace(e).GetFrames().Take(4)
        .Select(f => f.GetMethod()?.DeclaringType?.Name + "." + f.GetMethod()?.Name)));
    Environment.ExitCode = 1;
}

static string ReadHidden()
{
    if (Console.IsInputRedirected) return Console.ReadLine()?.Trim() ?? throw new InvalidOperationException("输入已结束。");
    var value = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return value.ToString().Trim(); }
        if (key.Key == ConsoleKey.Escape) throw new InvalidOperationException("已取消输入。");
        if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; continue; }
        if (!char.IsControl(key.KeyChar))
        {
            if (value.Length >= 64) throw new InvalidOperationException("输入过长。");
            value.Append(key.KeyChar);
        }
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
static async Task SaveAsync(string path, LocalIdentity identity)
{
    var temp = path + ".new-" + Guid.NewGuid().ToString("N");
    try
    {
        await using (var file = new FileStream(temp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
        {
            await JsonSerializer.SerializeAsync(file, identity);
            await file.FlushAsync();
            file.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
    finally { if (File.Exists(temp)) File.Delete(temp); }
}

sealed record LocalIdentity(LoginState State, HostDeviceProfile Profile, string Status)
{
    public override string ToString() => $"LocalIdentity(Status={Status}, identity redacted)";
}
