using System.Runtime.InteropServices;
using System.Text.Json;
using URemote.Core;
using URemote.Linux;
namespace URemote.Host;

/// <summary>Explicit user-triggered SMS login. Never sends SMS from app startup.</summary>
public sealed class DesktopHostLogin : IAsyncDisposable
{
    private readonly string path;
    private readonly FileStream identityLock;
    private SavedHostIdentity identity;
    private readonly UuMacHostApi api;
    private string? pendingMobile;
    private DateTimeOffset sentAt;
    private DesktopHostLogin(string path, FileStream identityLock, SavedHostIdentity identity)
    { this.path = path; this.identityLock = identityLock; this.identity = identity; api = new(identity.State); }
    public static DesktopHostLogin Open(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var gate = new FileStream(path + ".lock", new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite,
            Share = FileShare.None, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        try
        {
            SavedHostIdentity identity;
            if (File.Exists(path)) identity = DesktopHostSession.ReadIdentity(path);
            else
            {
                var client = Guid.NewGuid().ToString();
                var profile = new HostDeviceProfile(LinuxDeviceProfile.DeviceName, client, Guid.NewGuid().ToString(), Environment.OSVersion.VersionString,
                    RuntimeInformation.ProcessArchitecture.ToString(), GC.GetGCMemoryInfo().TotalAvailableMemoryBytes.ToString(),
                    "NixOS", "Linux desktop", "", "NixOS", "Linux", "", "", [], 96);
                identity = new(new(ClientId: client, Channel: "gwqd"), LinuxDeviceProfile.Refresh(profile), "uninitialized");
            }
            return new(path, gate, identity);
        }
        catch { gate.Dispose(); throw; }
    }
    public async Task SendCodeAsync(string mobile, CancellationToken ct)
    {
        if (identity.State.IsAuthenticated) throw new InvalidOperationException("Already logged in.");
        if (mobile.Length != 11 || !mobile.All(char.IsAsciiDigit)) throw new ArgumentException("Invalid mobile number.");
        if (DateTimeOffset.UtcNow - sentAt < TimeSpan.FromSeconds(60)) throw new InvalidOperationException("SMS cooldown.");
        if (api.State.DeviceId.Length == 0)
        {
            await api.InitializeAsync(identity.Profile, ct);
            identity = identity with { State = api.State, Status = "initialized-not-logged-in" };
            await SaveAsync(ct);
        }
        sentAt = DateTimeOffset.UtcNow; // An uncertain network response must not immediately resend SMS.
        await api.SendLoginCodeAsync(mobile, ct: ct);
        pendingMobile = mobile;
    }
    public async Task CompleteAsync(string code, CancellationToken ct)
    {
        if (pendingMobile is null) throw new InvalidOperationException("Send a code first.");
        await api.LoginAsync(pendingMobile, code, ct: ct);
        identity = identity with { State = api.State, Status = "logged-in-not-hosting" };
        await SaveAsync(ct); pendingMobile = null;
    }
    private async Task SaveAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var temp = path + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var file = new FileStream(temp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            { await JsonSerializer.SerializeAsync(file, identity, cancellationToken: ct); await file.FlushAsync(ct); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public ValueTask DisposeAsync() { pendingMobile = null; api.Dispose(); identityLock.Dispose(); return ValueTask.CompletedTask; }
}
