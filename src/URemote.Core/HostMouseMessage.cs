using System.Text;
using System.Text.Json;

namespace URemote.Core;

public enum PointerCoordinates { MacNormalized, WindowsPixels }
public enum MouseAction { MoveAbsolute, Press, Release, Click, Scroll }

// Parsed input only. No OS input is injected and no network listener is opened.
// A future host must authenticate the session before passing messages to an input backend.
public sealed record HostMouseMessage(MouseAction Action, double X = 0, double Y = 0,
    int Button = 0, int? ScreenId = null)
{
    public override string ToString() => $"HostMouseMessage({Action}; input values redacted)";

    public static HostMouseMessage Parse(string message, PointerCoordinates coordinates)
    {
        if (Encoding.UTF8.GetByteCount(message) > 4096) throw new FormatException("Input message too large.");
        using var document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Expected input object.");
        var names = new HashSet<string>();
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name)) throw new FormatException("Duplicate input field.");
        if (!root.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String)
            throw new FormatException("Missing action.");
        int? screen = root.TryGetProperty("screen_id", out _) ? ReadInt(root, "screen_id", 0, int.MaxValue) : null;
        switch (action.GetString())
        {
            case "mouse_move_absolute":
                double limit = coordinates == PointerCoordinates.MacNormalized ? 1 : 131072;
                return new(MouseAction.MoveAbsolute, ReadNumber(root, "abs_x", 0, limit),
                    ReadNumber(root, "abs_y", 0, limit), ScreenId: screen);
            case "mouse_press":
            case "mouse_release":
            case "mouse_click":
                var button = ReadInt(root, "button", 1, 16);
                if (button is not (1 or 2 or 4 or 8 or 16)) throw new FormatException("Unknown mouse button.");
                var kind = action.GetString() switch
                {
                    "mouse_press" => MouseAction.Press,
                    "mouse_release" => MouseAction.Release,
                    _ => MouseAction.Click
                };
                return new(kind, Button: button, ScreenId: screen);
            case "mouse_scroll":
                return new(MouseAction.Scroll, ReadNumber(root, "delta_x", -10000, 10000),
                    ReadNumber(root, "delta_y", -10000, 10000), ScreenId: screen);
            default:
                throw new FormatException("Unsupported mouse action.");
        }
    }

    private static double ReadNumber(JsonElement root, string key, double min, double max)
    {
        if (!root.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.Number
            || !element.TryGetDouble(out var value) || !double.IsFinite(value) || value < min || value > max)
            throw new FormatException("Invalid input field: " + key);
        return value;
    }

    private static int ReadInt(JsonElement root, string key, int min, int max)
    {
        if (!root.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value) || value < min || value > max)
            throw new FormatException("Invalid integer field: " + key);
        return value;
    }
}
