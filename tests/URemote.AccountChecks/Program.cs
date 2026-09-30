using System.Text.Json;
using URemote.Core;
using URemote.Host;

Environment.SetEnvironmentVariable("UREMOTE_HOST_PLATFORM", "windows");
if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
var directory = Path.Combine(Path.GetTempPath(), "uremote-account-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "identity.json");
var profile = new HostDeviceProfile("fixture", "client", "system", "", "", "", "", "", "", "", "", "", "", [], 96);
var authenticated = new SavedHostIdentity(new(Token: "fixture-token", UserId: "fixture-user", ClientId: "client", DeviceId: "device", Channel: "gwqd"), profile, "logged-in", 1, "138****1234");
void Save(SavedHostIdentity value)
{
    if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
    using var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write,
        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
    JsonSerializer.Serialize(stream, value);
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }
try
{
    var wallpaperRoot = Path.Combine(directory, "wallpapers", "fixture", "contents");
    Directory.CreateDirectory(Path.Combine(wallpaperRoot, "images"));
    Directory.CreateDirectory(Path.Combine(wallpaperRoot, "images_dark"));
    var lightImage = Path.Combine(wallpaperRoot, "images", "1920x1080.png");
    var darkImage = Path.Combine(wallpaperRoot, "images_dark", "1920x1080.png");
    File.WriteAllBytes(lightImage, []); File.WriteAllBytes(darkImage, []);
    Check(URemote.Linux.KdeWallpaper.ResolveImage("fixture", true, [directory]) == darkImage, "KDE wallpaper packages select the dark variant");
    Check(URemote.Linux.KdeWallpaper.ResolveImage("fixture#light", true, [directory]) == lightImage, "KDE explicit wallpaper mode overrides the theme");
    Check(URemote.Linux.KdeWallpaper.ResolveImage(new Uri(lightImage).AbsoluteUri, false, [directory]) == lightImage, "KDE file URI wallpapers resolve to local images");
    var themeDefaults = Path.Combine(directory, "plasma/look-and-feel/test.desktop/contents");
    Directory.CreateDirectory(themeDefaults);
    File.WriteAllText(Path.Combine(themeDefaults, "defaults"), "[Wallpaper]\nImage=fixture\n");
    File.WriteAllText(Path.Combine(directory, "kdeglobals"), "[KDE]\nLookAndFeelPackage=test.desktop\n[Colors:Window]\nBackgroundNormal=20,20,20\n");
    File.WriteAllText(Path.Combine(directory, "plasma-org.kde.plasma.desktop-appletsrc"), "[Containments][1]\nplugin=org.kde.panel\nwallpaperplugin=org.kde.image\n[Containments][2]\nplugin=org.kde.plasma.folder\nwallpaperplugin=org.kde.image\nlastScreen=0\n");
    Check(URemote.Linux.KdeWallpaper.Resolve(directory, [directory]) == darkImage, "KDE desktop falls back to the theme default wallpaper while excluding panels");
    Save(authenticated);
    Check(DesktopHostLogin.MaskAccount("13800001234") == "138****1234", "account display stores only a masked mobile number");
    Check(DesktopHostSession.ReadIdentity(path).LoginAccount == "138****1234", "account display survives identity reload");
    await DesktopHostLogin.LogoutAsync(path, CancellationToken.None);
    var loggedOut = DesktopHostSession.ReadIdentity(path);
    Check(!loggedOut.State.IsAuthenticated && loggedOut.State.Token == "" && loggedOut.State.UserId == "", "logout removes account credentials");
    Check(loggedOut.LoginAccount == "", "logout removes the account display label");
    Check(loggedOut.State.DeviceId == "device" && loggedOut.State.ClientId == "client" && loggedOut.Profile.SystemId == "system", "same-platform logout preserves device identity");
    Check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "logged-out identity remains private");
    Save(authenticated with { Platform = 4 });
    await DesktopHostLogin.LogoutAsync(path, CancellationToken.None);
    loggedOut = DesktopHostSession.ReadIdentity(path);
    Check(loggedOut.State.DeviceId == "" && loggedOut.State.Token == "" && loggedOut.State.UserId == "" && loggedOut.Platform == 0,
        "old-platform logout requires fresh initialization before login");
    Check(loggedOut.State.ClientId == "client" && loggedOut.Profile.SystemId == "system", "platform change preserves stable hardware identifiers");
    Save(authenticated);
    await using (var owner = DesktopHostLogin.Open(path))
    {
        Check(owner.CodeResendSeconds == 0, "SMS resend starts available without an earlier request");
        var sentAt = typeof(DesktopHostLogin).GetField("sentAt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        sentAt.SetValue(owner, DateTimeOffset.UtcNow);
        Check(owner.CodeResendSeconds is >= 59 and <= 60, "SMS request timestamp supplies the resend countdown");
        sentAt.SetValue(owner, DateTimeOffset.UtcNow.AddSeconds(-61));
        Check(owner.CodeResendSeconds == 0, "SMS resend becomes available after the cooldown");
        try { await DesktopHostLogin.LogoutAsync(path, CancellationToken.None); throw new Exception("lock was ignored"); }
        catch (IOException) { Check(DesktopHostSession.ReadIdentity(path).State.IsAuthenticated, "another identity owner prevents credential modification"); }
    }
    using var canceled = new CancellationTokenSource(); canceled.Cancel();
    try { await DesktopHostLogin.LogoutAsync(path, canceled.Token); throw new Exception("cancellation was ignored"); }
    catch (OperationCanceledException) { Check(DesktopHostSession.ReadIdentity(path).State.IsAuthenticated, "canceled logout preserves the original identity atomically"); }
    Console.WriteLine("Account checks passed; no real account or SMS was used.");
}
finally { Directory.Delete(directory, recursive: true); }
