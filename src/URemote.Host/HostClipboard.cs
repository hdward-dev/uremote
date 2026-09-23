using System.Text;
using URemote.Core;
using URemote.Linux;
using URemote.Media;
internal sealed class HostClipboard : IAsyncDisposable
{
    private readonly IAsyncDisposable? clipboardOwner;
    private readonly Func<string, byte[], bool> send;
    private readonly Func<CancellationToken, Task<string?>> read;
    private readonly Func<string, CancellationToken, Task> write;
    private readonly Action<string> report;
    private string? lastText;
    private readonly SemaphoreSlim gate = new(1);
    private long sequence = 100000;
    private string? pendingKey;
    private ulong pendingId;
    private int pendingFormat, expectedBlocks = -1;
    private DateTimeOffset expiry;
    private ulong pendingTextId;
    private DateTimeOffset textAckDeadline;
    private readonly SortedDictionary<int, byte[]> blocks = new();
    public HostClipboard(HostMediaPeer peer, Action<string> report) : this(peer.SendData, report) { }
    public HostClipboard(Func<string, byte[], bool> send, Action<string> report)
    {
        var clipboard = new WaylandClipboard(); clipboardOwner = clipboard;
        this.send = send; this.report = report; read = clipboard.ReadAsync; write = clipboard.WriteAsync;
    }
    internal HostClipboard(Func<string, byte[], bool> send, Action<string> report,
        Func<CancellationToken, Task<string?>> read, Func<string, CancellationToken, Task> write)
    { this.send = send; this.report = report; this.read = read; this.write = write; }
    public async Task<bool> HandleAsync(PeerDataMessage message, CancellationToken ct)
    {
        if (message.Bytes.Length == 0 || message.Bytes[0] == (byte)'{') return false;
        if (message.ChannelLabel is not ("TEXT_DATA_CHANNEL" or "FILE_DATA_CHANNEL" or "CONTROL_DATA_CHANNEL")) return false;
        if (HostClipboardTransfers.Decode(message.Bytes) is { } transfer)
        {
            await gate.WaitAsync(ct);
            try { report("clipboard-transfer=" + transfer.Kind); await TransferAsync(transfer, ct); return true; }
            finally { Array.Clear(transfer.Data); gate.Release(); }
        }
        var request = HostClipboardProtocol.Decode(message.Bytes);
        if (request is null) return false;
        await gate.WaitAsync(ct);
        try
        {
        if (request.IsRead)
        {
            report($"clipboard-read-request;format={request.Format}");
            string? text;
            try { text = await read(ct); }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or OperationCanceledException && !ct.IsCancellationRequested)
            { text = null; report("clipboard-unavailable"); }
            foreach (var response in HostClipboardProtocol.ReadResponse(request, text))
                try { report($"clipboard-response;channel={response.Channel};sent={send(response.Channel, response.Data)}"); } finally { Array.Clear(response.Data); }
        }
        else
        {
            var success = request.Format is 1 or 13;
            if (success)
            {
                try { await write(request.Text, ct); lastText = request.Text; pendingTextId = 0; report("clipboard-received"); }
                catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
                { success = false; report("clipboard-unavailable"); }
            }
            var response = HostClipboardProtocol.Acknowledge(request.RequestId, success);
            send("TEXT_DATA_CHANNEL", response);
        }
        return true;
        }
        finally { gate.Release(); }
    }
    public async Task WatchAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(750));
        while (await timer.WaitForNextTickAsync(ct))
        {
            await gate.WaitAsync(ct);
            try
            {
                if (pendingTextId != 0 && DateTimeOffset.UtcNow >= textAckDeadline) AdvertiseFormats();
                var text = await read(ct);
                if (text is null || text == lastText) continue;
                var id = (ulong)Interlocked.Increment(ref sequence);
                var packet = HostClipboardProtocol.Change(id, text);
                try { if (send("TEXT_DATA_CHANNEL", packet))
                    {
                        pendingTextId = id; textAckDeadline = DateTimeOffset.UtcNow.AddSeconds(2);
                        lastText = text; report("clipboard-sent");
                    } }
                finally { Array.Clear(packet); }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or System.Text.DecoderFallbackException)
            { report("clipboard-unavailable"); return; }
            finally { gate.Release(); }
        }
    }
    private async Task TransferAsync(ClipboardTransfer message, CancellationToken ct)
    {
        if (message.Kind == "text-ack")
        {
            report("clipboard-text-ack;pending=" + (message.Id == pendingTextId) + ";result=" + message.Result);
            if (message.Id == pendingTextId)
            {
                if (message.Result == 1) { pendingTextId = 0; report("clipboard-delivered"); }
                else AdvertiseFormats();
            }
            return;
        }
        if (message.Kind == "formats-ack") return;
        if (DateTimeOffset.UtcNow > expiry) ClearPending();
        if (message.Kind == "formats")
        {
            pendingTextId = 0; // A remote change supersedes an unacknowledged local change.
            send("TEXT_DATA_CHANNEL", HostClipboardTransfers.AcknowledgeFormats(message.Id));
            ClearPending();
            if (message.Format < 0) return;
            pendingFormat = message.Format; pendingKey = "u-remote-" + Guid.NewGuid().ToString("N");
            pendingId = (ulong)Interlocked.Increment(ref sequence); expiry = DateTimeOffset.UtcNow.AddSeconds(10);
            send("TEXT_DATA_CHANNEL", HostClipboardTransfers.Ask(pendingId, pendingKey, pendingFormat));
            report("clipboard-format-requested;format=" + pendingFormat); return;
        }
        var matches = pendingKey is not null && message.Key == pendingKey;
        if (message.Kind == "confirm")
        {
            report($"clipboard-confirm;key-match={matches};id-match={message.Id == pendingId};result={message.Result};blocks={message.Count}");
            if (!matches || message.Id != pendingId) return;
            if (message.Result != 1 || message.Count is < 0 or > 16) { ClearPending(); return; }
            expectedBlocks = message.Count;
        }
        else if (message.Kind == "block")
        {
            report($"clipboard-block;key-match={matches};index={message.BlockId};expected={expectedBlocks}");
            var accepted = matches && message.BlockId is > 0 and <= 16 && !blocks.ContainsKey(message.BlockId)
                && blocks.Values.Sum(b => b.Length) + message.Data.Length <= HostClipboardProtocol.MaxTextBytes;
            if (message.BlockId > 0) send("FILE_DATA_CHANNEL", HostClipboardTransfers.AcknowledgeBlock(message, accepted));
            if (!accepted) { if (matches) ClearPending(); return; }
            blocks.Add(message.BlockId, message.Data.ToArray());
        }
        if (pendingKey is null || expectedBlocks < 0 || blocks.Count != expectedBlocks
            || Enumerable.Range(1, expectedBlocks).Any(i => !blocks.ContainsKey(i))) return;
        var bytes = blocks.Values.SelectMany(x => x).ToArray();
        try
        {
            var text = HostClipboardProtocol.DecodeTextData(pendingFormat, bytes);
            await write(text, ct); lastText = text; report("clipboard-received");
        }
        finally { Array.Clear(bytes); ClearPending(); }
    }
    private void AdvertiseFormats()
    {
        pendingTextId = 0;
        if (send("TEXT_DATA_CHANNEL", HostClipboardTransfers.Advertise((ulong)Interlocked.Increment(ref sequence))))
            report("clipboard-formats-sent");
    }
    private void ClearPending()
    { foreach (var b in blocks.Values) Array.Clear(b); blocks.Clear(); pendingKey = null; expectedBlocks = -1; }
    public async ValueTask DisposeAsync() { lastText = null; ClearPending(); if (clipboardOwner is not null) await clipboardOwner.DisposeAsync(); gate.Dispose(); }
}
