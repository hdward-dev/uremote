using System.Runtime.InteropServices;
using URemote.Core;

namespace URemote.Linux;

public static class DesktopActions
{
    public static async Task<bool> ApplyAsync(HostDesktopAction action, IDesktopKeyboard keyboard, Action<string> report, CancellationToken ct)
    {
        if (action is not (HostDesktopAction.ShowDesktop or HostDesktopAction.ShowWindows)) return false;
        try
        {
            if (OperatingSystem.IsMacOS()) return await MacDesktop.ApplyDesktopActionAsync(action, ct);
            var desktop = DesktopEnvironmentInfo.Current.Name;
            if (desktop == "KDE Plasma")
            {
                await using var bus = await PortalBus.ConnectAsync(ct);
                using var name = PortalValue.String(action == HostDesktopAction.ShowDesktop ? "Show Desktop" : "ExposeAll");
                using var args = PortalValue.Tuple(name);
                using var reply = await bus.CallAsync("org.kde.kglobalaccel.Component", "invokeShortcut", args, ct,
                    "/component/kwin", "org.kde.kglobalaccel");
                return true;
            }
            if (desktop == "GNOME")
            {
                if (action == HostDesktopAction.ShowWindows)
                {
                    await using var bus = await PortalBus.ConnectAsync(ct);
                    using var iface = PortalValue.String("org.gnome.Shell"); using var property = PortalValue.String("OverviewActive");
                    using var get = PortalValue.Tuple(iface, property);
                    using var reply = await bus.CallAsync("org.freedesktop.DBus.Properties", "Get", get, ct, "/org/gnome/Shell", "org.gnome.Shell");
                    using var box = reply.Child(0); using var current = box.Unbox();
                    using var next = PortalValue.Bool(!current.Boolean()); using var variant = PortalValue.Variant(next);
                    using var set = PortalValue.Tuple(iface, property, variant);
                    using var result = await bus.CallAsync("org.freedesktop.DBus.Properties", "Set", set, ct, "/org/gnome/Shell", "org.gnome.Shell");
                    return true;
                }
                var chord = ReadGnomeDesktopShortcuts().Select(ParseShortcut).FirstOrDefault(x => x is not null);
                if (chord is null) { report("desktop-action-unavailable;reason=gnome-show-desktop-shortcut-unset"); return false; }
                await SendChordAsync(keyboard, chord, ct);
                return true;
            }
            report("desktop-action-unavailable;reason=unsupported-desktop");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or FormatException or DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
        { report("desktop-action-failed;type=" + e.GetType().Name); }
        return false;
    }

    internal static int[]? ParseShortcut(string shortcut)
    {
        if (shortcut.Length > 128) return null;
        var keys = new List<int>();
        while (shortcut.StartsWith('<'))
        {
            var end = shortcut.IndexOf('>'); if (end < 0) return null;
            var code = shortcut[1..end].ToLowerInvariant() switch
            { "control" or "ctrl" or "primary" => 59, "alt" or "mod1" => 58, "super" or "mod4" => 55, "shift" => 56, _ => -1 };
            if (code < 0 || keys.Contains(code)) return null;
            keys.Add(code); shortcut = shortcut[(end + 1)..];
        }
        int? key = shortcut.Length == 1 && char.IsAsciiLetterOrDigit(shortcut[0]) ? WindowsVirtualKeys.ToMac(char.ToUpperInvariant(shortcut[0]))
            : shortcut.StartsWith('F') && int.TryParse(shortcut[1..], out var f) && f is >= 1 and <= 12 ? WindowsVirtualKeys.ToMac(111 + f)
            : shortcut.ToLowerInvariant() switch { "space" => 49, "home" => 115, "end" => 119, "up" => 126, "down" => 125, "left" => 123, "right" => 124, _ => null };
        if (key is null) return null;
        keys.Add(key.Value); return keys.ToArray();
    }

    internal static async Task SendChordAsync(IDesktopKeyboard keyboard, int[] keys, CancellationToken ct)
    {
        var pressed = new List<int>();
        try
        {
            foreach (var key in keys) { pressed.Add(key); await keyboard.ApplyAsync(new(KeyAction.Press, key), ct); }
        }
        finally
        {
            foreach (var key in pressed.AsEnumerable().Reverse())
                await keyboard.ApplyAsync(new(KeyAction.Release, key), CancellationToken.None);
        }
    }

    private static string[] ReadGnomeDesktopShortcuts()
    {
        var source = g_settings_schema_source_get_default(); if (source == 0) return [];
        var schema = g_settings_schema_source_lookup(source, "org.gnome.desktop.wm.keybindings", 1); if (schema == 0) return [];
        try
        {
            if (g_settings_schema_has_key(schema, "show-desktop") == 0) return [];
            var settings = g_settings_new_full(schema, 0, 0); if (settings == 0) return [];
            try
            {
                using var value = new PortalValue(g_settings_get_value(settings, "show-desktop")); value.Require("as");
                var result = new List<string>();
                for (var i = 0; i < Math.Min(16, value.Count); i++) { using var child = value.Child(i); result.Add(child.Text()); }
                return result.ToArray();
            }
            finally { NativeGlib.g_object_unref(settings); }
        }
        finally { g_settings_schema_unref(schema); }
    }
    [DllImport(NativeGlib.Gio)] private static extern nint g_settings_schema_source_get_default();
    [DllImport(NativeGlib.Gio)] private static extern nint g_settings_schema_source_lookup(nint source, string name, int recursive);
    [DllImport(NativeGlib.Gio)] private static extern int g_settings_schema_has_key(nint schema, string key);
    [DllImport(NativeGlib.Gio)] private static extern void g_settings_schema_unref(nint schema);
    [DllImport(NativeGlib.Gio)] private static extern nint g_settings_new_full(nint schema, nint backend, nint path);
    [DllImport(NativeGlib.Gio)] private static extern nint g_settings_get_value(nint settings, string key);
}
