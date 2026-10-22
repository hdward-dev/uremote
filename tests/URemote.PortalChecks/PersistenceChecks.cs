using URemote.Linux;

internal static class PersistenceChecks
{
    internal static async Task RunAsync(MockPortal server, PortalBus bus, Action<bool, string> check)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var directory = Path.Combine(Path.GetTempPath(), "uremote-portal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PortalRestoreStore(true, directory);
            server.NextRestoreToken = "first-token";
            using (await PortalAuthorization.StartAsync(bus, MockPortal.Session, true, default, store)) { }
            check(server.PersistMode == 2 && server.PersistenceMethod == "SelectDevices" && server.ReceivedRestoreToken is null,
                "combined screen/input requests persistent permission once through RemoteDesktop");
            var file = Directory.GetFiles(directory).Single();
            check(File.GetUnixFileMode(file) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "restore capability stored with mode 600");
            server.NextRestoreToken = "rotated-token";
            using (await PortalAuthorization.StartAsync(bus, MockPortal.Session, true, default, new PortalRestoreStore(true, directory))) { }
            check(server.ReceivedRestoreToken == "first-token", "new application instance restores the previous grant");
            check(store.Take() == "rotated-token" && store.Take() is null, "replacement token is saved and consumed exactly once");
            store.Save("rejected-token"); server.Deny = true;
            try { using var denied = await PortalAuthorization.StartAsync(bus, MockPortal.Session, true, default, store); check(false,"denied restore"); }
            catch (InvalidOperationException) { check(store.Take() is null, "denied restore discards stale token without repeated consent prompts"); }
            store.Save("kept-token");
            check(store.Peek() == "kept-token" && store.Peek() == "kept-token", "peek reads a restore token without consuming it");
            server.Deny = false; server.Hold = true;
            try
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                try { using var pending = await PortalAuthorization.StartAsync(bus, MockPortal.Session, true, cancel.Token, store); check(false, "canceled start"); }
                catch (OperationCanceledException) { check(store.Peek() == "kept-token", "canceled start keeps the restore token for the next attempt"); }
            }
            finally { server.Hold = false; check(store.Take() == "kept-token", "kept token is still consumed only by an explicit take"); }
            server.Deny = false; server.ReceivedRestoreToken = null; server.NextRestoreToken = "fresh-token";
            using (await PortalAuthorization.StartAsync(bus, MockPortal.Session, true, default, store)) { }
            check(server.ReceivedRestoreToken is null && store.Take() == "fresh-token", "retry after invalidation uses fresh consent and saves replacement");
            store.Save("one-use"); server.NextRestoreToken = null;
            using (await PortalAuthorization.StartAsync(bus, MockPortal.Session, true, default, store)) { }
            check(store.Take() is null, "backend without replacement token leaves no stale capability");
            server.Version = 1; server.PersistMode = null; server.ReceivedRestoreToken = null;
            using (await PortalAuthorization.StartAsync(bus, MockPortal.Session, true, default, store)) { }
            check(server.PersistMode is null && server.ReceivedRestoreToken is null, "older portal uses ordinary consent without unsupported options");
            server.Version = 4; server.NextRestoreToken = "view-token"; server.PersistenceMethod = null;
            var view = new PortalRestoreStore(false, directory);
            using (await PortalAuthorization.StartAsync(bus, MockPortal.Session, false, default, view)) { }
            check(server.PersistenceMethod == "SelectSources" && view.Take() == "view-token" && store.Take() is null,
                "view-only persistence uses ScreenCast and never shares control tokens");
            store.Save("private-token"); file = Directory.GetFiles(directory).Single();
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.OtherRead);
            check(store.Take() is null, "insecure token files are not restored");
            store.Save("bad\nvalue"); check(store.Take() is null, "malformed restore token is not persisted");
        }
        finally
        {
            server.Deny = false; server.Hold = false; server.Version = 4; server.NextRestoreToken = null;
            Directory.Delete(directory, true);
        }
    }
}
