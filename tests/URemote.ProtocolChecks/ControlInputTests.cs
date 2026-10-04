using System.Buffers.Binary;
using System.Text;
using URemote.Core;
using URemote.Linux;

static class ControlInputTests
{
    public static void Run(Action<bool, string> check)
    {
        // Explicit protobuf tag 11 envelope, nested tag 2 UTF-8 input, omitted default type/display.
        static byte[] Fixture(string json, params byte[] routing)
        {
            var text = Encoding.UTF8.GetBytes(json);
            return [0x5a, (byte)(text.Length + 2 + routing.Length), 0x12, (byte)text.Length, ..text, ..routing];
        }
        byte[] echoRequest = [0x1a, 0x0b, 0x12, 0x09, ..Encoding.UTF8.GetBytes("{\"seq\":7}")];
        byte[] expectedEcho = [8, 1, 16, 2, 26, 17, 8, 1, 18, 9, ..Encoding.UTF8.GetBytes("{\"seq\":7}"), 34, 2, 24, 2];
        check(HostControlEcho.Reply(echoRequest, 1, 2, true)!.SequenceEqual(expectedEcho), "echo response preserves request sequence and advertises only key/mouse");
        check(HostControlEcho.Reply(expectedEcho, 2, 3, true) is null, "echo response does not create an echo loop");
        byte[] emptyEcho = [8, 7, 26, 0];
        check(HostControlEcho.Reply(emptyEcho, 1, 2, true)!.SequenceEqual(expectedEcho), "native empty-args echo uses outer request sequence");
        check(ControlPacketShape.Describe(false, echoRequest).Contains("argsPresent=True"), "diagnostic reports field shape without payload text");
        var directJson = Encoding.UTF8.GetBytes("{\"action\":\"mouse_move_absolute\",\"abs_x\":0.5,\"abs_y\":0.25,\"screen_id\":0}");
        check(HostControlEcho.Reply(directJson, 1, 2, true) is null && HostControlInput.Decode("CONTROL_DATA_CHANNEL", false, directJson)?.Mouse is { X: 0.5, Y: 0.25 },
            "native direct JSON bypasses echo decoder and reaches pointer parser");
        var secondDisplay = Encoding.UTF8.GetBytes("{\"action\":\"mouse_click\",\"button\":1,\"screen_id\":1}");
        check(HostControlInput.Decode("CONTROL_DATA_CHANNEL", false, secondDisplay) is null
            && HostControlInput.Decode("CONTROL_DATA_CHANNEL", false, secondDisplay, 2) is { DisplayId: 1, Mouse.Action: MouseAction.Click },
            "second display input is accepted only when that display is shared");
        check(HostControlInput.Decode("CONTROL_DATA_CHANNEL", false, Encoding.UTF8.GetBytes("{\"heartbeat\":\"fixture\"}")) is null,
            "native JSON heartbeat is not keyboard input");
        var move = Fixture("{\"action\":\"mouse_move_absolute\",\"abs_x\":0.5,\"abs_y\":0.25}");
        var input = HostControlInput.Decode("CONTROL_DATA_CHANNEL", false, move);
        check(input?.Mouse is { X: 0.5, Y: 0.25, ScreenId: null }, "binary VINPUT routes normalized pointer to selected display");
        check(HostControlInput.Decode("TEXT_DATA_CHANNEL", false, move) is null
            && HostControlInput.Decode("CONTROL_DATA_CHANNEL", true, move) is null, "other channels and text frames cannot inject VINPUT");
        var key = Fixture("{\"action\":\"kbd_click\",\"key\":0}");
        check(HostControlInput.Decode("CONTROL_DATA_CHANNEL", false, key)?.Key is { Action: KeyAction.Click, MacKey: 0 }, "Mac key zero is a valid A key, not a missing field");
        check(HostControlInput.Decode("CONTROL_DATA_CHANNEL", false, Fixture("{\"action\":\"kbd_press\",\"key\":65,\"interrept\":true}"), displayCount: 1, windowsKeys: true)?.Key is { Action: KeyAction.Press, MacKey: 0 },
            "Windows VK_A reaches Linux A through the physical-key normalization");
        check(HostControlInput.Decode("CONTROL_DATA_CHANNEL", false, key, displayCount: 1, windowsKeys: true) is null,
            "Windows reserved key zero cannot inject Mac A");
        check(WindowsVirtualKeys.ToMac(162) == 59 && WindowsVirtualKeys.ToMac(163) == 62
            && WindowsVirtualKeys.ToMac(160) == 56 && WindowsVirtualKeys.ToMac(161) == 60
            && WindowsVirtualKeys.ToMac(91) == 55 && WindowsVirtualKeys.ToMac(92) == 54,
            "Windows modifiers preserve left/right Control, Shift and Super");
        check(HostControlInput.Decode("CONTROL_DATA_CHANNEL", false, move, displayCount: 1, windowsKeys: true)?.Mouse is { X: 0.5, Y: 0.25 },
            "Windows desktop input retains normalized pointer coordinates");
        check(WindowsVirtualKeys.ToMac(96) is { } keypad && WaylandVirtualKeyboard.LinuxKey(keypad) == 82,
            "Windows keypad zero reaches the keypad physical key");
        check(HostControlInput.Decode("CONTROL_DATA_CHANNEL", false, Fixture("{\"action\":\"kbd_click\",\"key\":0}", 0x18, 1)) is null,
            "a different display cannot route keyboard input");
        check(HostControlInput.Decode("CONTROL_DATA_CHANNEL", false, Fixture("{\"action\":\"kbd_click\",\"key\":0}", 0x08, 1)) is null,
            "text message type cannot be interpreted as physical input");
        try { HostControlInput.Decode("CONTROL_DATA_CHANNEL", false, Fixture("{\"action\":\"kbd_click\",\"key\":0,\"key\":1}")); check(false, "ambiguous key rejected"); }
        catch (FormatException) { check(true, "ambiguous key rejected"); }
        try { HostControlInput.Decode("CONTROL_DATA_CHANNEL", false, move[..^1]); check(false, "truncated VINPUT rejected"); }
        catch (FormatException) { check(true, "truncated VINPUT rejected"); }
        try { HostControlInput.Decode("CONTROL_DATA_CHANNEL", false, Fixture("{}", 0x18, 0x80, 0x80, 0x80, 0x80, 0x10)); check(false, "overflow cannot alias display zero"); }
        catch (FormatException) { check(true, "overflow cannot alias display zero"); }
        check(WaylandVirtualKeyboard.LinuxKey(0) == 30 && WaylandVirtualKeyboard.LinuxKey(56) == 42
            && WaylandVirtualKeyboard.LinuxKey(123) == 105 && WaylandVirtualKeyboard.LinuxKey(127) is null,
            "Mac letter, modifier and arrow map to evdev; unknown keys ignored");
        var committedJson = "{\"action\":\"text_input\",\"content\":\"中文ABC🙂\"}";
        var committed = HostControlInput.Decode("CONTROL_DATA_CHANNEL", true, Encoding.UTF8.GetBytes(committedJson), windowsKeys: true, displayCount: 1);
        check(committed?.Text == "中文ABC🙂" && committed.Key is null, "mobile committed Unicode bypasses physical key translation");
        check(HostControlInput.Decode("CONTROL_DATA_CHANNEL", false, Fixture(committedJson))?.Text == committed?.Text,
            "committed text supports binary VINPUT envelope");
        check(!committed!.ToString().Contains("中文") && !ControlPacketShape.Describe(true, Encoding.UTF8.GetBytes(committedJson)).Contains("中文"),
            "committed text stays out of model and structural diagnostics");
        check(HostControlInput.Decode("TEXT_DATA_CHANNEL", true, Encoding.UTF8.GetBytes(committedJson)) is null,
            "text input respects control channel routing");
        foreach (var invalid in new[] { "{\"action\":\"text_input\"}", "{\"action\":\"text_input\",\"content\":3}",
            "{\"action\":\"text_input\",\"content\":\"a\",\"content\":\"b\"}", "{\"action\":\"text_input\",\"content\":\"\\u0000\"}" })
        {
            try { HostControlInput.Decode("CONTROL_DATA_CHANNEL", true, Encoding.UTF8.GetBytes(invalid)); check(false, "invalid committed text rejected"); }
            catch (FormatException) { check(true, "invalid committed text rejected"); }
        }
        try { HostControlInput.ValidateText(new string('中', 2000)); check(false, "Unicode text byte limit enforced"); }
        catch (FormatException) { check(true, "Unicode text byte limit enforced"); }
        try { HostControlInput.ValidateText("\ud800"); check(false, "unpaired surrogate rejected"); }
        catch (FormatException) { check(true, "unpaired surrogate rejected"); }
        var unicodeMap = WaylandVirtualKeyboard.TextKeymap("中A🙂".EnumerateRunes().ToArray());
        check(unicodeMap.Contains("U4E2D") && unicodeMap.Contains("U0041") && unicodeMap.Contains("U1F642")
            && !unicodeMap.Contains("Shift"), "Unicode keymap preserves non-BMP text without keyboard layout modifiers");
        var axis = WaylandVirtualPointer.AxisPayload(1, 0, 2);
        check(BinaryPrimitives.ReadInt32LittleEndian(axis.AsSpan(8)) == -512, "scroll uses signed Wayland fixed-point axis units");
    }
}
