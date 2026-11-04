using Avalonia.Controls;
using Avalonia.Interactivity;
using URemote.Core;

namespace URemote.Module;

public partial class LocalPasswordWindow : Window
{
    public LocalPasswordWindow() : this(new LocalConnectionRecord(LocalDesktopProtocol.Vnc, "", 5900, "", "")) { }

    public LocalPasswordWindow(LocalConnectionRecord record)
    {
        InitializeComponent();
        var host = record.Host.Contains(':') ? "[" + record.Host + "]" : record.Host;
        titleText.Text = $"{record.Protocol.ToString().ToUpperInvariant()} · {host}:{record.Port}";
        subtitleText.Text = record.Protocol == LocalDesktopProtocol.Rdp ? $"域：{record.Domain}  ·  用户名：{record.Username}" : "VNC 访问密码";
        Opened += (_, _) => passwordBox.Focus();
    }

    private void Connect_Click(object? sender, RoutedEventArgs e)
    {
        var value = passwordBox.Text ?? "";
        passwordBox.Text = "";
        Close(value);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        passwordBox.Text = "";
        Close(null);
    }
}
