using System.Text.Json;
using URemote.Core;
using URemote.Linux;

namespace URemote.Host;

public sealed record SavedHostIdentity(LoginState State, HostDeviceProfile Profile, string Status)
{
    public override string ToString() => "SavedHostIdentity(redacted)";
}

public static class DesktopHostSession
{
    public static SavedHostIdentity ReadIdentity(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidOperationException("身份文件权限必须为 600。");
        return JsonSerializer.Deserialize<SavedHostIdentity>(File.ReadAllText(path)) ?? throw new InvalidDataException();
    }

    public static async Task RunAsync(string identityPath, string ffmpeg, IReadOnlyList<uint> outputs,
        bool enableInput, TimeSpan duration, Action<string> report, CancellationToken ct, bool enableAudio = false, bool enableClipboard = false, IReadOnlyList<int>? outputIndices = null, Action<HostControlConnection?>? connectionChanged = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (outputs.Count > 5 || outputs.Distinct().Count() != outputs.Count || (duration != Timeout.InfiniteTimeSpan && (duration <= TimeSpan.Zero
            || duration > TimeSpan.FromHours(1))) || !Path.IsPathFullyQualified(ffmpeg) || !File.Exists(ffmpeg))
            throw new ArgumentException("无效的被控设置。");
        await using var identityLock = new FileStream(identityPath + ".lock", new FileStreamOptions
        { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        var identity = ReadIdentity(identityPath);
        if (!identity.State.IsAuthenticated) throw new InvalidOperationException("请先登录。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (duration != Timeout.InfiniteTimeSpan) deadline.CancelAfter(duration);
        using var api = new UuMacHostApi(identity.State);
        await using var terminals = new HostTerminalManager();
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
            var globals = await WaylandCapabilities.DiscoverAsync(deadline.Token);
            var available = globals.Where(g => g.Interface == "wl_output").Select(g => g.Name).Order().Take(5).ToArray();
            var selected = outputIndices is null
                ? (outputs.Count == 0 ? available : outputs.Where(available.Contains).ToArray())
                : available.Where((_, i) => outputIndices.Contains(i)).ToArray();
            if (selected.Length == 0)
            {
                report("waiting-for-display");
                await Task.Delay(TimeSpan.FromSeconds(3), deadline.Token);
                continue;
            }
            var room = await api.CreateRoomAsync(0, deadline.Token);
            await using var signal = await UuSignalClient.ConnectAsync(room, deadline.Token);
            var info = await signal.GetRoomInfoAsync(deadline.Token);
            if (info.Packet?.Data?[0]?["you"]?["role"]?.GetValue<string>() != "publisher")
                throw new InvalidOperationException("被控身份未获确认。");
            var availability = await api.GetHostAvailabilityAsync(deadline.Token);
            restore = true;
            if (availability.Availability == "control_off")
            {
                restore = true;
                await api.SetControllableAsync(true, deadline.Token);
                availability = await api.GetHostAvailabilityAsync(deadline.Token);
            }
            if (!availability.Controllable) throw new InvalidOperationException("服务端未允许被控。");
            report("ready");
            retrySeconds = 2;
            using var previewStop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var watch = WatchDisplaysAsync(available, previewStop, report);
            try { await HostPreview.RunAsync(signal, ffmpeg, selected, previewStop.Token, enableInput, report, enableAudio, enableClipboard, terminals, api.AnswerAssistanceAsync, () => HostAssistance.PermissionToken(identity.State.DeviceId), () => { if (HostAssistance.RotateAfterConnection(identity.State.DeviceId)) report("assistance-code-rotated"); }, connectionChanged); }
            finally { previewStop.Cancel(); await watch; }
            }
            catch (Exception e) when (!deadline.IsCancellationRequested && e is IOException or HttpRequestException or TimeoutException or System.Net.WebSockets.WebSocketException or InvalidOperationException or FormatException or NotSupportedException or OperationCanceledException or System.Net.Sockets.SocketException or ArgumentException)
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
            await terminals.DisposeAsync();
            if (restore)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await api.SetControllableAsync(false, cleanup.Token); }
                catch { report("restore-failed"); throw; }
            }
            report("stopped");
        }
    }
    private static async Task WatchDisplaysAsync(uint[] initial, CancellationTokenSource stop, Action<string> report)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), stop.Token);
                var globals = await WaylandCapabilities.DiscoverAsync(stop.Token);
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
        catch (Exception e) { report("display-watch-retry;type=" + e.GetType().Name); stop.Cancel(); }
    }

}
