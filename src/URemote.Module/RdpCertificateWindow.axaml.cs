using Avalonia.Controls;
using Avalonia.Interactivity;
using URemote.Core;

namespace URemote.Module;

public partial class RdpCertificateWindow : Window
{
    private readonly TaskCompletionSource<bool> result;

    public RdpCertificateWindow() : this(new DesktopCertificate("localhost", "未受信任", "未知", "未知", false), new(TaskCreationOptions.RunContinuationsAsynchronously)) { }

    public RdpCertificateWindow(DesktopCertificate certificate, TaskCompletionSource<bool> result)
    {
        InitializeComponent();
        this.result = result;
        message.Text = $"{(certificate.Changed ? "服务器证书已变化" : "服务器证书未受信任")}\n\n服务器：{certificate.Host}\n主体：{certificate.Subject}\n颁发者：{certificate.Issuer}\n指纹：{certificate.Fingerprint}";
        Closed += (_, _) => result.TrySetResult(false);
    }

    private void Accept_Click(object? sender, RoutedEventArgs e)
    {
        result.TrySetResult(true);
        Close();
    }

    private void Reject_Click(object? sender, RoutedEventArgs e) => Close();
}
