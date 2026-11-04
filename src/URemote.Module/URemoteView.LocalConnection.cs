using AsterDock.UI.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using URemote.Core;

namespace URemote.Module;

public sealed partial class URemoteView
{
    private readonly Dictionary<string, LocalDesktopWindow> localWindows = [];
    private Action<LocalDesktopOptions>? rememberLocal;

    private void InitializeLocalConnection(string dataDirectory)
    {
        if (rdpForm.Parent is Panel rdpParent) rdpParent.Children.Remove(rdpForm);
        localForms.Content = rdpForm;
        var history = new LocalConnectionHistory(Path.Combine(dataDirectory, "local-connections.json"));
        void Remember(LocalDesktopOptions options)
        {
            if (disposed) return;
            try { history.Remember(options); RenderHistory(); localHistoryState.Text = ""; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { localHistoryState.Text = "连接已建立，但历史记录未能保存。"; ReportFailure(localHistoryState.Text, "连接历史"); }
        }
        void RenderHistory()
        {
            historyItems.Clear();
            var records = history.Load();
            localHistoryEmpty.IsVisible = records.Count == 0;
            foreach (var record in records)
            {
                var account = record.Protocol == LocalDesktopProtocol.Rdp ?
                    (string.IsNullOrEmpty(record.Domain) ? record.Username : record.Domain + "\\" + record.Username) : "VNC";
                var host = record.Host.Contains(':') ? "[" + record.Host + "]" : record.Host;
                var current = record;
                historyItems.Add(new LocalHistoryModel {
                    Title = $"{current.Protocol.ToString().ToUpperInvariant()} · {host}:{current.Port}",
                    Account = account,
                    QuickConnect = new AsyncCommand(async () =>
                    {
                        ApplyLocalRecord(current);
                        var options = current.Options();
                        if (localWindows.TryGetValue(LocalConnectionKey(options), out var active) && !active.Completion.IsCompleted)
                        { OpenLocalConnection(options, localHistoryState, Remember); return; }
                        var password = await PromptLocalPasswordAsync(current);
                        if (password is not null && !disposed) OpenLocalConnection(current.Options(password), localHistoryState, Remember);
                    })
                });
            }
        }
        rememberLocal = Remember;
        RenderHistory();
    }

    private void LocalProtocol_Click(object? sender, RoutedEventArgs e)
    {
        var vnc = ReferenceEquals(sender, localVncTab);
        var form = vnc ? vncForm : rdpForm;
        if (!ReferenceEquals(localForms.Content, form))
        {
            if (form.Parent is Panel formParent) formParent.Children.Remove(form);
            localForms.Content = form;
        }
        SetClass(localRdpTab, "selected", !vnc);
        SetClass(localVncTab, "selected", vnc);
    }

    private void LocalConnect_Click(object? sender, RoutedEventArgs e)
    {
        var vnc = sender is Button button && button.Tag?.ToString() == "1";
        var state = vnc ? vncState : rdpState;
        var port = vnc ? vncPort : rdpPort;
        var address = vnc ? vncAddress : rdpAddress;
        var password = vnc ? vncPassword : rdpPassword;
        try
        {
            if (port.Value is not { } value || value != decimal.Truncate(value)) throw new ArgumentException("请输入有效的整数端口。");
            var host = (address.Text ?? "").Trim();
            if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
            var options = new LocalDesktopOptions(vnc ? LocalDesktopProtocol.Vnc : LocalDesktopProtocol.Rdp, host, (int)value,
                vnc ? "" : rdpUsername.Text?.Trim() ?? "", password.Text ?? "", vnc ? "" : rdpDomain.Text?.Trim() ?? "");
            options.Validate();
            OpenLocalConnection(options, state, rememberLocal!);
            password.Text = "";
        }
        catch (ArgumentException ex) { state.Text = ex.Message; ReportFailure(ex.Message, "直接连接"); }
        catch { state.Text = "无法打开连接窗口，请重试。"; ReportFailure(state.Text, "直接连接"); }
    }

    private void ApplyLocalRecord(LocalConnectionRecord record)
    {
        var vnc = record.Protocol == LocalDesktopProtocol.Vnc;
        var form = vnc ? vncForm : rdpForm;
        if (!ReferenceEquals(localForms.Content, form))
        {
            if (form.Parent is Panel formParent) formParent.Children.Remove(form);
            localForms.Content = form;
        }
        SetClass(localRdpTab, "selected", !vnc);
        SetClass(localVncTab, "selected", vnc);
        if (vnc)
        {
            vncAddress.Text = record.Host; vncPort.Value = record.Port; vncPassword.Text = ""; vncState.Text = "";
        }
        else
        {
            rdpAddress.Text = record.Host; rdpPort.Value = record.Port; rdpDomain.Text = record.Domain;
            rdpUsername.Text = record.Username; rdpPassword.Text = ""; rdpState.Text = "";
        }
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
            var window = new LocalDesktopWindow(options, appContext?.Feedback);
            window.Connected += () => remember(record.Options());
            localWindows.Add(id, window);
            window.Closed += (_, _) => localWindows.Remove(id);
            try { ShowManaged(window); }
            catch { localWindows.Remove(id); window.Close(); throw; }
            state.Text = "已打开连接窗口。";
        }
        catch (ArgumentException e) { state.Text = e.Message; ReportFailure(e.Message, "直接连接"); }
        catch { state.Text = "无法打开连接窗口，请重试。"; ReportFailure(state.Text, "直接连接"); }
    }

    private async Task<string?> PromptLocalPasswordAsync(LocalConnectionRecord record)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return null;
        var dialog = new LocalPasswordWindow(record);
        FeedbackWindow.Attach(dialog, appContext?.Feedback);
        return await dialog.ShowDialog<string?>(owner);
    }
}
