using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using URemote.Core;

namespace URemote.Module;

public sealed partial class URemoteView
{
    private readonly Dictionary<string, LocalDesktopWindow> localWindows = [];

    private Control BuildLocalConnectionPage()
    {
        var protocolTabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var forms = new ContentControl();
        var panels = new Control[2];
        for (var index = 0; index < 2; index++)
        {
            var protocol = index == 0 ? LocalDesktopProtocol.Rdp : LocalDesktopProtocol.Vnc;
            var address = new TextBox { PlaceholderText = "例如 192.168.1.10 或 desktop.local" };
            var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = index == 0 ? 3389 : 5900, FormatString = "0", Width = 120 };
            var username = new TextBox { PlaceholderText = "远程电脑的用户名" };
            var domain = new TextBox { PlaceholderText = "可选，例如 WORKGROUP" };
            var password = new TextBox { PasswordChar = '●', PlaceholderText = index == 0 ? "远程电脑的登录密码" : "VNC 访问密码" };
            var state = Text("", 12, true);
            var connect = new Button { Content = $"连接 {protocol.ToString().ToUpperInvariant()}", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(24, 10) };
            var endpoint = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
            endpoint.Children.Add(address); Grid.SetColumn(port, 1); endpoint.Children.Add(port);
            var fields = new StackPanel { Spacing = 12, Children = { Text("服务器地址与端口"), endpoint } };
            if (index == 0) { fields.Children.Add(Text("用户名")); fields.Children.Add(username); fields.Children.Add(Text("域")); fields.Children.Add(domain); }
            fields.Children.Add(Text("密码")); fields.Children.Add(password);
            fields.Children.Add(Text(index == 0 ? "连接到已启用远程桌面的电脑，默认端口 3389。" : "连接到已启用 VNC 的电脑，默认端口 5900；支持无认证或 VNC 密码认证（前 8 字节）。", 12, true));
            fields.Children.Add(Text("画面将在 U远程独立窗口中打开。密码仅用于本次连接，不会保存。", 12, true));
            fields.Children.Add(connect); fields.Children.Add(state);
            Avalonia.Automation.AutomationProperties.SetName(address, $"{protocol} 服务器地址");
            Avalonia.Automation.AutomationProperties.SetName(port, $"{protocol} 端口");
            Avalonia.Automation.AutomationProperties.SetName(password, $"{protocol} 密码");
            connect.Click += (_, _) =>
            {
                try
                {
                    if (port.Value is not { } value || value != decimal.Truncate(value)) throw new ArgumentException("请输入有效的整数端口。");
                    var host = (address.Text ?? "").Trim();
                    if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
                    var options = new LocalDesktopOptions(protocol, host, (int)value, username.Text?.Trim() ?? "", password.Text ?? "", domain.Text?.Trim() ?? "");
                    options.Validate();
                    var id = $"{protocol}:{host.ToLowerInvariant()}:{options.Port}:{options.Domain}:{options.Username}";
                    if (localWindows.TryGetValue(id, out var existing)) { existing.Activate(); state.Text = "已切换到当前连接窗口。"; return; }
                    var window = new LocalDesktopWindow(options); localWindows.Add(id, window);
                    window.Closed += (_, _) => localWindows.Remove(id);
                    if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner); else window.Show();
                    password.Text = ""; state.Text = "已打开连接窗口。";
                }
                catch (ArgumentException e) { state.Text = e.Message; }
                catch { state.Text = "无法打开连接窗口，请重试。"; }
            };
            panels[index] = Card(fields);
            var tabIndex = index;
            var tab = new Button { Content = protocol.ToString().ToUpperInvariant(), Padding = new Thickness(24, 9), CornerRadius = new CornerRadius(18) };
            tab.Click += (_, _) =>
            {
                forms.Content = panels[tabIndex];
                foreach (var child in protocolTabs.Children.OfType<Button>()) ApplyTheme(child, Button.BackgroundProperty, child == tab ? "AppAccentSurfaceBrush" : "AppSurfaceBrush");
            };
            ApplyTheme(tab, Button.BackgroundProperty, index == 0 ? "AppAccentSurfaceBrush" : "AppSurfaceBrush"); protocolTabs.Children.Add(tab);
        }
        forms.Content = panels[0];
        return new StackPanel { Spacing = 16, MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { Text("本地连接", 28), Text("通过 IP 地址或主机名直接连接，无需登录 UU 账号。", 13, true), protocolTabs, forms } };
    }
}
