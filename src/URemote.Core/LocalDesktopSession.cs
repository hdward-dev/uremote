namespace URemote.Core;

public enum LocalDesktopProtocol { Rdp, Vnc }

public sealed record LocalDesktopOptions(LocalDesktopProtocol Protocol, string Host, int Port,
    string Username = "", string Password = "", string Domain = "")
{
    public void Validate()
    {
        if (!Enum.IsDefined(Protocol)) throw new ArgumentException("请选择 RDP 或 VNC。");
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 ||
            (!System.Net.IPAddress.TryParse(Host, out _) && Uri.CheckHostName(Host) != UriHostNameType.Dns))
            throw new ArgumentException("请输入 IP 地址或主机名，端口请填写在端口栏。");
        if (Port is < 1 or > 65535) throw new ArgumentException("端口必须在 1–65535 之间。");
        if (Protocol == LocalDesktopProtocol.Rdp && string.IsNullOrWhiteSpace(Username))
            throw new ArgumentException("RDP 连接需要用户名。");
    }
}

public sealed record LocalDesktopFrame(int Width, int Height, byte[] Pixels);
public sealed record DesktopCertificate(string Host, string Subject, string Issuer, string Fingerprint, bool Changed);

public interface ILocalDesktopSession
{
    event Action<LocalDesktopFrame>? Frame;
    event Action<string>? Status;
    Task RunAsync(LocalDesktopOptions options, CancellationToken cancellationToken);
    void Pointer(int x, int y, int buttons, int wheel = 0);
    void Key(uint symbol, bool down);
}
