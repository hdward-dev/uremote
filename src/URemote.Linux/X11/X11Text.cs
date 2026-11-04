namespace URemote.Linux;

/// <summary>US keysym names from the shared Mac-to-evdev table. Enough for basic text, not an IME.</summary>
internal static class X11Text
{
    private static readonly Dictionary<string, char> names = new()
    {
        ["space"] = ' ', ["Return"] = '\n', ["Tab"] = '\t', ["exclam"] = '!', ["at"] = '@',
        ["numbersign"] = '#', ["dollar"] = '$', ["percent"] = '%', ["asciicircum"] = '^',
        ["ampersand"] = '&', ["asterisk"] = '*', ["parenleft"] = '(', ["parenright"] = ')',
        ["minus"] = '-', ["underscore"] = '_', ["equal"] = '=', ["plus"] = '+',
        ["bracketleft"] = '[', ["bracketright"] = ']', ["braceleft"] = '{', ["braceright"] = '}',
        ["semicolon"] = ';', ["colon"] = ':', ["apostrophe"] = '\'', ["quotedbl"] = '"',
        ["comma"] = ',', ["less"] = '<', ["period"] = '.', ["greater"] = '>',
        ["slash"] = '/', ["question"] = '?', ["backslash"] = '\\', ["bar"] = '|',
        ["grave"] = '`', ["asciitilde"] = '~'
    };

    private static readonly Dictionary<char, (int KeyCode, bool Shift)> keys = Build();

    internal static bool TryPlan(string text, out (int KeyCode, bool Shift)[] plan)
    {
        var steps = new List<(int KeyCode, bool Shift)>(text.Length);
        foreach (var character in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            if (!keys.TryGetValue(character, out var step)) { plan = []; return false; }
            steps.Add(step);
        }
        plan = steps.ToArray();
        return steps.Count > 0;
    }

    private static Dictionary<char, (int KeyCode, bool Shift)> Build()
    {
        var map = new Dictionary<char, (int, bool)>();
        foreach (var key in WaylandVirtualKeyboard.Layout)
        {
            var code = X11Model.XKeyCode(key.Linux);
            Add(map, key.Lower, code, false);
            Add(map, key.Upper, code, true);
        }
        return map;
    }

    private static void Add(Dictionary<char, (int, bool)> map, string symbol, int keycode, bool shift)
    {
        if (!TryGlyph(symbol, out var character)) return;
        map.TryAdd(character, (keycode, shift));
    }

    private static bool TryGlyph(string symbol, out char character)
    {
        if (symbol.Length == 1) { character = symbol[0]; return true; }
        return names.TryGetValue(symbol, out character);
    }
}
