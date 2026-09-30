using System.Text.Json;
using URemote.Core;
using URemote.Linux;

namespace URemote.Host;

public static class HostIdentityMigration
{
    // Explicit operation after a separate Windows login. Never run from app startup.
    // Caller stops the old host only after preparing and validating the pending identity.
    public static async Task<bool> AdoptWindowsAsync(string identityPath, string pendingPath,
        string assistancePath, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || !HostWirePlatform.Windows)
            throw new InvalidOperationException("Explicit Windows platform required.");
        if (Path.GetFullPath(identityPath) == Path.GetFullPath(pendingPath))
            throw new ArgumentException("Pending login must use a separate file.");
        await using var gate = new FileStream(identityPath + ".lock", new FileStreamOptions
        { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        await using var pendingGate = new FileStream(pendingPath + ".lock", new FileStreamOptions
        { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        var identity = DesktopHostSession.ReadIdentity(identityPath);
        var pending = DesktopHostSession.ReadIdentity(pendingPath);
        if (!identity.State.IsAuthenticated || !pending.State.IsAuthenticated || pending.Platform != 1
            || pending.State.UserId != identity.State.UserId
            || pending.State.ClientId != identity.State.ClientId
            || pending.Profile.SystemId != identity.Profile.SystemId)
            throw new InvalidOperationException("A fresh Windows login for this device and account is required.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var api = new UuMacHostApi(pending.State);
        var self = (await api.GetDevicesAsync(timeout.Token)).Single(d => d.IsCurrent);
        if (self.Platform != 1) throw new InvalidOperationException("Server did not confirm Windows identity.");
        var next = pending.State;
        var profile = LinuxDeviceProfile.Refresh(pending.Profile);
        AssistanceCodeSettings? secret = null;
        if (next.DeviceId != identity.State.DeviceId && File.Exists(assistancePath))
        {
            secret = AssistanceCodeSettings.Load(assistancePath, identity.State.DeviceId);
            var stored = JsonSerializer.Deserialize<AssistanceCodeSettings>(File.ReadAllText(assistancePath));
            if (stored?.DeviceId != identity.State.DeviceId) throw new IOException("Assistance settings belong to another device.");
        }
        Backup(identityPath);
        if (secret is not null) Backup(assistancePath);
        var updated = identity with { State = next, Profile = profile, Platform = 1 };
        try
        {
            if (secret is not null) (secret with { DeviceId = next.DeviceId }).Save(assistancePath);
            Save(identityPath, updated);
        }
        catch
        {
            if (secret is not null) secret.Save(assistancePath);
            throw;
        }
        return true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void Backup(string path)
    {
        var backup = path + ".before-windows";
        if (File.Exists(backup)) return;
        using var file = new FileStream(backup, new FileStreamOptions
        { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        using var source = File.OpenRead(path);
        source.CopyTo(file); file.Flush(true);
    }
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void Save(string path, SavedHostIdentity identity)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, new FileStreamOptions
            { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            { JsonSerializer.Serialize(file, identity); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
