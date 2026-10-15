using URemote.Core;
using URemote.Linux;

internal static class DesktopActionChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        check(DesktopEnvironmentInfo.Detect(k => k == "XDG_CURRENT_DESKTOP" ? "ubuntu:GNOME" : null).Name == "GNOME", "GNOME sessions recognized with vendor prefix");
        check(DesktopActions.ParseShortcut("<Control><Alt>d")!.SequenceEqual(new[] { 59, 58, 2 }), "GNOME configured desktop shortcut mapped");
        check(DesktopActions.ParseShortcut("<Super>d")!.SequenceEqual(new[] { 55, 2 }), "custom Super desktop shortcut preserved");
        check(DesktopActions.ParseShortcut("<Control>F11")!.SequenceEqual(new[] { 59, 103 }), "function-key desktop shortcut mapped");
        check(new[] { "", "disabled", "<Hyper>d", "<Control", "<Alt><Alt>d", "<Super>unknown" }.All(x => DesktopActions.ParseShortcut(x) is null), "unset or unsupported shortcuts do not inject guessed keys");
        var keyboard = new Keyboard();
        await DesktopActions.SendChordAsync(keyboard, [59, 58, 2], default);
        check(keyboard.Keys.Select(k => (k.Action, k.MacKey)).SequenceEqual(new[] {
            (KeyAction.Press,59), (KeyAction.Press,58), (KeyAction.Press,2),
            (KeyAction.Release,2), (KeyAction.Release,58), (KeyAction.Release,59) }), "desktop chord releases keys in reverse order");
        var canceled = new Keyboard { CancelOnKey = 2 };
        try { await DesktopActions.SendChordAsync(canceled, [59, 58, 2], default); } catch (OperationCanceledException) { }
        check(canceled.Keys.TakeLast(3).All(k => k.Action == KeyAction.Release), "interrupted chord releases all attempted keys");
    }
    private sealed class Keyboard : IDesktopKeyboard
    {
        public List<HostKeyMessage> Keys { get; } = [];
        public int CancelOnKey = -1;
        public Task ApplyAsync(HostKeyMessage message, CancellationToken ct = default)
        { Keys.Add(message); if (message.Action == KeyAction.Press && message.MacKey == CancelOnKey) throw new OperationCanceledException(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    internal static async Task LiveKdeAsync(Action<bool,string> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var ct = timeout.Token;
        await using var bus = await PortalBus.ConnectAsync(ct);
        async Task<bool> Showing()
        {
            using var iface = PortalValue.String("org.kde.KWin"); using var name = PortalValue.String("showingDesktop"); using var args = PortalValue.Tuple(iface,name);
            using var reply = await bus.CallAsync("org.freedesktop.DBus.Properties","Get",args,ct,"/KWin","org.kde.KWin");
            using var box = reply.Child(0); using var value = box.Unbox(); return value.Boolean();
        }
        async Task<bool> WindowViewActive()
        {
            using var iface = PortalValue.String("org.kde.kwin.Effects"); using var name = PortalValue.String("activeEffects"); using var args = PortalValue.Tuple(iface,name);
            using var reply = await bus.CallAsync("org.freedesktop.DBus.Properties","Get",args,ct,"/Effects","org.kde.KWin");
            using var box = reply.Child(0); using var value = box.Unbox();
            for (var i=0;i<value.Count;i++) { using var effect=value.Child(i); if(effect.Text()=="windowview") return true; }
            return false;
        }
        var original = await Showing();
        try
        {
            check(await DesktopActions.ApplyAsync(HostDesktopAction.ShowDesktop,new Keyboard(),Console.WriteLine,ct),"KDE desktop action accepted");
            await Task.Delay(500,ct);
            check(await Showing() != original,"KDE actual showingDesktop property changed");
        }
        finally
        {
            using var state = PortalValue.Bool(original); using var args = PortalValue.Tuple(state);
            using var reply = await bus.CallAsync("org.kde.KWin","showDesktop",args,CancellationToken.None,"/KWin","org.kde.KWin");
        }
        var wasActive = await WindowViewActive();
        try
        {
            check(await DesktopActions.ApplyAsync(HostDesktopAction.ShowWindows,new Keyboard(),Console.WriteLine,ct),"KDE all-windows action accepted");
            await Task.Delay(700,ct);
            check(await WindowViewActive() != wasActive,"KDE actual windowview effect changed");
        }
        finally
        {
            if (await WindowViewActive()!=wasActive)
                await DesktopActions.ApplyAsync(HostDesktopAction.ShowWindows,new Keyboard(),Console.WriteLine,ct);
        }
    }
}
