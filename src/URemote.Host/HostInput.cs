using System.Text.Json;
using SIPSorcery.Net;
using URemote.Core;
using URemote.Linux;
using URemote.Media;
using URemote.Host;

internal static class HostInput
{
    public static async Task RunAsync(HostMediaPeer media, IReadOnlyList<uint> outputs, CancellationToken ct, Action<string> report, bool enableInput = true, bool enableClipboard = false, HostVideoSettings? profiles = null, bool enableFiles = true, Action? inputApplied = null)
    {
        while (media.State != RTCPeerConnectionState.connected) await Task.Delay(100, ct);
        await using var fileTransfer = enableFiles ? new HostFileTransfer(media.SendData, report, ct) : null;
        await using var keyboard = enableInput ? await WaylandVirtualKeyboard.CreateAsync(ct) : null;
        await using var clipboard = enableClipboard ? new HostClipboard(media, report) : null;
        using var clipboardStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watch = clipboard?.WatchAsync(clipboardStop.Token) ?? Task.CompletedTask;
        var displays = new List<(uint Width, uint Height, WaylandVirtualPointer Pointer)>();
        try
        {
        foreach (var output in enableInput ? outputs : Array.Empty<uint>())
        {
            var screen = await WaylandScreenCapture.CaptureAsync(output, ct: ct);
            Array.Clear(screen.Pixels);
            var pointer = await WaylandVirtualPointer.CreateAsync(output, ct);
            displays.Add((screen.Width, screen.Height, pointer));
        }
        var count = 0;
        ulong sequence = 0;
        var observed = new HashSet<string>();
        await foreach (var message in media.DataMessages.ReadAllAsync(ct))
        {
            try
            {
                var category = message.ChannelLabel switch
                { "CONTROL_DATA_CHANNEL" => "control", "TEXT_DATA_CHANNEL" => "text", "STREAMER_DATA_CHANNEL" => "streamer",
                    "FILE_DATA_CHANNEL" => "file", "BINARY_DATA_CHANNEL" => "binary", _ => "other" };
                var signature = category + (message.IsText ? "-text" : "-binary");
                if (observed.Add(signature)) report("input-channel-received=" + signature);
                var firstShape = ControlPacketShape.Describe(message.IsText, message.Bytes);
                if (observed.Count < 64 && observed.Add(category + ":" + firstShape)) report("channel-shape=" + category + ";" + firstShape);
                if (fileTransfer is not null && await fileTransfer.HandleAsync(message)) continue;
                if (category == "control" && !message.IsText && HostControlEcho.Reply(message.Bytes, ++sequence,
                    (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), enableInput, enableClipboard, enableFiles) is { } reply)
                {
                    if (media.SendControl(reply) && observed.Add("echo")) {
                        report("control-echo-replied");
                        if (enableFiles) media.SendControl(FileTransferProtocol.Join(FileTransferProtocol.Int(1, ++sequence),
                            FileTransferProtocol.Blob(26, FileTransferProtocol.Text(1, "/"))));
                    }
                    continue;
                }
                if (category is "text" or "control" && profiles is not null && HostCaptureProtocol.Decode(message.Bytes) is { } update)
                {
                    var accepted = profiles.Apply(update);
                    report("capture-values=" + HostCaptureProtocol.DescribeSettings(message.Bytes));
                    report($"capture-request;screen={update.Screen};width={update.Width};height={update.Height};fps={update.Fps};quality={update.Quality};accepted={accepted}");
                    media.SendData(message.ChannelLabel, HostCaptureProtocol.Reply(update, accepted));
                    report(accepted ? "capture-settings-applied" : "capture-settings-rejected");
                    continue;
                }
                if (clipboard is not null && await clipboard.HandleAsync(message, ct)) continue;
                if (!enableInput) continue;
                var input = HostControlInput.Decode(message.ChannelLabel, message.IsText, message.Bytes, outputs.Count);
                if (input?.Mouse is { } mouse)
                {
                    var displayId = profiles?.SingleVideoStream == true ? profiles.SelectedScreen : input.DisplayId;
                    var screen = displays[displayId];
                    var pointer = screen.Pointer;
                    if (mouse.Action == MouseAction.MoveAbsolute)
                    {
                        // Undo the encoder's centered letterbox before mapping to the native output.
                        var profile = profiles?.Get(displayId);
                        var encodedWidth = profile?.Width ?? 1280; var encodedHeight = profile?.Height ?? 720;
                        var scale = Math.Min((double)encodedWidth / screen.Width, (double)encodedHeight / screen.Height);
                        var w = Math.Floor(screen.Width * scale / 2) * 2;
                        var h = Math.Floor(screen.Height * scale / 2) * 2;
                        var x = (mouse.X * encodedWidth - (encodedWidth - w) / 2) / w;
                        var y = (mouse.Y * encodedHeight - (encodedHeight - h) / 2) / h;
                        if (x < 0 || x > 1 || y < 0 || y > 1) continue;
                        mouse = mouse with { X = x, Y = y };
                    }
                    await pointer.ApplyAsync(mouse, PointerCoordinates.MacNormalized, screen.Width, screen.Height, ct);
                }
                else if (input?.Key is { } key) await keyboard!.ApplyAsync(key, ct);
                else { if (observed.Add("ignored")) report("input-envelope-ignored"); continue; }
                if (++count == 1) inputApplied?.Invoke();
                if (count == 1 || count % 100 == 0) report("input-events-applied=" + count);
            }
            catch (Exception e) when (e is FormatException or JsonException or ArgumentException or KeyNotFoundException)
            { if (observed.Add("malformed"))
                {
                    report("input-envelope-rejected=" + e.GetType().Name);
                    report("input-failure-site=" + string.Join(",", new System.Diagnostics.StackTrace(e).GetFrames().Take(3)
                        .Select(f => f.GetMethod()?.DeclaringType?.Name + "." + f.GetMethod()?.Name)));
                } }
            finally { Array.Clear(message.Bytes); }
        }
        }
        finally
        {
            clipboardStop.Cancel();
            try { await watch; } catch (OperationCanceledException) when (clipboardStop.IsCancellationRequested) { }
            foreach (var display in displays) await display.Pointer.DisposeAsync();
        }
    }
}
