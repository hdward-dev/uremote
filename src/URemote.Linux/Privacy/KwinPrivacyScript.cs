using System.Text.Json;

namespace URemote.Linux;

internal static class KwinPrivacyScript
{
    internal static async Task<bool> RunAsync(string expression, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var token = timeout.Token;
        await using var bus = await PortalBus.ConnectAsync(token);
        using var service = PortalValue.String("org.kde.KWin"); using var ownerArgs = PortalValue.Tuple(service);
        using var ownerReply = await bus.CallAsync("org.freedesktop.DBus", "GetNameOwner", ownerArgs, token, "/org/freedesktop/DBus", "org.freedesktop.DBus");
        using var owner = ownerReply.Child(0);
        var name = "uremote_privacy_" + Guid.NewGuid().ToString("N");
        var path = "/org/asterdock/uremote/privacy/" + name;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var callback = await bus.ExportStringAsync(path, owner.Text(), text => ready.TrySetResult(text == "ready"));
        var directory = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var file = Path.Combine(directory, "main.js");
        var loaded = false;
        try
        {
            var script = "var ok = false; try { ok = (function() { " + expression + " })(); } catch (e) {}\n" +
                "callDBus(" + JsonSerializer.Serialize(bus.UniqueName) + "," + JsonSerializer.Serialize(path) + ",\"" + PortalBus.Export.Interface + "\",\"Report\", ok ? \"ready\" : \"unsupported\");";
            await File.WriteAllTextAsync(file, script, token);
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var filename = PortalValue.String(file); using var plugin = PortalValue.String(name);
            using var args = PortalValue.Tuple(filename, plugin);
            using var reply = await bus.CallAsync("org.kde.kwin.Scripting", "loadScript", args, token, "/Scripting", "org.kde.KWin");
            using var id = reply.Child(0);
            if (id.Int() < 0) return false;
            loaded = true;
            using var empty = PortalValue.Tuple();
            using var run = await bus.CallAsync("org.kde.kwin.Script", "run", empty, token, "/Scripting/Script" + id.Int(), "org.kde.KWin");
            return await ready.Task.WaitAsync(token);
        }
        finally
        {
            if (loaded)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    using var plugin = PortalValue.String(name); using var args = PortalValue.Tuple(plugin);
                    using var reply = await bus.CallAsync("org.kde.kwin.Scripting", "unloadScript", args, cleanup.Token, "/Scripting", "org.kde.KWin", 1000);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException) { }
            }
            Directory.Delete(directory, true);
        }
    }
}
