using System.Text;
using URemote.Core;
using URemote.Linux;

static class CommittedTextTests
{
    public static void Run(Action<bool, string> check)
    {
        check(CommittedText.Select(true, true, true, true) == CommittedTextRoute.Keysym
            && CommittedText.Select(true, true, false, true) == CommittedTextRoute.Paste
            && CommittedText.Select(false, true, true, true) == CommittedTextRoute.Eis
            && CommittedText.Select(false, false, true, true) == CommittedTextRoute.Keysym
            && CommittedText.Select(false, false, false, true) == CommittedTextRoute.VirtualKeyboard
            && CommittedText.Select(false, false, false, false) == CommittedTextRoute.Paste
            && CommittedText.Select(null, true, true, true) == CommittedTextRoute.Keysym
            && CommittedText.Select(null, true, false, false) == CommittedTextRoute.Paste,
            "locked portal text skips EIS and uses a keysym, otherwise paste");
        check(CommittedText.RouteName(CommittedTextRoute.Eis) == "eis"
            && CommittedText.RouteName(CommittedTextRoute.Keysym) == "kwin-keysym"
            && CommittedText.RouteName(CommittedTextRoute.VirtualKeyboard) == "virtual-keyboard"
            && CommittedText.RouteName(CommittedTextRoute.Paste) == "paste",
            "committed text route names stay structural");

        var symbols = CommittedText.Keysyms("中A🙂\n\t");
        check(symbols.SequenceEqual(new uint[] { 0x01004E2D, 0x41, 0x0101F642, 0xff0d, 0xff09 }),
            "Unicode keysyms preserve Latin-1, non-BMP, newline and tab");
        check(CommittedText.Keysyms("a\r\nb\rc").SequenceEqual(new uint[] { 0x61, 0xff0d, 0x62, 0xff0d, 0x63 }),
            "committed newlines collapse to Return");
        var rendered = string.Join(",", symbols.Select(item => item.ToString("X8")));
        check(!rendered.Contains('中') && !rendered.Contains("🙂"), "keysym plan does not embed committed text");

        var han = new string('中', 85);
        var chunks = CommittedText.EisChunks(han + "🙂");
        check(chunks.Length == 2 && Encoding.UTF8.GetByteCount(chunks[0]) == 252
            && Encoding.UTF8.GetByteCount(chunks[1]) == Encoding.UTF8.GetByteCount("中🙂")
            && string.Concat(chunks) == han + "🙂",
            "EIS text frames stay within 254 bytes and do not split a rune");
        check(CommittedText.EisChunks(new string('a', 254)).Length == 1
            && CommittedText.EisChunks(new string('a', 255)).Length == 2
            && string.Concat(CommittedText.EisChunks("a\r\nb")) == "a\nb",
            "EIS text chunks cover the libei frame limit");

        var payload = KWinFakeInput.AuthenticatePayload("AsterDock", "remote committed text");
        check(payload.Length % 4 == 0 && KWinFakeInput.KeysymOpcode == 12 && KWinFakeInput.MinimumVersion == 6
            && KWinFakeInput.Pressed == 1 && KWinFakeInput.Released == 0
            && KWinFakeInput.Interface == "org_kde_kwin_fake_input",
            "KWin keysym request matches fake-input version 6");
        check(!Encoding.UTF8.GetString(payload).Contains('中'), "keysym authentication does not carry committed text");

        check(CommittedTextPaste.CtrlV.Select(item => WaylandVirtualKeyboard.LinuxKey(item.MacKey)).SequenceEqual(new uint?[] { 29, 47, 47, 29 })
            && CommittedTextPaste.CtrlV.All(item => !item.ToString().Contains('中')),
            "paste fallback is Left Ctrl and V, with the text left out of the key log");
        try { CommittedText.Keysyms("\0"); check(false, "committed text still rejects control characters"); }
        catch (FormatException) { check(true, "committed text still rejects control characters"); }
    }
}
