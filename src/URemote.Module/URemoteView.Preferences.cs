using System.Text.Json;
using Avalonia.Controls;
using URemote.Host;
using URemote.Linux;

namespace URemote.Module;

public sealed partial class URemoteView
{
    internal sealed record Settings(string Identity, string Encoder, bool AllowAssistance = true, bool HostEnabled = true,
        bool Input = true, bool Audio = true, bool Clipboard = true, int DurationMinutes = 0, string[]? DisplayKeys = null);
    private string[]? rememberedDisplayKeys;
    private bool preferencesLoadFailed;
    private void DisableUnreadablePreferences()
    {
        preferencesLoadFailed = true; hostEnabled = false; allowAssistance = false;
        input.IsChecked = audio.IsChecked = clipboard.IsChecked = false;
        rememberedDisplayKeys = [];
    }
    private bool replacingScreens;
    private HostSessionOptions hostOptions = new(true, true, true, Timeout.InfiniteTimeSpan, null);

    private Settings CurrentSettings(string? encoderOverride = null, bool? assistanceOverride = null) => new(
        identity.Text ?? "", encoderOverride ?? appliedEncoder, assistanceOverride ?? allowAssistance, hostEnabled,
        input.IsChecked == true, audio.IsChecked == true, clipboard.IsChecked == true,
        duration.SelectedIndex switch { 1 => 5, 2 => 30, 3 => 60, _ => 0 }, rememberedDisplayKeys?.ToArray());

    private void SaveSettings(string? encoderOverride = null, bool? assistanceOverride = null)
    {
        var value = CurrentSettings(encoderOverride, assistanceOverride);
        var temporary = settingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, settingsFile, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        var limit = value.DurationMinutes == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(value.DurationMinutes);
        Volatile.Write(ref hostOptions, new(value.Input, value.Audio, value.Clipboard, limit, value.DisplayKeys));
    }

    private void WireHostPreferences()
    {
        void Changed()
        {
            if (replacingScreens || !settings.IsEnabled) return;
            try { SaveSettings(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { detail.Text = "共享设置未能保存，请检查设置目录权限。"; ReportFailure(detail.Text, "共享设置"); }
        }
        foreach (var option in new[] { input, audio, clipboard })
            option.PropertyChanged += (_, e) => { if (e.Property == CheckBox.IsCheckedProperty) Changed(); };
        duration.SelectionChanged += (_, _) => Changed();
    }

    private void RememberScreenSelection()
    {
        if (replacingScreens || !settings.IsEnabled) return;
        var present = outputs.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        rememberedDisplayKeys = (rememberedDisplayKeys ?? []).Where(key => !present.Contains(key))
            .Concat(outputs.Where(x => x.IsChecked == true).Select(x => x.Key)).Distinct(StringComparer.Ordinal).ToArray();
        SaveSettings();
    }

    private void ReplaceScreens(IReadOnlyList<DesktopOutput> displays, string? emptyHint)
    {
        replacingScreens = true;
        try
        {
            outputs.Clear();
            foreach (var display in displays.Take(5))
            {
                var model = new ScreenShareModel { Id = display.Id, Key = display.Key,
                    Label = "显示屏 " + (outputs.Count + 1) + " · " + display.Name,
                    IsChecked = rememberedDisplayKeys is null || rememberedDisplayKeys.Contains(display.Key, StringComparer.Ordinal) };
                model.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(ScreenShareModel.IsChecked)) return;
                    try { RememberScreenSelection(); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { detail.Text = "屏幕选择未能保存。"; ReportFailure(detail.Text, "共享设置"); }
                };
                outputs.Add(model);
            }
            if (rememberedDisplayKeys is null && outputs.Count > 0)
                rememberedDisplayKeys = outputs.Select(x => x.Key).ToArray();
            if (!string.IsNullOrEmpty(emptyHint)) screenHint.Text = emptyHint;
            screenHint.IsVisible = displays.Count == 0 && !string.IsNullOrEmpty(emptyHint);
        }
        finally { replacingScreens = false; }
    }
}
