using System.Text.Json;
using URemote.Core;
using URemote.Linux;

namespace URemote.Host;

public sealed record SavedHostIdentity(LoginState State, HostDeviceProfile Profile, string Status, int Platform = 0, string LoginAccount = "")
{
    public override string ToString() => "SavedHostIdentity(redacted)";
}

public static class DesktopHostSession
{
    public static SavedHostIdentity ReadIdentity(string path)
    {
        if (OperatingSystem.IsWindows() || (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())) throw new PlatformNotSupportedException();
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidOperationException("身份文件权限必须为 600。");
        return JsonSerializer.Deserialize<SavedHostIdentity>(File.ReadAllText(path)) ?? throw new InvalidDataException();
    }

    public static async Task RunAsync(string identityPath, string ffmpeg, IReadOnlyList<uint> outputs,
        bool enableInput, TimeSpan duration, Action<string> report, CancellationToken ct, bool enableAudio = false, bool enableClipboard = false, IReadOnlyList<int>? outputIndices = null, Action<HostControlConnection?>? connectionChanged = null, HostAdmission? admission = null)
    {
        if (OperatingSystem.IsWindows() || (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())) throw new PlatformNotSupportedException();
        if (outputs.Count > 5 || outputs.Distinct().Count() != outputs.Count || (duration != Timeout.InfiniteTimeSpan && (duration <= TimeSpan.Zero
            || duration > TimeSpan.FromHours(1))))
            throw new ArgumentException("无效的被控设置。");
        await using var identityLock = new FileStream(identityPath + ".lock", new FileStreamOptions
        { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        var identity = ReadIdentity(identityPath);
        if (!identity.State.IsAuthenticated) throw new InvalidOperationException("请先登录。");
        if (HostWirePlatform.Windows && identity.Platform != 1)
            throw new InvalidOperationException("当前保存的是旧平台身份，请先迁移并重新登录 Windows 设备身份；旧令牌不会自动转换。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var ownedAdmission = admission is null ? new HostAdmission() : null;
        admission ??= ownedAdmission!;
        using var api = new UuMacHostApi(identity.State);
        api.AdmissionAllowed = () => admission.Enabled;
        using var wallpaperStop=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var wallpaper=HostWallpaper.RunAsync(identity.State,ffmpeg,identityPath,report,wallpaperStop.Token);
        var restore = false;
        try
        {
            report("starting");
            var retrySeconds = 2;
            var profileUpdated = false;
            while (!deadline.IsCancellationRequested)
            {
            try
            {
            if (!profileUpdated)
            {
                var profile = LinuxDeviceProfile.Refresh(identity.Profile);
                await api.RefreshDeviceProfileAsync(profile, deadline.Token);
                await api.SetLocalDeviceNameAsync(profile.Name, deadline.Token);
                report("device-profile-refreshed");
                profileUpdated = true;
            }
            var room = await api.CreateRoomAsync(0, deadline.Token);
            await using var signal = await UuSignalClient.ConnectAsync(room, deadline.Token);
            signal.Diagnostic += report;
            var info = await signal.GetRoomInfoAsync(deadline.Token);
            if (info.Packet?.Data?[0]?["you"]?["role"]?.GetValue<string>() != "publisher")
                throw new InvalidOperationException("被控身份未获确认。");
            await signal.RefreshReconnectKeyAsync(deadline.Token);
            restore = true;
            retrySeconds = 2;
            while (!deadline.IsCancellationRequested)
            {
                var permission = admission.Snapshot();
                using var controlStop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, permission.Changed);
                await api.SetControllableAsync(false, deadline.Token);
                try
                {
                    if (!permission.Enabled)
                    {
                        report("online-control-disabled");
                        await WaitWhileDisabledAsync(signal, api, controlStop.Token, report);
                        continue;
                    }
                    if (duration != Timeout.InfiniteTimeSpan) controlStop.CancelAfter(duration);
                    if (!Path.IsPathFullyQualified(ffmpeg) || !File.Exists(ffmpeg))
                        throw new NotSupportedException("请检查编码器路径。");
                    report("desktop-authorizing");
                    IAsyncDisposable authorization;
                    try { authorization = await DesktopBackend.OpenAsync(enableInput, controlStop.Token); }
                    catch (Exception e) when (e is InvalidOperationException or FormatException or UnauthorizedAccessException)
                    { throw new NotSupportedException("桌面共享未获授权。", e); }
                    await using var desktop = authorization;
                    report("desktop-ready");
                    await using var terminals = new HostTerminalManager();
                    var globals = await DesktopBackend.DiscoverAsync(controlStop.Token);
                    var available = globals.Where(g => g.Interface == "wl_output").Select(g => g.Name).Order().Take(5).ToArray();
                    var selected = outputIndices is null
                        ? (outputs.Count == 0 ? available : outputs.Where(available.Contains).ToArray())
                        : available.Where((_, i) => outputIndices.Contains(i)).ToArray();
                    if (selected.Length == 0)
                    {
                        report("waiting-for-display");
                        await Task.Delay(TimeSpan.FromSeconds(3), controlStop.Token);
                        continue;
                    }
                    await api.SetControllableAsync(true, controlStop.Token);
                    report("ready");
                    using var previewStop = CancellationTokenSource.CreateLinkedTokenSource(controlStop.Token);
                    var watch = WatchDisplaysAsync(available, previewStop, report);
                    try { await HostPreview.RunAsync(signal, ffmpeg, selected, previewStop.Token, enableInput, report, enableAudio, enableClipboard, terminals, api.AnswerAssistanceAsync, () => HostAssistance.PermissionToken(identity.State.DeviceId), () => { if (HostAssistance.RotateAfterConnection(identity.State.DeviceId)) report("assistance-code-rotated"); }, connectionChanged, api.AnswerAssistanceModeAsync); }
                    catch (OperationCanceledException) when (previewStop.IsCancellationRequested) { }
                    finally { previewStop.Cancel(); await watch; }
                    if (signal.Events.Completion.IsCompleted) { await signal.Events.Completion; throw new IOException("Publisher signal closed."); }
                }
                catch (OperationCanceledException) when (controlStop.IsCancellationRequested) { }
                catch (Exception e) when (e is NotSupportedException)
                {
                    admission.SetEnabled(false);
                    report("control-unavailable;type=" + e.GetType().Name);
                    if (OperatingSystem.IsMacOS()) report("macos-control-unavailable");
                }
                finally
                {
                    connectionChanged?.Invoke(null);
                    if (!deadline.IsCancellationRequested)
                    {
                        await api.SetControllableAsync(false, deadline.Token);
                        await signal.ClearControlRoomAsync(deadline.Token);
                    }
                }
                if (controlStop.IsCancellationRequested && !permission.Changed.IsCancellationRequested && !deadline.IsCancellationRequested)
                { admission.SetEnabled(false); report("control-duration-expired"); }
            }
            }
            catch (Exception e) when (!deadline.IsCancellationRequested && e is DesktopSessionClosedException or IOException or HttpRequestException or TimeoutException or System.Net.WebSockets.WebSocketException or InvalidOperationException or FormatException or NotSupportedException or OperationCanceledException or System.Net.Sockets.SocketException or ArgumentException)
            { report("host-retry;type=" + e.GetType().Name); }
            if (!deadline.IsCancellationRequested) { report("publisher-reconnecting"); await Task.Delay(TimeSpan.FromSeconds(retrySeconds), deadline.Token); retrySeconds = Math.Min(30, retrySeconds * 2); }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        finally
        {
            connectionChanged?.Invoke(null);
            report("stopping");
            wallpaperStop.Cancel();
            try {await wallpaper;}catch(OperationCanceledException){}
            if (restore)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await api.SetControllableAsync(false, cleanup.Token); }
                catch { report("restore-failed"); throw; }
            }
            report("stopped");
        }
    }
    private static async Task WaitWhileDisabledAsync(UuSignalClient signal, UuMacHostApi api, CancellationToken ct, Action<string> report)
    {
        await foreach (var frame in signal.Events.ReadAllAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            if (HostAssistance.ControlModeRequest(frame.Packet?.Data) is { } control)
                await api.AnswerAssistanceModeAsync(control, ct);
            else if (HostAssistance.Challenge(frame.Packet?.Data) is { } challenge)
                await api.AnswerAssistanceAsync(challenge.ControlId, challenge.Salt, ct);
            else if (frame.Packet?.Data?[0]?.GetValue<string>() == "be-controlled")
            {
                await signal.ClearControlRoomAsync(ct);
                report("control-disabled-request-rejected");
            }
        }
        throw new IOException("Publisher signal closed.");
    }

    private static async Task WatchDisplaysAsync(uint[] initial, CancellationTokenSource stop, Action<string> report)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stop.Token);
                var globals = await DesktopBackend.DiscoverAsync(stop.Token);
                var current = globals.Where(g => g.Interface == "wl_output").Select(g => g.Name).Order().Take(5);
                if (!initial.SequenceEqual(current))
                {
                    report("display-topology-changed");
                    stop.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (DesktopSessionClosedException) { report("desktop-session-closed"); stop.Cancel(); throw; }
        catch (Exception e) { report("display-watch-retry;type=" + e.GetType().Name); stop.Cancel(); }
    }

}
