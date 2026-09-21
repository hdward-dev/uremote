using System.Globalization;
using URemote.Core;
namespace URemote.Linux;
public static class LinuxDeviceProfile
{
    public static string DeviceName => "aster-" + Environment.MachineName;

    public static HostDeviceProfile Refresh(HostDeviceProfile profile)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var cpu = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim()
            ?? System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        var totalLine = File.ReadLines("/proc/meminfo").First(l => l.StartsWith("MemTotal:", StringComparison.Ordinal));
        var kib = long.Parse(totalLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
        string Read(string path) { try { return File.ReadAllText(path).Trim(); } catch (IOException) { return ""; } catch (UnauthorizedAccessException) { return ""; } }
        var adapter = Directory.EnumerateDirectories("/sys/class/net")
            .Where(d => Directory.Exists(Path.Combine(d, "device")))
            .OrderByDescending(d => Read(Path.Combine(d, "operstate")) == "up")
            .ThenBy(d => d, StringComparer.Ordinal).FirstOrDefault();
        var mac = adapter is null ? "" : Read(Path.Combine(adapter, "address")).ToUpperInvariant();
        string Dmi(string file) { try { return File.ReadAllText("/sys/class/dmi/id/" + file).Trim(); } catch (IOException) { return ""; } catch (UnauthorizedAccessException) { return ""; } }
        return profile with { Name = DeviceName, Cpu = cpu, Memory = (kib / 1024).ToString(CultureInfo.InvariantCulture),
            SystemVersion = File.ReadAllText("/proc/sys/kernel/osrelease").Trim(), SystemName = "Linux", Os = "Linux",
            Mac = mac, Model = Dmi("product_name"), ModelIdentifier = Dmi("board_name"), ModelNumber = "" };
    }
}
