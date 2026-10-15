using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using URemote.Core;

namespace URemote.Module;

public sealed partial class URemoteView
{
    private readonly Dictionary<string, LocalDesktopWindow> localWindows = [];

    private Control BuildLocalConnectionPage(string dataDirectory)
    {
        var protocolTabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var forms = new ContentControl();
        var panels = new Control[2];
        var fill = new Action<LocalConnectionRecord>[2];
        var history = new LocalConnectionHistory(Path.Combine(dataDirectory, "local-connections.json"));
        var historyRows = new StackPanel { Spacing = 10 };
        var historyState = Text("", 12, true);
        void Remember(LocalDesktopOptions options)
        {
            if (disposed) return;
            try { history.Remember(options); RenderHistory(); historyState.Text = ""; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { historyState.Text = "连接已建立，但历史记录未能保存。"; }
        }
        void RenderHistory()
        {
            historyRows.Children.Clear();
            var records = history.Load();
            if (records.Count == 0) { historyRows.Children.Add(Text("连接成功后，最近 5 条记录会显示在这里。", 12, true)); return; }
            foreach (var record in records)
            {
                var account = record.Protocol == LocalDesktopProtocol.Rdp ?
                    (string.IsNullOrEmpty(record.Domain) ? record.Username : record.Domain + "\\" + record.Username) : "VNC";
                var host = record.Host.Contains(':') ? "[" + record.Host + "]" : record.Host;
                var quick = new Button { Content = "快速连接", VerticalAlignment = VerticalAlignment.Center };
                var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
                row.Children.Add(new StackPanel { Spacing = 4, Children = {
                    Text($"{record.Protocol.ToString().ToUpperInvariant()} · {host}:{record.Port}"), Text(account, 12, true) } });
                Grid.SetColumn(quick, 1); row.Children.Add(quick);
                quick.Click += async (_, _) =>
                {
                    fill[(int)record.Protocol](record);
                    var options = record.Options();
                    if (localWindows.TryGetValue(LocalConnectionKey(options), out var active) && !active.Completion.IsCompleted)
                    { OpenLocalConnection(options, historyState, Remember); return; }
                    var password = await PromptLocalPasswordAsync(record);
                    if (password is not null && !disposed) OpenLocalConnection(record.Options(password), historyState, Remember);
                };
                historyRows.Children.Add(Card(row, 14));
            }
        }
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
            if (index == 0) { fields.Children.Add(Text("域")); fields.Children.Add(domain); fields.Children.Add(Text("用户名")); fields.Children.Add(username); }
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
                    OpenLocalConnection(options, state, Remember);
                    password.Text = "";
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
            fill[index] = record =>
            {
                tab.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                address.Text = record.Host; port.Value = record.Port; domain.Text = record.Domain; username.Text = record.Username;
                password.Text = ""; state.Text = "";
            };
        }
        forms.Content = panels[0];
        RenderHistory();
        return new StackPanel { Spacing = 16, MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { Text("本地连接", 28), Text("通过 IP 地址或主机名直接连接，无需登录 UU 账号。", 13, true), protocolTabs, forms, Text("最近连接", 20), Text("保留最近 5 条成功连接，不保存密码。", 12, true), historyRows, historyState } };
    }

    private static string LocalConnectionKey(LocalDesktopOptions options) =>
        $"{options.Protocol}:{options.Host.ToLowerInvariant()}:{options.Port}:{options.Domain}:{options.Username}";

    private void OpenLocalConnection(LocalDesktopOptions options, TextBlock state, Action<LocalDesktopOptions> remember)
    {
        try
        {
            options.Validate();
            var id = LocalConnectionKey(options);
            if (localWindows.TryGetValue(id, out var existing))
            {
                if (!existing.Completion.IsCompleted)
                {
                    existing.Activate(); state.Text = "已切换到当前连接窗口。";
                    if (existing.IsConnected) remember(LocalConnectionRecord.From(options).Options());
                    return;
                }
                existing.Close();
            }
            var record = LocalConnectionRecord.From(options);
            var window = new LocalDesktopWindow(options);
            window.Connected += () => remember(record.Options());
            localWindows.Add(id, window);
            window.Closed += (_, _) => localWindows.Remove(id);
            try { if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner); else window.Show(); }
            catch { localWindows.Remove(id); window.Close(); throw; }
            state.Text = "已打开连接窗口。";
        }
        catch (ArgumentException e) { state.Text = e.Message; }
        catch { state.Text = "无法打开连接窗口，请重试。"; }
    }

    private async Task<string?> PromptLocalPasswordAsync(LocalConnectionRecord record)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return null;
        var dialog = new Window { Title = "快速连接", Width = 440, SizeToContent = SizeToContent.Height, CanResize = false };
        var password = new TextBox { PasswordChar = '●', PlaceholderText = "输入密码；无密码认证可留空" };
        var connect = new Button { Content = "连接", IsDefault = true };
        var cancel = new Button { Content = "取消", IsCancel = true };
        connect.Click += (_, _) => { var value = password.Text ?? ""; password.Text = ""; dialog.Close(value); };
        cancel.Click += (_, _) => { password.Text = ""; dialog.Close(); };
        dialog.Content = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children = {
            Text($"{record.Protocol.ToString().ToUpperInvariant()} · {record.Host}:{record.Port}", 18),
            Text(record.Protocol == LocalDesktopProtocol.Rdp ? $"域：{record.Domain}  ·  用户名：{record.Username}" : "VNC 访问密码", 12, true),
            password, Text("密码仅用于本次连接，不会保存。", 12, true),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { cancel, connect } } } };
        dialog.Opened += (_, _) => password.Focus();
        return await dialog.ShowDialog<string?>(owner);
    }
}
