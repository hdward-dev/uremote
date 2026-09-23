using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using URemote.Core;

namespace URemote.Module;

public sealed partial class URemoteView
{
    private readonly StackPanel controlConnectionPanel = new() { Spacing = 12, IsVisible = false };
    private readonly TextBlock connectionDevice = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock connectionDetails = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock connectionLocation = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock connectionIdentity = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock connectionDuration = new() { FontSize = 28, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock connectionStarted = new() { FontSize = 12 };
    private readonly DispatcherTimer connectionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Button disconnectControl = new() { Content = "断开控制", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private HostControlConnection? currentControlConnection;

    private Control BuildConnectionCard()
    {
        var heading = Text("●  当前控制连接", 16);
        ApplyTheme(heading, TextBlock.ForegroundProperty, "AppPositiveBrush");
        foreach (var item in new[] { connectionDetails, connectionLocation, connectionIdentity, connectionStarted })
            ApplyTheme(item, TextBlock.ForegroundProperty, "AppMutedBrush");
        controlConnectionPanel.Children.Add(new Separator());
        controlConnectionPanel.Children.Add(heading);
        controlConnectionPanel.Children.Add(Card(new StackPanel { Spacing = 8, Children = {
            connectionDevice, connectionDetails, connectionIdentity, connectionLocation,
            new Border { Height = 4 }, Text("已连接时长", 12, true), connectionDuration, connectionStarted, disconnectControl
        } }));
        disconnectControl.Click += (_, _) =>
        {
            if (!disconnectControl.IsEnabled || currentControlConnection?.RequestDisconnect is not { } disconnect) return;
            disconnectControl.IsEnabled = false;
            disconnectControl.Content = "正在断开…";
            disconnect();
        };
        connectionTimer.Tick += (_, _) => { if (currentControlConnection is { } connection) connectionDuration.Text = connection.ElapsedText; };
        return controlConnectionPanel;
    }

    private void UpdateHostBadge()
    {
        var text = status.Text ?? "正在准备";
        var brush = "AppWarningBrush";
        if (currentControlConnection is { } connection)
        {
            // Activity-based display: an idle controller and a viewer are indistinguishable on the wire.
            var controlling = connection.HasInputActivity && !connection.ViewOnly;
            text = connection.CaptureType is 5 or 8 ? connection.SessionName + "已连接"
                : controlling ? "正在被控" : "正在屏幕共享";
            brush = controlling || connection.CaptureType is 5 or 8 ? "AppWarningBrush" : "AppAccentBrush";
        }
        else if (text is "等待连接" or "远程控制已连接" or "正在共享屏幕" or "屏幕共享已连接" or "远程终端已连接" or "文件传输已连接")
        { text = "被控已开启"; brush = "AppPositiveBrush"; }
        else if (text is "被控已关闭" or "连接已停止" or "尚未就绪") brush = "AppMutedBrush";
        hostBadge.Text = "●  " + text;
        ApplyTheme(hostBadge, TextBlock.ForegroundProperty, brush);
    }

    private void UpdateControlConnection(HostControlConnection? connection)
    {
        connectionTimer.Stop(); currentControlConnection = connection;
        disconnectControl.Content = "断开控制";
        disconnectControl.IsEnabled = connection?.RequestDisconnect is not null;
        controlConnectionPanel.IsVisible = connection is not null;
        UpdateHostBadge();
        if (connection is null)
        {
            ClearScreenFps();
            connectionDevice.Text = connectionDetails.Text = connectionIdentity.Text = connectionLocation.Text = connectionStarted.Text = connectionDuration.Text = "";
            return;
        }
        connectionDevice.Text = devices.FirstOrDefault(x => x.Id == connection.DeviceId)?.Name ?? connection.PlatformName + " 控制端";
        connectionDetails.Text = connection.PlatformName + " · " + (connection.Assistance ? "协助码连接" : "账号连接") + " · " + connection.SessionName;
        connectionIdentity.Text = "设备 ID：" + connection.DeviceId;
        connectionIdentity.IsVisible = connection.DeviceId.Length > 0;
        connectionLocation.Text = "来源：" + connection.Location;
        connectionLocation.IsVisible = connection.Location.Length > 0;
        connectionDuration.Text = connection.ElapsedText;
        connectionStarted.Text = "开始于 " + connection.ConnectedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
        status.Text = connection.SessionName + "已连接";
        connectionTimer.Start();
    }
}
