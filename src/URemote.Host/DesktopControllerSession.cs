using System.Text.Json.Nodes;
using System.Threading.Channels;
using SIPSorcery.Net;
using URemote.Core;
using URemote.Media;
namespace URemote.Host;

public sealed class DesktopControllerSession
{
    private ControllerMediaPeer? peer;
    private int display;
    public event Action<string>? Status;
    public event Action<int>? TargetPlatform;
    public event Action<string, byte[]>? DataReceived;
    public bool SendData(string channel, byte[] bytes) => peer?.SendData(channel, bytes) ?? false;
    public event Action<int, ControllerFrame>? Frame;
    public event Action<int[]>? Displays;
    public bool SendInput(string json) => peer?.SendInput(json, Volatile.Read(ref display)) ?? false;
    public void SelectDisplay(int id) { Volatile.Write(ref display, id); peer?.SelectDisplay(id); }
    public async Task RunAsync(LoginState state, string target, string ffmpeg, CancellationToken ct, int captureType = 1, AssistanceRequest? assistance = null)
    {
        using var api = new UuMacHostApi(state);
        var joined = false;
        var decoders = new Dictionary<int, ControllerVideoDecoder>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? candidatesTask = null;
        Task? clipboardTask = null;
        try
        {
            Status?.Invoke("joining");
            HostRoomConfiguration room;
            if (assistance is not null)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(45));
                var assistanceRoom = await api.JoinAssistanceAsync(assistance.ConnectId, assistance.TakeCode(), deadline.Token);
                joined = true; room = assistanceRoom.Room; TargetPlatform?.Invoke(assistanceRoom.Platform);
            }
            else { room = await api.JoinDeviceAsync(target, ct); joined = true; }
            Status?.Invoke("signaling");
            await using var signal = await UuSignalClient.ConnectAsync(room, ct);
            var appId = Guid.NewGuid().ToString(); var initialIce = Guid.NewGuid().ToString();
            var ack = await signal.EmitWithAckAsync("control", new JsonObject {
                ["app_control_id"] = appId, ["app_data"] = new JsonObject { ["_placeholder"] = true, ["num"] = 0 },
                ["streamer_data"] = ControllerProtocol.StreamerData(appId, initialIce)
            }, TimeSpan.FromSeconds(20), ct, [ControllerProtocol.ConnectOptions(state.DeviceId, captureType, assistance is not null, captureType == 1)]);
            if (ack.Packet?.Data is not JsonArray values) throw new InvalidDataException("Invalid control acknowledgement.");
            var result = values.OfType<JsonObject>().FirstOrDefault() ?? throw new InvalidDataException("Missing control result.");
            if (values.OfType<JsonValue>().Any(x => x.TryGetValue<string>(out var s) && s != "success") || (result["code"]?.GetValue<int>() ?? 0) != 0)
                throw new InvalidOperationException("UU control rejected (code " + (result["code"]?.ToJsonString() ?? "missing") + ").");
            var id = new HostPeerId(result["client_id"]?.GetValue<string>() ?? throw new InvalidDataException("Missing client id."),
                result["ice_id"]?.GetValue<string>() ?? initialIce, appId);
            var servers = new List<RTCIceServer>();
            foreach (var server in result["iceServers"]?.AsArray() ?? [])
            {
                if (server is not JsonObject o) continue;
                var urls = o["urls"] is JsonArray list ? list.Select(x => x!.GetValue<string>()) : [o["urls"]!.GetValue<string>()];
                foreach (var url in urls) servers.Add(new RTCIceServer { urls = url, username = o["username"]?.GetValue<string>(), credential = o["credential"]?.GetValue<string>() });
            }
            Status?.Invoke("ice-servers=" + servers.Count + ";relay-required=" + (result["force_relay"]?.GetValue<bool>() ?? false));
            using var media = new ControllerMediaPeer(servers, result["force_relay"]?.GetValue<bool>() ?? false, captureType != 1, captureType == 1); peer = media;
            if (captureType == 1) clipboardTask = RunClipboardAsync(media, stop.Token);
            media.DataReceived += (channel, bytes) => DataReceived?.Invoke(channel, bytes);
            var candidateQueue = Channel.CreateBounded<JsonObject>(128);
            media.LocalCandidate += c => { if (!candidateQueue.Writer.TryWrite(c)) done.TrySetException(new IOException("Too many candidates.")); };
            media.Status += s => { Status?.Invoke(s); if (s is "peer-failed" or "peer-closed" or "control-failed") done.TrySetException(new IOException("Remote connection ended.")); };
            media.ControlReady += () => { Status?.Invoke("control-ready"); if (captureType == 1) media.SelectDisplay(Volatile.Read(ref display)); };
            media.Video += (index, bytes) =>
            {
                if (stop.IsCancellationRequested || index is < 0 or > 4) return;
                lock (decoders)
                {
                    if (!decoders.TryGetValue(index, out var decoder))
                    {
                        try { decoder = new ControllerVideoDecoder(ffmpeg); }
                        catch { done.TrySetException(new IOException("Could not start video decoder.")); return; }
                        decoders[index] = decoder; Status?.Invoke("video-stream-received");
                        decoder.Failed += () => done.TrySetException(new IOException("Video decoder stopped."));
                        var firstFrame = true;
                        decoder.Frame += frame => { if (firstFrame) { firstFrame = false; Status?.Invoke("video-decoded"); } if (!stop.IsCancellationRequested) Frame?.Invoke(index, frame); };
                        Displays?.Invoke(decoders.Keys.Order().ToArray());
                    }
                    decoder.Push(bytes);
                }
            };
            Status?.Invoke("negotiating");
            var offer = await media.OfferAsync(ct);
            var (payload, binary) = HostSignalSessions.BuildAnswer(id, offer);
            payload["data"]!["type"] = "offer";
            payload["data"]!["ice_network_type"] = 3; // Automatic network selection, as used by the controller protocol.
            await signal.SendEventAsync("soac", payload, [binary], ct: ct);
            candidatesTask = Task.Run(async () => {
                try { await foreach (var c in candidateQueue.Reader.ReadAllAsync(stop.Token))
                    await signal.SendEventAsync("soac", new JsonObject { ["client_id"] = id.ClientId, ["data"] = new JsonObject {
                        ["type"] = "candidate", ["ice_id"] = id.IceId, ["app_control_id"] = id.AppControlId, ["candidate"] = c } }, ct: stop.Token); }
                catch (OperationCanceledException) { } catch { done.TrySetException(new IOException("Candidate signaling failed.")); }
            }, CancellationToken.None);
            var completionNotified = false;
            var connectedAt = DateTime.UtcNow;
            var receivedVideo = false;
            void MarkFrame(int _, ControllerFrame __) => receivedVideo = true;
            Frame += MarkFrame;
            try
            {
                var available = signal.Events.WaitToReadAsync(ct).AsTask();
                while (!ct.IsCancellationRequested)
                {
                    await Task.WhenAny(available, done.Task, Task.Delay(1000, ct));
                    ct.ThrowIfCancellationRequested();
                    if (done.Task.IsCompleted) await done.Task;
                    if (captureType == 1 && !receivedVideo && DateTime.UtcNow - connectedAt > TimeSpan.FromSeconds(45)) throw new TimeoutException("No remote video received.");
                    if (!completionNotified && media.CanControl)
                    {
                        await signal.SendEventAsync("ice_finished_log", new JsonObject { ["app_control_id"] = id.AppControlId, ["ice_id"] = id.IceId }, ct: ct);
                        completionNotified = true; Status?.Invoke("ice-completion-notified");
                    }
                    if (!available.IsCompleted) continue;
                    if (!await available) throw new IOException("Signaling ended.");
                    while (signal.Events.TryRead(out var item))
                    {
                        if (item.Packet?.Data is not JsonArray parts || parts.Count < 2 || parts[0]?.GetValue<string>() != "soac" || parts[1] is not JsonObject p || p["data"] is not JsonObject data) continue;
                        if (data["app_control_id"]?.GetValue<string>() is { } aid && aid != appId || data["ice_id"]?.GetValue<string>() is { } ice && ice != id.IceId) continue;
                        switch (data["type"]?.GetValue<string>())
                        {
                            case "answer": media.ApplyAnswer(data["gzip_sdp"] is not null ? UuSignalReader.DecodeGzipSdp(item) : data["sdp"]!.GetValue<string>()); Status?.Invoke("answer-received"); break;
                            case "candidate": if (data["candidate"] is JsonObject c) media.AddCandidate(c); break;
                        }
                    }
                    available = signal.Events.WaitToReadAsync(ct).AsTask();
                }
            }
            finally { Frame -= MarkFrame; stop.Cancel(); if (candidatesTask is not null) await candidatesTask; }
        }
        finally
        {
            stop.Cancel(); peer = null; assistance?.TakeCode();
            if (clipboardTask is not null) await clipboardTask;
            foreach (var decoder in decoders.Values) await decoder.DisposeAsync();
            if (joined) { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); try { if (assistance is not null) await api.CancelAssistanceAsync(assistance.ConnectId, deadline.Token); else await api.LeaveDeviceAsync(target, deadline.Token); } catch { } }
            Status?.Invoke("closed");
        }
    }

    private async Task RunClipboardAsync(ControllerMediaPeer media, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var clipboard = new HostClipboard(media.SendData, value => Status?.Invoke(value));
        var messages = Channel.CreateBounded<PeerDataMessage>(64);
        void Receive(string label, byte[] bytes)
        {
            if (ct.IsCancellationRequested || label is not ("TEXT_DATA_CHANNEL" or "FILE_DATA_CHANNEL" or "CONTROL_DATA_CHANNEL")
                || bytes.Length is 0 or > 131072 || bytes[0] == (byte)'{') return;
            var copy = bytes.ToArray();
            if (!messages.Writer.TryWrite(new(label, false, copy))) Array.Clear(copy);
        }
        media.DataReceived += Receive;
        var watch = clipboard.WatchAsync(lifetime.Token);
        try
        {
            await foreach (var message in messages.Reader.ReadAllAsync(ct))
            {
                try { await clipboard.HandleAsync(message, ct); }
                catch (Exception e) when (e is FormatException or ArgumentException or IOException or System.ComponentModel.Win32Exception)
                { Status?.Invoke("clipboard-unavailable;stage=receive;error=" + e.GetType().Name); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { Status?.Invoke("clipboard-unavailable"); }
                finally { Array.Clear(message.Bytes); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            media.DataReceived -= Receive;
            messages.Writer.TryComplete();
            lifetime.Cancel();
            try { await watch; } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            while (messages.Reader.TryRead(out var message)) Array.Clear(message.Bytes);
        }
    }
}
