using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace URemote.Module;

public sealed partial class URemoteView
{
    private string appliedEncoder = "";
    private readonly TextBlock encoderHint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button detectEncoder = new() { Content = "自动检测" };
    private readonly Button applyEncoder = new() { Content = "验证并应用" };

    private void BuildEncoderSettings()
    {
        advancedSettings.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { detectEncoder, applyEncoder }
        });
        advancedSettings.Children.Add(encoderHint);
        encoderHint.Text = "填写 FFmpeg 完整路径并验证。应用后用于新连接，当前连接不受影响。";
        detectEncoder.Click += (_, _) =>
        {
            var configured = Environment.GetEnvironmentVariable("UREMOTE_FFMPEG");
            var found = Path.IsPathFullyQualified(configured ?? "") && File.Exists(configured)
                ? configured : FindExecutable("ffmpeg");
            if (string.IsNullOrEmpty(found))
            {
                encoderHint.Text = "未找到 FFmpeg，请手动填写可执行文件的完整路径。";
                return;
            }
            encoder.Text = found;
            encoderHint.Text = "已找到路径，请点击“验证并应用”。";
        };
        applyEncoder.Click += async (_, _) => await ApplyEncoderAsync();
    }

    private async Task ApplyEncoderAsync()
    {
        var candidate = (encoder.Text ?? "").Trim();
        if (!Path.IsPathFullyQualified(candidate) || !File.Exists(candidate))
        {
            encoderHint.Text = "请输入存在的 FFmpeg 可执行文件完整路径。";
            return;
        }
        applyEncoder.IsEnabled = detectEncoder.IsEnabled = encoder.IsEnabled = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(candidate)
                {
                    UseShellExecute = false, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true,
                    ArgumentList = { "-version" }
                }
            };
            process.Start();
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var output = await stdout;
                await stderr;
                if (process.ExitCode != 0 || !output.StartsWith("ffmpeg version ", StringComparison.OrdinalIgnoreCase))
                {
                    encoderHint.Text = "验证失败：该文件未返回有效的 FFmpeg 版本信息。";
                    return;
                }
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            if (disposed) return;
            // Persist before publishing the new path to the background host.
            File.WriteAllText(settingsFile, JsonSerializer.Serialize(
                new Settings(identity.Text ?? "", candidate, allowAssistance, hostEnabled)));
            Volatile.Write(ref appliedEncoder, candidate);
            encoder.Text = candidate;
            encoderHint.Text = "验证成功，路径已保存。新连接使用此路径，当前连接保持不变。";
        }
        catch (OperationCanceledException)
        {
            if (!disposed) encoderHint.Text = "验证超时，原配置保持不变。";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            encoderHint.Text = "无法执行 FFmpeg 或保存设置，请检查文件权限与路径。原配置保持不变。";
        }
        finally
        {
            if (!disposed) applyEncoder.IsEnabled = detectEncoder.IsEnabled = encoder.IsEnabled = true;
        }
    }
}
