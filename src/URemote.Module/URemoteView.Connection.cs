using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using URemote.Core;

namespace URemote.Module;

public sealed partial class URemoteView
{
    private readonly DispatcherTimer connectionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private HostControlConnection? currentControlConnection;

    private void WireConnectionCard()
    {
        disconnectControl.Click += (_, _) =>
        {
            if (!disconnectControl.IsEnabled || currentControlConnection?.RequestDisconnect is not { } disconnect) return;
            disconnectControl.IsEnabled = false;
            disconnectControl.Content = "正在断开…";
            disconnect();
        };
        connectionTimer.Tick += (_, _) => { if (currentControlConnection is { } connection) connectionDuration.Text = connection.ElapsedText; };
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
        else if (text is "在线 · 不允许被控" or "被控已关闭" or "连接已停止" or "尚未就绪") brush = "AppMutedBrush";
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
            connectionDevice.Text = connectionDetails.Text = connectionLocation.Text = connectionStarted.Text = connectionDuration.Text = "";
            return;
        }
        connectionDevice.Text = devices.FirstOrDefault(x => x.Id == connection.DeviceId)?.Name ?? connection.PlatformName + " 控制端";
        connectionDetails.Text = connection.PlatformName + " · " + (connection.Assistance ? "协助码连接" : "账号连接") + " · " + connection.SessionName;
        connectionLocation.Text = "来源：" + connection.Location;
        connectionLocation.IsVisible = connection.Location.Length > 0;
        connectionDuration.Text = connection.ElapsedText;
        connectionStarted.Text = "开始于 " + connection.ConnectedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");
        status.Text = connection.SessionName + "已连接";
        connectionTimer.Start();
    }
}
