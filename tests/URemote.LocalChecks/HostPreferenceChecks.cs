using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using URemote.Linux;
using URemote.Module;
using URemote.Host;

internal static class HostPreferenceChecks
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Call(URemoteView view, string method, params object?[] args) => typeof(URemoteView).GetMethod(method, Flags)!.Invoke(view, args);
    private static T Field<T>(URemoteView view, string name) => (T)typeof(URemoteView).GetField(name, Flags)!.GetValue(view)!;
    private sealed class TestApp : Application { }
    public static void Run(Action<bool, string> check)
    {
        AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var root = Path.Combine(Path.GetTempPath(), "uremote-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var originalIdentity = Environment.GetEnvironmentVariable("UREMOTE_IDENTITY");
        Environment.SetEnvironmentVariable("UREMOTE_IDENTITY", Path.Combine(root, "identity.json"));
        try
        {
            var path = Path.Combine(root, "settings.json");
            using (var view = new URemoteView(root, startSession: false))
            {
                Call(view, "ReplaceScreens", new DesktopOutput[] { new(11,"wayland:DP-1","DP-1"), new(22,"wayland:DP-2","DP-2") }, null);
                Field<System.Collections.ObjectModel.ObservableCollection<ScreenShareModel>>(view,"outputs")[1].IsChecked = false;
                view.FindControl<CheckBox>("input")!.IsChecked = false;
                view.FindControl<CheckBox>("audio")!.IsChecked = false;
                view.FindControl<CheckBox>("clipboard")!.IsChecked = false;
                view.FindControl<ComboBox>("duration")!.SelectedIndex = 1;
                var presence = new TaskCompletionSource();
                typeof(URemoteView).GetField("session",Flags)!.SetValue(view,presence.Task);
                try
                {
                    Call(view,"Stop");
                    check(view.FindControl<StackPanel>("settings")!.IsEnabled,"stopping control unlocks settings while presence remains running");
                    Call(view,"Start");
                    var options = Field<HostSessionOptions>(view,"hostOptions");
                    check(!options.Input && !options.Audio && !options.Clipboard && options.Duration == TimeSpan.FromMinutes(5),"reenabling publishes edited permissions to the existing online session");
                    check(options.DisplayKeys!.SequenceEqual(new[]{"wayland:DP-1"}),"explicit screen selection is saved by identity");
                    check(!view.FindControl<StackPanel>("settings")!.IsEnabled && !presence.Task.IsCompleted,"control settings lock again without stopping presence");
                    File.Move(path,path+".backup");Directory.CreateDirectory(path);
                    try
                    {
                        Call(view,"Stop");Call(view,"Start");
                        check(!Field<URemote.Core.HostAdmission>(view,"admission").Enabled && view.FindControl<ToggleSwitch>("hostSwitch")!.IsChecked==false,
                            "failed settings save cannot enable control with an older permission snapshot");
                    }
                    finally {Directory.Delete(path);File.Move(path+".backup",path);}

                }
                finally { presence.TrySetResult(); }
            }
            using (var reloaded = new URemoteView(root,startSession:false))
            {
                check(reloaded.FindControl<CheckBox>("input")!.IsChecked == false && reloaded.FindControl<CheckBox>("audio")!.IsChecked == false
                    && reloaded.FindControl<CheckBox>("clipboard")!.IsChecked == false && reloaded.FindControl<ComboBox>("duration")!.SelectedIndex == 1,"permissions and duration survive reopening the module");
                Call(reloaded,"ReplaceScreens",new DesktopOutput[]{new(99,"wayland:DP-2","DP-2")},null);
                var output=Field<System.Collections.ObjectModel.ObservableCollection<ScreenShareModel>>(reloaded,"outputs").Single();
                check(output.IsChecked == false,"removed first screen never authorizes the replacement at ordinal zero");
                Call(reloaded,"SaveSettings",null,null);
                var saved=JsonSerializer.Deserialize<URemoteView.Settings>(File.ReadAllText(path))!;
                check(saved.DisplayKeys!.SequenceEqual(new[]{"wayland:DP-1"}),"missing screen selection is retained for later recovery");
            }
            var legacy=JsonSerializer.Deserialize<URemoteView.Settings>("{\"Identity\":\"identity.json\",\"Encoder\":\"ffmpeg\",\"HostEnabled\":false}")!;
            check(legacy.Input && legacy.Audio && legacy.Clipboard && !legacy.HostEnabled && legacy.DisplayKeys is null,"legacy settings preserve existing permission defaults and host switch");
            check(!Directory.EnumerateFiles(root,"*.tmp").Any(),"atomic settings updates leave no temporary files");
            File.WriteAllText(path,"{invalid-json");
            using(var invalid=new URemoteView(root,startSession:false))
                check(!Field<bool>(invalid,"hostEnabled") && invalid.FindControl<CheckBox>("input")!.IsChecked==false
                    && invalid.FindControl<CheckBox>("audio")!.IsChecked==false && invalid.FindControl<CheckBox>("clipboard")!.IsChecked==false,
                    "unreadable settings fail closed rather than restoring broad default permissions");

        }
        finally { Environment.SetEnvironmentVariable("UREMOTE_IDENTITY",originalIdentity);Directory.Delete(root,true); }
    }
}
