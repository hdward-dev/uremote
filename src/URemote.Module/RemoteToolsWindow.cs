using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using URemote.Core;
using URemote.Host;
namespace URemote.Module;

public sealed class RemoteToolsWindow : Window
{
    private readonly DesktopControllerSession controller = new();
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource terminalOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBlock status = new() { Text = "正在连接…", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBox transcript = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontFamily = new Avalonia.Media.FontFamily("monospace") };
    private readonly TextBox command = new() { PlaceholderText = "输入命令，按 Enter 发送", IsEnabled = false };
    private readonly TextBox path = new() { Text = ":/", MinWidth = 160 };
    private readonly ListBox listing = new();
    private readonly StackPanel fileActions = new() { Orientation = Orientation.Horizontal, Spacing = 8, IsEnabled = false };
    private readonly ControllerFiles files;
    private readonly bool terminal;
    private uint sessionId;
    private readonly Decoder terminalDecoder = Encoding.UTF8.GetDecoder();
    private bool closed, busy;
    private string currentPath = ":/";
    private readonly Stack<string> history = [];
    private CancellationTokenSource? transferStop;
    private int terminalStarted;
    private readonly HashSet<string> dataChannels = [];
    public Task Completion { get; private set; } = Task.CompletedTask;

    public RemoteToolsWindow(UuDevice device, LoginState state, bool terminal)
    {
        this.terminal = terminal; files = new(controller.SendData);
        Title = device.Name + (terminal ? " · 远程终端" : " · 文件传输"); Width = 940; Height = 650; MinWidth = 620; MinHeight = 420;
        var disconnect = new Button { Content = "断开" }; disconnect.Click += (_, _) => Close();
        var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 }; header.Children.Add(status); Grid.SetColumn(disconnect, 1); header.Children.Add(disconnect);
        var body = new Grid { Margin = new Thickness(18), RowDefinitions = new("Auto,*,Auto"), RowSpacing = 12 }; body.Children.Add(header);
        if (terminal)
        {
            Grid.SetRow(transcript, 1); body.Children.Add(transcript);
            var send = new Button { Content = "发送" }; send.Click += (_, _) => SendCommand();
            var interrupt = new Button { Content = "Ctrl+C" }; interrupt.Click += (_, _) => SendTerminal(2, [3]);
            var inputRow = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8 }; inputRow.Children.Add(command); Grid.SetColumn(send, 1); inputRow.Children.Add(send); Grid.SetColumn(interrupt, 2); inputRow.Children.Add(interrupt);
            command.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SendCommand(); e.Handled = true; } };
            var footer = new StackPanel { Spacing = 8, Children = { inputRow, new TextBlock { Text = "命令模式 · 支持命令输入与中断；暂不支持 vim 等全屏终端程序", FontSize = 12 } } };
            Grid.SetRow(footer, 2); body.Children.Add(footer);
        }
        else
        {
            var parent = new Button { Content = "返回" }; parent.Click += async (_, _) => { if (!busy && history.Count > 0) await ListAsync(history.Pop(), false); };
            var refresh = new Button { Content = "打开 / 刷新" }; refresh.Click += async (_, _) => await ListAsync(path.Text ?? ":/", true);
            var upload = new Button { Content = "上传文件" }; upload.Click += async (_, _) => await UploadAsync();
            var download = new Button { Content = "下载所选" }; download.Click += async (_, _) => await DownloadAsync();
            var cancel = new Button { Content = "取消传输" }; cancel.Click += (_, _) => transferStop?.Cancel();
            fileActions.Children.Add(parent); fileActions.Children.Add(refresh); fileActions.Children.Add(upload); fileActions.Children.Add(download);
            var filesBody = new Grid { RowDefinitions = new("Auto,Auto,*"), RowSpacing = 10 }; filesBody.Children.Add(path); Grid.SetRow(fileActions, 1); filesBody.Children.Add(fileActions); Grid.SetRow(listing, 2); filesBody.Children.Add(listing);
            listing.DoubleTapped += async (_, _) => { if (listing.SelectedItem is ListBoxItem { Tag: RemoteFileEntry { Directory: true } d }) await ListAsync(d.Path, true); };
            Grid.SetRow(filesBody, 1); body.Children.Add(filesBody); Grid.SetRow(cancel, 2); body.Children.Add(cancel);
            files.Progress += (n, total) => Post(() => status.Text = $"正在传输 · {n:N0} / {total:N0} 字节");
        }
        Content = body;
        controller.DataReceived += Receive;
        controller.Status += s =>
        {
            if (s.StartsWith("data-ready-", StringComparison.Ordinal)) lock (dataChannels)
            {
                dataChannels.Add(s[11..]);
                if (terminal && dataChannels.Contains("BINARY_DATA_CHANNEL") || !terminal && dataChannels.Contains("TEXT_DATA_CHANNEL") && dataChannels.Contains("FILE_DATA_CHANNEL")) ready.TrySetResult();
            }
            Post(() => { if (!busy && s is "joining" or "signaling" or "negotiating") status.Text = "正在建立安全连接…"; });
        };
        Opened += (_, _) => Completion = RunAsync(state, device.Id);
        Closed += (_, _) => { if (terminal && sessionId != 0) SendTerminal(4, []); closed = true; transferStop?.Cancel(); stop.Cancel(); };
    }
    private void Post(Action action) => Dispatcher.UIThread.Post(() => { if (!closed) action(); });
    private async Task RunAsync(LoginState state, string target)
    {
        var connection = controller.RunAsync(state, target, "", stop.Token, terminal ? 8 : 5);
        try
        {
            var wait = ready.Task.WaitAsync(TimeSpan.FromSeconds(30), stop.Token);
            if (await Task.WhenAny(connection, wait) == connection) await connection;
            await wait;
            if (terminal)
            {
                if (Interlocked.Exchange(ref terminalStarted, 1) == 0 && !SendTerminal(1, JsonSerializer.SerializeToUtf8Bytes(new { cols = 100, rows = 30, option = "create" }))) throw new IOException();
                await terminalOpened.Task.WaitAsync(TimeSpan.FromSeconds(20), stop.Token);
                command.IsEnabled = true; status.Text = "终端已连接"; command.Focus();
            }
            else { fileActions.IsEnabled = true; await ListAsync(":/", false); }
            await connection;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch { Post(() => status.Text = "连接失败或已断开，请确认设备在线、功能受支持且没有其他控制会话。"); }
        finally { stop.Cancel(); try { await connection; } catch { } Post(() => { command.IsEnabled = false; fileActions.IsEnabled = false; }); }
    }
    private void Receive(string channel, byte[] bytes)
    {
        if (!terminal) { files.Receive(channel, bytes); return; }
        if (channel != "BINARY_DATA_CHANNEL") return;
        try
        {
            var frame = HostTerminalProtocol.DecodeFrame(bytes); if (frame is null) return;
            if (frame.Type == 13)
            {
                using var json = JsonDocument.Parse(frame.Payload);
                if (json.RootElement.GetProperty("code").GetInt32() != 0 || frame.SessionId == 0) { terminalOpened.TrySetException(new IOException("终端创建失败")); return; }
                sessionId = frame.SessionId; terminalOpened.TrySetResult();
            }
            else if (frame.Type == 5 && frame.SessionId == sessionId)
            {
                var chars = new char[frame.Payload.Length + 2];
                var count = terminalDecoder.GetChars(frame.Payload, chars, false);
                var text = new string(chars, 0, count);
                text = Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\a]*(?:\a|\x1B\\)", "");
                var clean = new string(text.Where(c => c is '\n' or '\r' or '\t' || !char.IsControl(c)).ToArray());
                Post(() => { var content = transcript.Text + clean; transcript.Text = content.Length > 262144 ? content[^262144..] : content; transcript.CaretIndex = transcript.Text.Length; });
            }
            else if (frame.Type is 6 or 8 or 12) Post(() => status.Text = "终端会话已结束或返回错误");
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException) { Post(() => status.Text = "收到无效终端响应"); }
    }
    private bool SendTerminal(byte type, byte[] payload) => controller.SendData("BINARY_DATA_CHANNEL", HostTerminalProtocol.EncodeFrame(type, sessionId, payload));
    private void SendCommand()
    {
        if (!command.IsEnabled || sessionId == 0) return;
        if (SendTerminal(2, Encoding.UTF8.GetBytes((command.Text ?? "") + "\r"))) command.Text = "";
        else status.Text = "发送失败，请重新连接。";
    }
    private async Task ListAsync(string destination, bool remember)
    {
        if (busy) return; busy = true; fileActions.IsEnabled = false;
        try
        {
            var entries = await files.ListAsync(destination, stop.Token);
            if (remember && destination != currentPath) history.Push(currentPath);
            currentPath = destination; path.Text = destination;
            listing.ItemsSource = entries.Select(d => new ListBoxItem { Tag = d, Content = (d.Directory ? "▣  " : "▤  ") + d.Name + (d.Directory ? " /" : $"    {d.Size:N0} 字节") }).ToArray();
            status.Text = $"{entries.Count} 项 · 双击目录进入";
        }
        catch { status.Text = "无法读取目录，请检查路径或权限。"; }
        finally { busy = false; fileActions.IsEnabled = !stop.IsCancellationRequested; }
    }
    private async Task UploadAsync()
    {
        if (busy) return;
        var selected = await StorageProvider.OpenFilePickerAsync(new() { Title = "选择上传到远端当前目录的文件", AllowMultiple = false });
        if (selected.Count == 0) return;
        await TransferAsync(async ct => { await using var stream = await selected[0].OpenReadAsync(); if (!stream.CanSeek) throw new IOException(); await files.UploadAsync(currentPath, selected[0].Name, stream, stream.Length, ct); });
    }
    private async Task DownloadAsync()
    {
        if (busy || listing.SelectedItem is not ListBoxItem { Tag: RemoteFileEntry { Directory: false } file }) return;
        var destination = await StorageProvider.SaveFilePickerAsync(new() { Title = "保存远端文件", SuggestedFileName = Path.GetFileName(file.Name.Replace('\\', '/')), ShowOverwritePrompt = true });
        if (destination?.TryGetLocalPath() is not { } target) return;
        await TransferAsync(async ct =>
        {
            var temporary = Path.Combine(Path.GetDirectoryName(target)!, ".uremote-" + Guid.NewGuid().ToString("N") + ".part");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true)) await files.DownloadAsync(currentPath, file, stream, ct);
                File.Move(temporary, target, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        });
    }
    private async Task TransferAsync(Func<CancellationToken, Task> run)
    {
        if (busy) return; busy = true; fileActions.IsEnabled = false;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); transferStop = cancel;
        try { await run(cancel.Token); status.Text = "传输完成"; }
        catch (OperationCanceledException) { status.Text = "传输已取消"; }
        catch { status.Text = "传输失败，请检查权限、文件是否变化或连接状态。"; }
        finally { transferStop = null; busy = false; fileActions.IsEnabled = !stop.IsCancellationRequested; }
    }
}
