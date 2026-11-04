using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using URemote.Core;
using URemote.Host;
namespace URemote.Module;
public sealed partial class URemoteView
{
    private void WireAssistanceConnect()
    {
        assistanceConnect.Click += (_, _) =>
        {
            AssistanceRequest? request = null;
            try
            {
                var id = (partnerId.Text ?? "").Replace(" ", "").Trim();
                request = new AssistanceRequest(id, partnerCode.Text ?? "");
                var state = DesktopHostSession.ReadIdentity(identity.Text ?? "").State;
                if (!state.IsAuthenticated) { assistanceConnectHint.Text = "请先在账号与设置中登录。"; return; }
                if (!System.IO.File.Exists(appliedEncoder)) { assistanceConnectHint.Text = "请先在高级设置中配置视频解码器路径。"; return; }
                if (id == assistanceId.Text) { assistanceConnectHint.Text = "这是本机的协助码，请输入对方的协助码。"; return; }
                var key = "assistance:" + id;
                if (remoteWindows.TryGetValue(key, out var existing)) { existing.Activate(); assistanceConnectHint.Text = "已打开该协助窗口。"; return; }
                var device = new UuDevice(id, "远程协助", "desktop", 0, "ONLINE", true, true, "", false);
                var window = new RemoteDesktopWindow(device, state, appliedEncoder, request, appContext?.Feedback);
                remoteWindows.Add(key, window);
                window.Closed += async (_, _) => { try { await window.Completion; } finally { remoteWindows.Remove(key); } };
                try { ShowManaged(window); } catch { remoteWindows.Remove(key); throw; }
                request = null;
                partnerCode.Text = "";
                assistanceConnectHint.Text = "已打开协助窗口，正在验证并连接对方。验证码不会保存。";
            }
            catch (ArgumentException e) { assistanceConnectHint.Text = e.Message; ReportFailure(e.Message, "远程协助"); }
            catch { assistanceConnectHint.Text = "无法发起协助，请检查登录状态和本机配置后重试。"; ReportFailure(assistanceConnectHint.Text, "远程协助"); }
            finally { request?.TakeCode(); }
        };
        partnerCode.KeyDown += (_, e) => { if (e.Key == Key.Enter) { assistanceConnect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; } };
    }
}
