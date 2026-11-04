using URemote.Linux;

internal static class LoginSessionChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var source = new Fixture { PidSession = true };
        check(await Find(source) == Fixture.Process && !source.UsedDisplay,
            "a process in its own graphical login session keeps that session");
        source = new Fixture();
        check(await Find(source) == Fixture.Display && source.UsedDisplay,
            "a systemd user service without a PID session watches its user's graphical Display");
        source = new Fixture { PidSession = true, PidType = "tty" };
        check(await Find(source) == Fixture.Display && source.UsedDisplay,
            "an SSH or terminal process cannot replace the graphical session used for lock monitoring");
        foreach (var denied in new[]
        {
            new Fixture { DisplayUid = 1001 }, new Fixture { DisplayRemote = true },
            new Fixture { DisplayClass = "greeter" }, new Fixture { DisplayType = "tty" },
            new Fixture { DisplayPath = "/" }
        })
        {
            try { await Find(denied); check(false, "unrelated login session rejected"); }
            catch (IOException) { check(true, "another user, greeter, remote, or missing graphical session is rejected"); }
        }
        source = new Fixture { Cancel = true };
        try { await Find(source); check(false, "cancelled session lookup"); }
        catch (OperationCanceledException) { check(!source.UsedDisplay, "cancelled lookup never continues to another session"); }
        return;

        static Task<string> Find(Fixture fixture) => SessionLock.LoginSessionPathAsync(fixture.CallAsync, 1000, CancellationToken.None);
    }

    private sealed class Fixture
    {
        internal const string Process = "/org/freedesktop/login1/session/process", Display = "/org/freedesktop/login1/session/display";
        private const string User = "/org/freedesktop/login1/user/_1000";
        internal bool PidSession, Cancel, UsedDisplay, DisplayRemote;
        internal uint DisplayUid = 1000;
        internal string PidType = "wayland", DisplayType = "wayland", DisplayClass = "user", DisplayPath = Display;

        internal Task<PortalValue> CallAsync(string iface, string method, PortalValue args, string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Cancel) throw new OperationCanceledException();
            if (iface == "org.freedesktop.login1.Manager" && method == "GetSessionByPID")
            {
                args.Require("(u)");
                if (!PidSession) throw new IOException("no PID session");
                using var value = PortalValue.Path(Process);
                return Task.FromResult(PortalValue.Tuple(value));
            }
            if (iface == "org.freedesktop.login1.Manager" && method == "GetUser")
            {
                args.Require("(u)");
                using var uid = args.Child(0);
                if (uid.UInt() != 1000) throw new Exception("wrong user");
                UsedDisplay = true;
                using var value = PortalValue.Path(User);
                return Task.FromResult(PortalValue.Tuple(value));
            }
            if (iface == "org.freedesktop.DBus.Properties" && method == "Get" && path == User)
            {
                args.Require("(ss)");
                using var property = args.Child(1);
                if (property.Text() != "Display") throw new Exception("wrong user property");
                using var id = PortalValue.String("3");
                using var value = PortalValue.Path(DisplayPath);
                using var display = PortalValue.Tuple(id, value);
                using var boxed = PortalValue.Variant(display);
                return Task.FromResult(PortalValue.Tuple(boxed));
            }
            if (iface == "org.freedesktop.DBus.Properties" && method == "GetAll" && path is Process or Display)
            {
                args.Require("(s)");
                using var uid = PortalValue.UInt(path == Process ? 1000u : DisplayUid);
                using var userPath = PortalValue.Path(User);
                using var user = PortalValue.Tuple(uid, userPath);
                using var type = PortalValue.String(path == Process ? PidType : DisplayType);
                using var kind = PortalValue.String(path == Process ? "user" : DisplayClass);
                using var remote = PortalValue.Bool(path != Process && DisplayRemote);
                using var properties = PortalValue.Dict(("User", user), ("Type", type), ("Class", kind), ("Remote", remote));
                return Task.FromResult(PortalValue.Tuple(properties));
            }
            throw new Exception("unexpected login1 call");
        }
    }
}
