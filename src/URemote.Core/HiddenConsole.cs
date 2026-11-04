using System.Diagnostics;
using System.Runtime.InteropServices;

namespace URemote.Core;

/// <summary>
/// Hides windows owned by a Windows console-subsystem child process.
/// Console-free child processes are started with CreateNoWindow at their call sites.
/// </summary>
public static class HiddenConsole
{
    public static void Hide(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        int id;
        try { id = process.Id; }
        catch (InvalidOperationException) { return; }
        _ = Task.Run(() =>
        {
            try
            {
                for (var attempt = 0; attempt < 25; attempt++)
                {
                    var found = false;
                    EnumWindows((hwnd, _) =>
                    {
                        GetWindowThreadProcessId(hwnd, out var owner);
                        if (owner != (uint)id) return true;
                        found = true;
                        ShowWindow(hwnd, 0);
                        return true;
                    }, IntPtr.Zero);
                    if (found) return;
                    Thread.Sleep(20);
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
}
