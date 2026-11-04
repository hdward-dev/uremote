using AsterDock.Contracts;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace URemote.Module;

public partial class DeviceDetailView : UserControl
{
    public event EventHandler? Dismissed;
    public IUserFeedbackService? Feedback { get; set; }
    public DeviceDetailView() => InitializeComponent();

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeviceDetailModel model) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } board)
            {
                await board.SetTextAsync(model.DeviceId);
                copyButton.Content = "已复制";
                Feedback.Notify(FeedbackKind.Success, "已复制设备标识");
            }
        }
        catch { copyButton.Content = "复制失败"; Feedback.Notify(FeedbackKind.Error, "复制设备标识失败", "复制"); }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Dismissed?.Invoke(this, EventArgs.Empty);
}
