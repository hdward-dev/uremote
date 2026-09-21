using System.Text;
using URemote.Core;

namespace URemote.Linux;

public sealed class WaylandVirtualKeyboard : IAsyncDisposable
{
    private readonly WaylandConnection connection;
    private readonly uint keyboard;
    private readonly HashSet<uint> held = [];
    private readonly SemaphoreSlim gate = new(1);
    private bool disposed;
    private uint locked;
    private WaylandVirtualKeyboard(WaylandConnection connection, uint keyboard)
    { this.connection = connection; this.keyboard = keyboard; }

    // macOS virtual key codes -> Linux evdev codes, plus an explicit US keymap.
    private static readonly (int Mac, uint Linux, string Lower, string Upper)[] Keys =
    [
        (0,30,"a","A"),(1,31,"s","S"),(2,32,"d","D"),(3,33,"f","F"),(4,35,"h","H"),(5,34,"g","G"),
        (6,44,"z","Z"),(7,45,"x","X"),(8,46,"c","C"),(9,47,"v","V"),(11,48,"b","B"),
        (12,16,"q","Q"),(13,17,"w","W"),(14,18,"e","E"),(15,19,"r","R"),(16,21,"y","Y"),(17,20,"t","T"),
        (18,2,"1","exclam"),(19,3,"2","at"),(20,4,"3","numbersign"),(21,5,"4","dollar"),
        (22,7,"6","asciicircum"),(23,6,"5","percent"),(24,13,"equal","plus"),(25,10,"9","parenleft"),
        (26,8,"7","ampersand"),(27,12,"minus","underscore"),(28,9,"8","asterisk"),(29,11,"0","parenright"),
        (30,27,"bracketright","braceright"),(31,24,"o","O"),(32,22,"u","U"),(33,26,"bracketleft","braceleft"),
        (34,23,"i","I"),(35,25,"p","P"),(36,28,"Return","Return"),(37,38,"l","L"),(38,36,"j","J"),
        (39,40,"apostrophe","quotedbl"),(40,37,"k","K"),(41,39,"semicolon","colon"),(42,43,"backslash","bar"),
        (43,51,"comma","less"),(44,53,"slash","question"),(45,49,"n","N"),(46,50,"m","M"),(47,52,"period","greater"),
        (48,15,"Tab","ISO_Left_Tab"),(49,57,"space","space"),(50,41,"grave","asciitilde"),(51,14,"BackSpace","BackSpace"),
        (53,1,"Escape","Escape"),(54,126,"Super_R","Super_R"),(55,125,"Super_L","Super_L"),
        (56,42,"Shift_L","Shift_L"),(57,58,"Caps_Lock","Caps_Lock"),(58,56,"Alt_L","Alt_L"),
        (59,29,"Control_L","Control_L"),(60,54,"Shift_R","Shift_R"),(61,100,"Alt_R","Alt_R"),(62,97,"Control_R","Control_R"),
        (96,63,"F5","F5"),(97,64,"F6","F6"),(98,65,"F7","F7"),(99,61,"F3","F3"),(100,66,"F8","F8"),
        (101,67,"F9","F9"),(103,87,"F11","F11"),(109,68,"F10","F10"),(111,88,"F12","F12"),
        (114,110,"Insert","Insert"),(115,102,"Home","Home"),(116,104,"Prior","Prior"),(117,111,"Delete","Delete"),
        (118,62,"F4","F4"),(119,107,"End","End"),(120,60,"F2","F2"),(121,109,"Next","Next"),(122,59,"F1","F1"),
        (123,105,"Left","Left"),(124,106,"Right","Right"),(125,108,"Down","Down"),(126,103,"Up","Up")
    ];
    public static uint? LinuxKey(int mac) => Keys.Where(k => k.Mac == mac).Select(k => (uint?)k.Linux).FirstOrDefault();
    public static string Keymap()
    {
        var b = new StringBuilder("xkb_keymap { xkb_keycodes \"uremote\" { minimum=8; maximum=255;");
        foreach (var key in Keys) b.Append($"<K{key.Linux:D3}>={key.Linux + 8};");
        b.Append("}; xkb_types \"uremote\" { type \"ONE_LEVEL\" { modifiers=None; map[None]=Level1; }; type \"TWO_LEVEL\" { modifiers=Shift; map[None]=Level1; map[Shift]=Level2; }; type \"ALPHABETIC\" { modifiers=Shift+Lock; map[None]=Level1; map[Shift]=Level2; map[Lock]=Level2; map[Shift+Lock]=Level1; }; }; xkb_compatibility \"uremote\" {}; xkb_symbols \"uremote\" {");
        foreach (var key in Keys)
        {
            var type = key.Lower.Length == 1 && char.IsAsciiLetterLower(key.Lower[0]) ? "ALPHABETIC" : "TWO_LEVEL";
            b.Append($"key <K{key.Linux:D3}> {{ type=\"{type}\", symbols[Group1]=[{key.Lower},{key.Upper}] }};");
        }
        b.Append("modifier_map Shift { <K042>, <K054> }; modifier_map Lock { <K058> }; modifier_map Control { <K029>, <K097> }; modifier_map Mod1 { <K056>, <K100> }; modifier_map Mod4 { <K125>, <K126> }; }; };");
        return b.ToString();
    }
    public static async Task<WaylandVirtualKeyboard> CreateAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var c = await WaylandConnection.OpenAsync(timeout.Token);
        try
        {
            var global = c.Globals.FirstOrDefault(g => g.Interface == "zwp_virtual_keyboard_manager_v1")
                ?? throw new NotSupportedException("Virtual keyboard is unavailable.");
            var seat = c.Globals.FirstOrDefault(g => g.Interface == "wl_seat") ?? throw new NotSupportedException("Seat unavailable.");
            var manager = await c.BindAsync(global, 1, timeout.Token);
            var seatObject = await c.BindAsync(seat, 1, timeout.Token);
            var keyboard = c.AllocateId();
            await c.SendAsync(manager, 0, WaylandConnection.Words(seatObject, keyboard), timeout.Token);
            var bytes = Encoding.UTF8.GetBytes(Keymap() + "\0");
            using var file = UnixFileDescriptor.CreateMemoryFile();
            RandomAccess.SetLength(file, bytes.Length);
            await RandomAccess.WriteAsync(file, bytes, 0, timeout.Token);
            await c.SendFileDescriptorAsync(keyboard, 0, WaylandConnection.Words(1, (uint)bytes.Length), file, timeout.Token);
            await c.RoundtripAsync(timeout.Token);
            return new(c, keyboard);
        }
        catch { c.Dispose(); throw; }
    }
    public async Task ApplyAsync(HostKeyMessage message, CancellationToken ct = default)
    {
        if (LinuxKey(message.MacKey) is not { } code) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await gate.WaitAsync(timeout.Token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (message.Action == KeyAction.Click && held.Contains(code)) return;
            if (message.Action != KeyAction.Release && held.Add(code))
            {
                if (code == 58) locked ^= 2;
                await SendKeyAsync(code, true, timeout.Token);
            }
            if (message.Action != KeyAction.Press && held.Remove(code)) await SendKeyAsync(code, false, timeout.Token);
            await connection.RoundtripAsync(timeout.Token);
        }
        catch { disposed = true; connection.Dispose(); throw; }
        finally { gate.Release(); }
    }
    private async Task SendKeyAsync(uint code, bool down, CancellationToken ct)
    {
        await connection.SendAsync(keyboard, 1, WaylandConnection.Words(unchecked((uint)Environment.TickCount64), code, down ? 1u : 0u), ct);
        uint mods = 0;
        if (held.Contains(42) || held.Contains(54)) mods |= 1;
        if (held.Contains(29) || held.Contains(97)) mods |= 4;
        if (held.Contains(56) || held.Contains(100)) mods |= 8;
        if (held.Contains(125) || held.Contains(126)) mods |= 64;
        await connection.SendAsync(keyboard, 2, WaylandConnection.Words(mods, 0, locked, 0), ct);
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                var keys = held.ToArray(); held.Clear(); locked = 0;
                foreach (var key in keys) await SendKeyAsync(key, false, timeout.Token);
                await connection.SendAsync(keyboard, 2, WaylandConnection.Words(0, 0, 0, 0), timeout.Token);
                await connection.SendAsync(keyboard, 3, [], timeout.Token);
                await connection.RoundtripAsync(timeout.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or System.Net.Sockets.SocketException) { }
            finally { connection.Dispose(); }
        }
        finally { gate.Release(); }
    }
}
