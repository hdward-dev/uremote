using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using URemote.Core;

namespace URemote.Linux;

internal static class WindowsDeviceProfile
{
    internal static HostDeviceProfile Refresh(HostDeviceProfile profile)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var mac = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .OrderByDescending(n => n.OperationalStatus == OperationalStatus.Up)
            .Select(n => n.GetPhysicalAddress().GetAddressBytes())
            .FirstOrDefault(a => a.Length == 6);
        var memory = MemoryStatus();
        var cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString();
        var board = ReadRegistry(@"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardProduct");
        var screen = PrimaryScreen();
        return profile with
        {
            Name = LinuxDeviceProfile.DeviceName,
            Cpu = cpu,
            Memory = (memory / 1048576).ToString(CultureInfo.InvariantCulture),
            SystemVersion = Environment.OSVersion.VersionString,
            SystemName = "Windows",
            Os = "Windows",
            Model = board,
            ModelIdentifier = board,
            ModelNumber = "",
            Mac = mac is null ? "" : string.Join(":", mac.Select(b => b.ToString("X2"))),
            Resolution = screen,
            Video = string.IsNullOrEmpty(screen) ? [] : [screen],
            Dpi = PrimaryDpi()
        };
    }

    private static string PrimaryScreen()
    {
        var width = GetSystemMetrics(0);
        var height = GetSystemMetrics(1);
        return width > 0 && height > 0 ? width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture) : "";
    }

    private static int PrimaryDpi()
    {
        var dpi = GetDpiForSystem();
        return dpi is >= 72 and <= 480 ? dpi : 96;
    }

    private static long MemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? checked((long)status.TotalPhys) : 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string ReadRegistry(string key, string name)
    {
        try
        {
            using var registry = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
            return registry?.GetValue(name) as string ?? "";
        }
        catch (System.Security.SecurityException) { return ""; }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatusEx { public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern int GetDpiForSystem();
}
