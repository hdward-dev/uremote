using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using URemote.Core;
using URemote.Host;
namespace URemote.Module;
public sealed partial class URemoteView
{
    private readonly SelectableTextBlock assistanceId = new() { Text = "正在获取…", FontSize = 26, FontWeight = Avalonia.Media.FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox assistanceCode = new() { IsReadOnly = true, PasswordChar = '●', FontSize = 18 };
    private readonly TextBlock assistanceHint = new() { Text = "获取本机协助信息后，可在官方客户端输入协助码连接。", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private string? assistanceDevice;
    private bool allowAssistance = true;
    private readonly ToggleSwitch assistanceSwitch = new() { OnContent = null, OffContent = null };
    private Control? assistanceCredentials;
    private AssistanceCodeSettings? codeSettings;
    private readonly ComboBox codeMode = new() { ItemsSource = new[] { "临时验证码", "自定义验证码" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private bool changingCodeMode;
    private Action? updateCodeModeUi;
    private string AssistanceSettingsFile => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settingsFile)!, "assistance-secret.json");
    private void LoadAssistanceSettings()
    {
        string device;
        try { device = DesktopHostSession.ReadIdentity(identity.Text ?? "").State.DeviceId; }
        catch { return; }
        try
        {
            codeSettings = AssistanceCodeSettings.Load(AssistanceSettingsFile, device);
            HostAssistance.Configure(device, codeSettings.CustomCode, codeSettings.UseCustom);
        }
        catch
        {
            allowAssistance = false; assistanceSwitch.IsChecked = false; HostAssistance.SetEnabled(device, false);
            assistanceHint.Text = "验证码设置无法读取，已关闭远程协助。请检查本机设置文件权限。";
        }
    }
    private void SaveCodeSettings(string custom, bool useCustom)
    {
        var device = DesktopHostSession.ReadIdentity(identity.Text ?? "").State.DeviceId;
        var value = new AssistanceCodeSettings(device, custom, useCustom);
        value.Save(AssistanceSettingsFile);
        HostAssistance.Configure(device, custom, useCustom);
        codeSettings = value;
        assistanceDevice = device;
        assistanceCode.PasswordChar = '●';
        UpdateAssistanceState();
    }
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
        ApplyTheme(assistanceId, TextBlock.ForegroundProperty, "AppStrongTextBrush");
        var copyIcon = new Avalonia.Controls.Shapes.Path {
            Data = Avalonia.Media.Geometry.Parse("M8,7 H19 V21 H8 Z M5,17 H3 V3 H14 V5"),
            Width = 18, Height = 18, Stretch = Avalonia.Media.Stretch.Uniform, StrokeThickness = 1.5 };
        ApplyTheme(copyIcon, Avalonia.Controls.Shapes.Shape.StrokeProperty, "AppStrongTextBrush");
        var copyId = new Button { Content = copyIcon, Width = 34, Height = 34,
            Padding = new Thickness(8), Background = Avalonia.Media.Brushes.Transparent, BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(copyId, "复制协助码");
        Avalonia.Automation.AutomationProperties.SetName(copyId, "复制协助码");
        var assistanceRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        assistanceRow.Children.Add(assistanceId); Grid.SetColumn(copyId, 1); assistanceRow.Children.Add(copyId);
        Button IconButton(string label, string geometry)
        {
            var icon = new Avalonia.Controls.Shapes.Path { Data = Avalonia.Media.Geometry.Parse(geometry),
                Width = 17, Height = 17, Stretch = Avalonia.Media.Stretch.Uniform, StrokeThickness = 1.5 };
            ApplyTheme(icon, Avalonia.Controls.Shapes.Shape.StrokeProperty, "AppStrongTextBrush");
            var button = new Button { Content = icon, Width = 30, Height = 34, Padding = new Thickness(6),
                Background = Avalonia.Media.Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(button, label); Avalonia.Automation.AutomationProperties.SetName(button, label);
            return button;
        }
        const string eye = "M2,12 C6,4 18,4 22,12 C18,20 6,20 2,12 Z M15,12 A3,3 0 1 1 9,12 A3,3 0 1 1 15,12";
        var copyCode = IconButton("复制验证码", "M8,7 H19 V21 H8 Z M5,17 H3 V3 H14 V5");
        var reveal = IconButton("显示验证码", eye);
        var rotate = IconButton("更换验证码", "M20,9 A8,8 0 1 0 20,16 M20,3 V9 H14");
        void UpdateRevealIcon()
        {
            var visible = assistanceCode.PasswordChar == '\0';
            ((Avalonia.Controls.Shapes.Path)reveal.Content!).Data = Avalonia.Media.Geometry.Parse(eye + (visible ? " M3,3 L21,21" : ""));
            var label = visible ? "隐藏验证码" : "显示验证码";
            ToolTip.SetTip(reveal, label); Avalonia.Automation.AutomationProperties.SetName(reveal, label);
        }
        assistanceCode.PropertyChanged += (_, e) => { if (e.Property == TextBox.PasswordCharProperty) UpdateRevealIcon(); };
        UpdateRevealIcon();
        var codeRow = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto"), ColumnSpacing = 2 };
        assistanceCode.MinWidth = 0; assistanceCode.VerticalAlignment = VerticalAlignment.Center;
        codeRow.Children.Add(assistanceCode);
        Grid.SetColumn(reveal, 1); codeRow.Children.Add(reveal);
        Grid.SetColumn(copyCode, 3); codeRow.Children.Add(copyCode);
        Grid.SetColumn(rotate, 2); codeRow.Children.Add(rotate);
        var customInput = new TextBox { PasswordChar = '●', MaxLength = 32, PlaceholderText = "8–32 位，包含字母和数字" };
        var customConfirm = new TextBox { PasswordChar = '●', MaxLength = 32, PlaceholderText = "再次输入验证码" };
        var saveCustom = new Button { Content = "保存" };
        var cancelCustom = new Button { Content = "取消" };
        var removeCustom = new Button { Content = "清除自定义码" };
        var customEditor = new StackPanel { IsVisible = false, Spacing = 8, Children = {
            Text("设置自定义验证码", 13), customInput, customConfirm,
            new WrapPanel { Children = { saveCustom, cancelCustom, removeCustom } },
            Text("仅保存在本机私有文件中，重启后保留。", 11, true) } };
        void OpenEditor() { customInput.Text = customConfirm.Text = ""; customEditor.IsVisible = true; customInput.Focus(); }
        void CloseEditor() { customInput.Text = customConfirm.Text = ""; customEditor.IsVisible = false; }
        saveCustom.Click += (_, _) => {
            try {
                var value = customInput.Text ?? "";
                if (value != customConfirm.Text) { assistanceHint.Text = "两次输入不一致。"; return; }
                HostAssistance.ValidateCustomCode(value);
                SaveCodeSettings(value, true); CloseEditor();
            }
            catch (ArgumentException) { assistanceHint.Text = "需为 8–32 位，包含字母和数字，不含空格。"; }
            catch { assistanceHint.Text = "自定义验证码未保存，请检查本机文件权限。"; }
        };
        cancelCustom.Click += (_, _) => CloseEditor();
        removeCustom.Click += (_, _) => { try { SaveCodeSettings("", false); CloseEditor(); } catch { assistanceHint.Text = "未能清除自定义码，请重试。"; } };
        codeMode.SelectionChanged += (_, _) => {
            if (changingCodeMode) return;
            var useCustom = codeMode.SelectedIndex == 1;
            if (useCustom && string.IsNullOrEmpty(codeSettings?.CustomCode)) {
                changingCodeMode = true; codeMode.SelectedIndex = 0; changingCodeMode = false;
                OpenEditor(); assistanceHint.Text = "保存后两种验证码均可连接；下拉框仅切换显示。"; return;
            }
            try { SaveCodeSettings(codeSettings?.CustomCode ?? "", useCustom); CloseEditor(); }
            catch { updateCodeModeUi?.Invoke(); assistanceHint.Text = "验证码类型未能保存，请重试。"; }
        };
        updateCodeModeUi = () => {
            changingCodeMode = true; codeMode.SelectedIndex = codeSettings?.UseCustom == true ? 1 : 0; changingCodeMode = false;
            var custom = codeSettings?.UseCustom == true;
            ((Avalonia.Controls.Shapes.Path)rotate.Content!).Data = Avalonia.Media.Geometry.Parse(custom
                ? "M4,16 L15,5 L19,9 L8,20 H4 Z M14,6 L18,10" : "M20,9 A8,8 0 1 0 20,16 M20,3 V9 H14");
            ToolTip.SetTip(rotate, custom ? "修改自定义验证码" : "刷新临时验证码");
            Avalonia.Automation.AutomationProperties.SetName(rotate, custom ? "修改自定义验证码" : "刷新临时验证码");
            removeCustom.IsVisible = !string.IsNullOrEmpty(codeSettings?.CustomCode);
        };
        updateCodeModeUi();
        var reload = new Button { Content = "重新获取" };
        copyId.Click += async (_, _) => await CopyAssistanceAsync(assistanceId.Text);
        copyCode.Click += async (_, _) => await CopyAssistanceAsync(assistanceCode.Text);
        reveal.Click += (_, _) => { var hide = assistanceCode.PasswordChar == '\0'; assistanceCode.PasswordChar = hide ? '●' : '\0';  };
        rotate.Click += (_, _) => {
            if (codeSettings?.UseCustom == true) { OpenEditor(); return; }
            if (assistanceDevice is { } device) { HostAssistance.Rotate(device); UpdateAssistanceState(); }
        };
        reload.Click += async (_, _) => await RefreshAssistanceAsync();
        var columns = new StackPanel { Spacing = 12, Children = {
            Text("协助码", 12, true), assistanceRow,
            codeMode, codeRow, customEditor
        } };
        assistanceCredentials = columns; columns.IsEnabled = allowAssistance;
        reload.FontSize = 11; reload.Padding = new Thickness(8, 5);
        assistanceSwitch.MinWidth = 0;
        assistanceHint.FontSize = 12;
        return new StackPanel { Spacing = 12, Children = { heading, columns, assistanceHint, reload } };
    }
    private void UpdateAssistanceState()
    {
        if(assistanceCredentials is not null) assistanceCredentials.IsEnabled = allowAssistance;
        assistanceCode.Text = allowAssistance && assistanceDevice is { } device ? HostAssistance.DisplayCode(device) : "";
        if(!allowAssistance) assistanceCode.PasswordChar = '●';
        updateCodeModeUi?.Invoke();
        assistanceHint.Text = !allowAssistance ? "远程协助已关闭，同账号设备被控不受影响。"
            : !string.IsNullOrEmpty(codeSettings?.CustomCode) ? "两种验证码均可连接，下拉框仅切换显示。自定义码持续有效；临时码在协助连接成功后更新。"
            : "临时码会在协助连接成功后更新，也可手动刷新。";
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
            if (codeSettings?.DeviceId != state.DeviceId) LoadAssistanceSettings();
            assistanceDevice = state.DeviceId; assistanceId.Text = info.ConnectId;
            HostAssistance.SetEnabled(state.DeviceId, allowAssistance);
            UpdateAssistanceState();
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
        catch { if(!disposed) { assistanceDevice = null; assistanceId.Text = "未获取"; assistanceCode.Text = ""; assistanceHint.Text = "协助信息获取失败，请检查登录状态后重新获取。"; } }
    }
}
