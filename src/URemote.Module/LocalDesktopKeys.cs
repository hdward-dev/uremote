using Avalonia.Input;
using URemote.Core;

namespace URemote.Module;

internal static class LocalDesktopKeys
{
    private static readonly uint[] Letters = [0x1e,0x30,0x2e,0x20,0x12,0x21,0x22,0x23,0x17,0x24,0x25,0x26,0x32,0x31,0x18,0x19,0x10,0x13,0x1f,0x14,0x16,0x2f,0x11,0x2d,0x15,0x2c];
    private static readonly uint[] Digits = [0x0b,0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0a];
    public static uint? Map(Key key, KeyModifiers modifiers, LocalDesktopProtocol protocol)
    {
        if (protocol == LocalDesktopProtocol.Rdp)
        {
            if (key is >= Key.A and <= Key.Z) return Letters[(int)key - (int)Key.A];
            if (key is >= Key.D0 and <= Key.D9) return Digits[(int)key - (int)Key.D0];
            if (key is >= Key.F1 and <= Key.F10) return (uint)(0x3b + (int)key - (int)Key.F1);
            return key switch {
                Key.Escape => 0x01, Key.Back => 0x0e, Key.Tab => 0x0f, Key.Enter => 0x1c, Key.Space => 0x39,
                Key.LeftCtrl => 0x1d, Key.RightCtrl => 0x11d, Key.LeftShift => 0x2a, Key.RightShift => 0x36,
                Key.LeftAlt => 0x38, Key.RightAlt => 0x138, Key.LWin => 0x15b, Key.RWin => 0x15c,
                Key.CapsLock => 0x3a, Key.NumLock => 0x45, Key.Scroll => 0x46, Key.F11 => 0x57, Key.F12 => 0x58,
                Key.Home => 0x147, Key.End => 0x14f, Key.Up => 0x148, Key.Down => 0x150, Key.Left => 0x14b, Key.Right => 0x14d,
                Key.PageUp => 0x149, Key.PageDown => 0x151, Key.Insert => 0x152, Key.Delete => 0x153,
                Key.OemMinus => 0x0c, Key.OemPlus => 0x0d, Key.OemOpenBrackets => 0x1a, Key.OemCloseBrackets => 0x1b,
                Key.OemSemicolon => 0x27, Key.OemQuotes => 0x28, Key.OemTilde => 0x29, Key.OemPipe => 0x2b,
                Key.OemComma => 0x33, Key.OemPeriod => 0x34, Key.OemQuestion => 0x35,
                Key.NumPad0 => 0x52, Key.NumPad1 => 0x4f, Key.NumPad2 => 0x50, Key.NumPad3 => 0x51,
                Key.NumPad4 => 0x4b, Key.NumPad5 => 0x4c, Key.NumPad6 => 0x4d, Key.NumPad7 => 0x47,
                Key.NumPad8 => 0x48, Key.NumPad9 => 0x49, Key.Decimal => 0x53, Key.Add => 0x4e,
                Key.Subtract => 0x4a, Key.Multiply => 0x37, Key.Divide => 0x135, _ => null
            };
        }
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        if (key is >= Key.A and <= Key.Z) return (uint)((shift ? 'A' : 'a') + (int)key - (int)Key.A);
        if (key is >= Key.D0 and <= Key.D9) return (uint)(shift ? ")!@#$%^&*("[(int)key - (int)Key.D0] : '0' + (int)key - (int)Key.D0);
        if (key is >= Key.F1 and <= Key.F12) return (uint)(0xffbe + (int)key - (int)Key.F1);
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return (uint)(0xffb0 + (int)key - (int)Key.NumPad0);
        return key switch {
            Key.Escape => 0xff1b, Key.Back => 0xff08, Key.Tab => 0xff09, Key.Enter => 0xff0d, Key.Space => 0x20,
            Key.LeftCtrl => 0xffe3, Key.RightCtrl => 0xffe4, Key.LeftShift => 0xffe1, Key.RightShift => 0xffe2,
            Key.LeftAlt => 0xffe9, Key.RightAlt => 0xffea, Key.LWin => 0xffeb, Key.RWin => 0xffec,
            Key.CapsLock => 0xffe5, Key.NumLock => 0xff7f, Key.Scroll => 0xff14,
            Key.Home => 0xff50, Key.End => 0xff57, Key.Up => 0xff52, Key.Down => 0xff54, Key.Left => 0xff51, Key.Right => 0xff53,
            Key.PageUp => 0xff55, Key.PageDown => 0xff56, Key.Insert => 0xff63, Key.Delete => 0xffff,
            Key.OemMinus => shift ? '_' : '-', Key.OemPlus => shift ? '+' : '=',
            Key.OemOpenBrackets => shift ? '{' : '[', Key.OemCloseBrackets => shift ? '}' : ']',
            Key.OemSemicolon => shift ? ':' : ';', Key.OemQuotes => shift ? '"' : '\'',
            Key.OemTilde => shift ? '~' : '`', Key.OemPipe => shift ? '|' : '\\',
            Key.OemComma => shift ? '<' : ',', Key.OemPeriod => shift ? '>' : '.', Key.OemQuestion => shift ? '?' : '/',
            Key.Decimal => 0xffae, Key.Add => 0xffab, Key.Subtract => 0xffad, Key.Multiply => 0xffaa, Key.Divide => 0xffaf, _ => null
        };
    }
}
