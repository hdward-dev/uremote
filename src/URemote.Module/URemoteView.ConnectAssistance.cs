using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using URemote.Core;
using URemote.Host;
namespace URemote.Module;
public sealed partial class URemoteView
{
    private Control BuildConnectAssistancePage()
    {
        var partnerId = new TextBox { PlaceholderText = "输入对方的协助码", MaxLength = 20 };
        var partnerCode = new TextBox { PlaceholderText = "输入对方的验证码", PasswordChar = '●', MaxLength = 128 };
        var hint = Text("请向对方获取协助码和验证码，连接后将在独立窗口显示远程桌面。", 13, true);
        var connect = new Button { Content = "连接对方", HorizontalAlignment = HorizontalAlignment.Stretch };
        ApplyTheme(connect, Button.BackgroundProperty, "AppAccentBrush");
        connect.Foreground = Avalonia.Media.Brushes.White;
        connect.Click += (_, _) =>
        {
            AssistanceRequest? request = null;
            try
            {
                var id = (partnerId.Text ?? "").Replace(" ", "").Trim();
                request = new AssistanceRequest(id, partnerCode.Text ?? "");
                var state = DesktopHostSession.ReadIdentity(identity.Text ?? "").State;
                if (!state.IsAuthenticated) { hint.Text = "请先在账号与设置中登录。"; return; }
                if (!System.IO.File.Exists(encoder.Text)) { hint.Text = "请先在高级设置中配置视频解码器路径。"; return; }
                if (id == assistanceId.Text) { hint.Text = "这是本机的协助码，请输入对方的协助码。"; return; }
                var key = "assistance:" + id;
                if (remoteWindows.TryGetValue(key, out var existing)) { existing.Activate(); hint.Text = "已打开该协助窗口。"; return; }
                var device = new UuDevice(id, "远程协助", "desktop", 0, "ONLINE", true, true, "", false);
                var window = new RemoteDesktopWindow(device, state, encoder.Text!, request);
                remoteWindows.Add(key, window);
                window.Closed += async (_, _) => { try { await window.Completion; } finally { remoteWindows.Remove(key); } };
                try { window.Show(); } catch { remoteWindows.Remove(key); throw; }
                request = null;
                partnerCode.Text = "";
                hint.Text = "已打开协助窗口，正在验证并连接对方。验证码不会保存。";
            }
            catch (ArgumentException e) { hint.Text = e.Message; }
            catch { hint.Text = "无法发起协助，请检查登录状态和本机配置后重试。"; }
            finally { request?.TakeCode(); }
        };
        partnerCode.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { connect.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); e.Handled = true; } };
        return new StackPanel { Spacing = 18, Children = {
            Text("远程协助", 26), Text("连接对方的设备", 16, true),
            new Border { MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left, Child = Card(new StackPanel { Spacing = 14, Children = {
                Text("对方的协助码", 13), partnerId, Text("对方的验证码", 13), partnerCode, connect, hint } }) }
        } };
    }
}
