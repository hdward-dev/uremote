using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
namespace URemote.Media;

// Opt-in per installation: do not force a specific interface on other machines.
public sealed record DirectMediaNetwork(string Interface, IPAddress Address)
{
    public static DirectMediaNetwork? FromEnvironment()
    {
        var name = Environment.GetEnvironmentVariable("UREMOTE_DIRECT_INTERFACE");
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Direct interface requires Linux.");
        if (name.Length > 15 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))) throw new ArgumentException("Invalid direct interface.");
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => x.Name == name && x.OperationalStatus == OperationalStatus.Up)
            ?? throw new IOException("Direct interface is unavailable.");
        var address = adapter.GetIPProperties().UnicastAddresses.FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork)?.Address
            ?? throw new IOException("Direct interface has no IPv4 address.");
        return new(name, address);
    }
    public void Bind(Socket socket)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        // Linux SOL_SOCKET / SO_BINDTODEVICE. Binding only a source IP does not bypass policy routing.
        socket.SetRawSocketOption(1, 25, Encoding.ASCII.GetBytes(Interface + "\0"));
    }
}
