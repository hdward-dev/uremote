using System.Text;
using System.Text.Json;

namespace URemote.Core;

public enum KeyAction { Press, Release, Click }
public sealed record HostKeyMessage(KeyAction Action, int MacKey)
{
    public override string ToString() => "HostKeyMessage(input redacted)";
}
public sealed record HostControlInput(HostMouseMessage? Mouse = null, HostKeyMessage? Key = null, int DisplayId = 0)
{
    public override string ToString() => "HostControlInput(contents redacted)";
    // This host advertises Mac protocol semantics and one display (id 0).
    public static HostControlInput? Decode(string channel, bool isText, ReadOnlyMemory<byte> bytes, int displayCount = 1)
    {
        if (channel != "CONTROL_DATA_CHANNEL") return null;
        if (isText && (bytes.Length == 0 || bytes.Span[0] != (byte)'{')) return null;
        if (bytes.Length > 8192) throw new FormatException("Input envelope too large.");
        if (displayCount is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(displayCount));
        var displayId = 0;
        string text;
        if (bytes.Length > 0 && bytes.Span[0] == (byte)'{')
            text = new UTF8Encoding(false, true).GetString(bytes.Span);
        else
        {
            var envelope = ProtoFields.Read(bytes);
            if (envelope.Bytes(11) is not { } body) return null;
            var input = ProtoFields.Read(body);
            if (input.Items.Any(f => f.Tag is 1 or 3 && f.Value > int.MaxValue))
                throw new FormatException("Input routing field exceeds range.");
            if (input.Int(1) != 0 || input.Int(3) < 0 || input.Int(3) >= displayCount) return null;
            displayId = input.Int(3);
            text = input.Text(2);
        }
        if (Encoding.UTF8.GetByteCount(text) > 4096) throw new FormatException("Input JSON too large.");
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Expected input object.");
        var fields = new HashSet<string>();
        foreach (var p in root.EnumerateObject()) if (!fields.Add(p.Name)) throw new FormatException("Duplicate input field.");
        if (root.TryGetProperty("screen_id", out var screen))
        {
            if (!screen.TryGetInt32(out var id) || id < 0 || id >= displayCount) return null;
            displayId = id;
        }
        if (!root.TryGetProperty("action", out var actionField) || actionField.ValueKind != JsonValueKind.String) return null;
        var action = actionField.GetString();
        if (action?.StartsWith("mouse_", StringComparison.Ordinal) == true)
            return new(Mouse: HostMouseMessage.Parse(text, PointerCoordinates.MacNormalized) with { ScreenId = null }, DisplayId: displayId);
        var keyAction = action switch { "kbd_press" => KeyAction.Press, "kbd_release" => KeyAction.Release,
            "kbd_click" => KeyAction.Click, _ => (KeyAction?)null };
        if (keyAction is null) return null; // IME/text/clipboard are deliberately separate protocols.
        if (!root.TryGetProperty("key", out var key) || !key.TryGetInt32(out var code) || code is < 0 or > 127)
            throw new FormatException("Invalid Mac key code.");
        return new(Key: new(keyAction.Value, code), DisplayId: displayId);
    }
}
