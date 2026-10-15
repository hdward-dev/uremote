using URemote.Core;

internal static class HistoryChecks
{
    public static void Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "uremote-history-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "connections.json");
        try
        {
            var history = new LocalConnectionHistory(path);
            check(history.Load().Count == 0, "missing history starts empty");
            for (var i = 0; i < 7; i++) history.Remember(new(LocalDesktopProtocol.Rdp, $"desktop{i}.local", 3389, "user", "secret-never-save", "domain"));
            var records = new LocalConnectionHistory(path).Load();
            check(records.Count == 5 && records[0].Host == "desktop6.local" && records[^1].Host == "desktop2.local", "history persists only five newest connections across reloads");
            check(!File.ReadAllText(path).Contains("secret-never-save") && !File.ReadAllText(path).Contains("Password"), "history persists no password or password field");
            history.Remember(new(LocalDesktopProtocol.Rdp, "DESKTOP3.local", 3389, "USER", "different-secret", "DOMAIN"));
            records = history.Load();
            check(records.Count == 5 && records[0].Host == "DESKTOP3.local" && records.Count(r => r.Host.Equals("desktop3.local", StringComparison.OrdinalIgnoreCase)) == 1, "reconnection moves matching endpoint and account to front without duplication");
            history.Remember(new(LocalDesktopProtocol.Vnc, "desktop3.local", 5900, "unused", "vnc-secret", "unused"));
            records = history.Load();
            check(records[0].Protocol == LocalDesktopProtocol.Vnc && records[0].Username == "" && records[0].Domain == "", "VNC history omits unrelated RDP account fields");
            check(records[0].Options().Password == "" && records[1].Options("entered").Password == "entered", "quick connect restores endpoint and takes password only from current input");
            File.WriteAllText(path, "broken json");
            check(history.Load().Count == 0, "corrupt history does not prevent opening local connections");
            history.Remember(new(LocalDesktopProtocol.Vnc, "localhost", 5900));
            check(history.Load().Count == 1, "successful connection repairs corrupt history");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
