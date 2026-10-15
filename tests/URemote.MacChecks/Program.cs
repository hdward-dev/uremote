using URemote.Core;
using URemote.Linux;
if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
if (HostWirePlatform.Windows) throw new Exception("Mac host must default to its native protocol identity.");
var displays=await DesktopBackend.DiscoverAsync();
if (displays.Any(d=>d.Name==0) || displays.Select(d=>d.Name).Distinct().Count()!=displays.Count) throw new Exception("Invalid display identifiers.");
var rates=await DesktopBackend.ReadRefreshRatesAsync(default);
if (rates.Any(r=>r.Value<1000 || r.Value>500000)) throw new Exception("Invalid refresh rate (expected millihertz).");
Console.WriteLine($"PASS: native Mac ABI loaded; {displays.Count} display(s); refresh metadata validated. No permission requested or input sent.");
