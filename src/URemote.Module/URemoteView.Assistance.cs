using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using URemote.Core;
using URemote.Host;
namespace URemote.Module;
public sealed partial class URemoteView
{
    private readonly TextBox assistanceId = new() { IsReadOnly = true, Text = "正在获取…", FontSize = 18 };
    private readonly TextBox assistanceCode = new() { IsReadOnly = true, PasswordChar = '●', FontSize = 18 };
    private readonly TextBlock assistanceHint = new() { Text = "获取本机协助信息后，可在官方客户端输入协助码连接。", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private string? assistanceDevice;
    private bool allowAssistance = true;
    private readonly ToggleSwitch assistanceSwitch = new() { OnContent = null, OffContent = null };
    private Control? assistanceCredentials;
    private Control BuildAssistanceCard()
    {
        assistanceSwitch.IsChecked = allowAssistance;
        Avalonia.Automation.AutomationProperties.SetName(assistanceSwitch, "允许远程协助");
        assistanceSwitch.PropertyChanged += (_, e) =>
        {
            if(e.Property != ToggleSwitch.IsCheckedProperty) return;
            var enabled = assistanceSwitch.IsChecked == true;
            if(enabled == allowAssistance) return;
            try
            {
                System.IO.File.WriteAllText(settingsFile, System.Text.Json.JsonSerializer.Serialize(new Settings(identity.Text ?? "", encoder.Text ?? "", enabled, hostEnabled)));
                allowAssistance = enabled;
                var device = assistanceDevice ?? DesktopHostSession.ReadIdentity(identity.Text ?? "").State.DeviceId;
                HostAssistance.SetEnabled(device, enabled);
                UpdateAssistanceState();
            }
            catch { assistanceHint.Text = "开关设置保存失败，请重试。"; assistanceSwitch.IsChecked = allowAssistance; }
        };
        var heading = new Grid { ColumnDefinitions = new("*,Auto") };
        heading.Children.Add(Text("允许远程协助",16)); Grid.SetColumn(assistanceSwitch,1); heading.Children.Add(assistanceSwitch);
        var copyId = new Button { Content = "复制协助码" };
        var copyCode = new Button { Content = "复制验证码" };
        var reveal = new Button { Content = "显示" };
        var rotate = new Button { Content = "更换验证码" };
        var reload = new Button { Content = "重新获取" };
        copyId.Click += async (_, _) => await CopyAssistanceAsync(assistanceId.Text);
        copyCode.Click += async (_, _) => await CopyAssistanceAsync(assistanceCode.Text);
        reveal.Click += (_, _) => { var hide = assistanceCode.PasswordChar == '\0'; assistanceCode.PasswordChar = hide ? '●' : '\0'; reveal.Content = hide ? "显示" : "隐藏"; };
        rotate.Click += (_, _) => { if(assistanceDevice is { } device) { assistanceCode.Text = HostAssistance.Rotate(device); assistanceHint.Text = "验证码已更换；旧码不能用于新的协助连接。"; } };
        reload.Click += async (_, _) => await RefreshAssistanceAsync();
        var columns = new StackPanel { Spacing = 12, Children = {
            Text("协助码", 12, true), assistanceId, copyId,
            Text("验证码", 12, true), assistanceCode,
            new WrapPanel { Orientation = Orientation.Horizontal, Children = { reveal, copyCode, rotate } }
        } };
        assistanceCredentials = columns; columns.IsEnabled = allowAssistance;
        foreach (var button in new[] { copyId, copyCode, reveal, rotate, reload }) { button.FontSize = 11; button.Padding = new Thickness(8, 5); }
        assistanceSwitch.MinWidth = 0;
        assistanceHint.FontSize = 12;
        return new StackPanel { Spacing = 12, Children = { heading, columns, assistanceHint, reload } };
    }
    private void UpdateAssistanceState()
    {
        if(assistanceCredentials is not null) assistanceCredentials.IsEnabled = allowAssistance;
        assistanceCode.Text = allowAssistance && assistanceDevice is { } device ? HostAssistance.Code(device) : "";
        if(!allowAssistance) assistanceCode.PasswordChar = '●';
        assistanceHint.Text = allowAssistance ? "在官方客户端输入协助码和验证码连接。" : "远程协助已关闭，同账号设备被控不受影响。";
    }
    private async Task CopyAssistanceAsync(string? value)
    {
        if(!allowAssistance || assistanceDevice is null || string.IsNullOrEmpty(value)) return;
        try { if(TopLevel.GetTopLevel(this)?.Clipboard is { } c) await c.SetTextAsync(value); }
        catch { assistanceHint.Text = "复制失败，请手动选择并复制。"; }
    }
    private async Task RefreshAssistanceAsync()
    {
        try
        {
            var state = DesktopHostSession.ReadIdentity(identity.Text ?? "").State;
            using var api = new UuMacHostApi(state);
            var info = await api.GetAssistanceInfoAsync(lifetime.Token);
            if(disposed) return;
            assistanceDevice = state.DeviceId; assistanceId.Text = info.ConnectId;
            HostAssistance.SetEnabled(state.DeviceId, allowAssistance);
            UpdateAssistanceState();
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
        catch { if(!disposed) { assistanceDevice = null; assistanceId.Text = "未获取"; assistanceCode.Text = ""; assistanceHint.Text = "协助信息获取失败，请检查登录状态后重新获取。"; } }
    }
}
