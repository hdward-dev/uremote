using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using URemote.Core;
namespace URemote.Linux;
internal static class MacDeviceProfile
{
    internal static HostDeviceProfile Refresh(HostDeviceProfile profile)
    {
        string Read(string program,params string[] arguments)
        {
            var start=new ProcessStartInfo(program) { RedirectStandardOutput=true, RedirectStandardError=true, UseShellExecute=false };
            foreach(var arg in arguments) start.ArgumentList.Add(arg);
            using var process=Process.Start(start) ?? throw new IOException("Unable to read Mac system profile.");
            var output=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(3000)) { process.Kill(); throw new IOException("Mac system profile timed out."); }
            error.GetAwaiter().GetResult();
            return process.ExitCode==0?output.GetAwaiter().GetResult().Trim():"";
        }
        var memory=Read("/usr/sbin/sysctl","-n","hw.memsize");
        var mac=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.NetworkInterfaceType!=NetworkInterfaceType.Loopback)
            .OrderByDescending(n=>n.OperationalStatus==OperationalStatus.Up).Select(n=>n.GetPhysicalAddress().GetAddressBytes()).FirstOrDefault(a=>a.Length==6);
        var model=Read("/usr/sbin/sysctl","-n","hw.model");
        return profile with { Name=LinuxDeviceProfile.DeviceName, Cpu=Read("/usr/sbin/sysctl","-n","machdep.cpu.brand_string"),
            Memory=long.TryParse(memory,out var bytes)?(bytes/1048576).ToString(CultureInfo.InvariantCulture):"0",
            SystemVersion=Read("/usr/bin/sw_vers","-productVersion"), SystemName="macOS", Os="macOS",
            Model=model, ModelIdentifier=model, ModelNumber="", Mac=mac is null?"":string.Join(":",mac.Select(b=>b.ToString("X2"))) };
    }
}
