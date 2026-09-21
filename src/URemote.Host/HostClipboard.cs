using System.Text;
using URemote.Core;
using URemote.Linux;
using URemote.Media;
internal sealed class HostClipboard : IAsyncDisposable
{
    private readonly WaylandClipboard clipboard = new();
    private readonly HostMediaPeer peer;
    private readonly Action<string> report;
    private string? lastText;
    private readonly SemaphoreSlim gate = new(1);
    private long sequence = 100000;
    private string? pendingKey;
    private ulong pendingId;
    private int pendingFormat, expectedBlocks = -1;
    private DateTimeOffset expiry;
    private readonly SortedDictionary<int, byte[]> blocks = new();
    public HostClipboard(HostMediaPeer peer, Action<string> report) { this.peer = peer; this.report = report; }
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
            try { text = await clipboard.ReadAsync(ct); }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or OperationCanceledException && !ct.IsCancellationRequested)
            { text = null; report("clipboard-unavailable"); }
            foreach (var response in HostClipboardProtocol.ReadResponse(request, text))
                try { report($"clipboard-response;channel={response.Channel};sent={peer.SendData(response.Channel, response.Data)}"); } finally { Array.Clear(response.Data); }
        }
        else
        {
            var success = request.Format is 1 or 13;
            if (success)
            {
                lastText = request.Text;
                try { await clipboard.WriteAsync(request.Text, ct); report("clipboard-received"); }
                catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
                { success = false; report("clipboard-unavailable"); }
            }
            var response = HostClipboardProtocol.Acknowledge(request.RequestId, success);
            peer.SendData("TEXT_DATA_CHANNEL", response);
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
                var text = await clipboard.ReadAsync(ct);
                if (text is null || text == lastText) continue;
                var packet = HostClipboardProtocol.Change((ulong)Interlocked.Increment(ref sequence), text);
                try { if (peer.SendData("TEXT_DATA_CHANNEL", packet))
                    {
                        peer.SendData("TEXT_DATA_CHANNEL", HostClipboardTransfers.Advertise((ulong)Interlocked.Increment(ref sequence)));
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
        if (DateTimeOffset.UtcNow > expiry) ClearPending();
        if (message.Kind == "formats")
        {
            peer.SendData("TEXT_DATA_CHANNEL", HostClipboardTransfers.AcknowledgeFormats(message.Id));
            ClearPending();
            if (message.Format < 0) return;
            pendingFormat = message.Format; pendingKey = "u-remote-" + Guid.NewGuid().ToString("N");
            pendingId = (ulong)Interlocked.Increment(ref sequence); expiry = DateTimeOffset.UtcNow.AddSeconds(10);
            peer.SendData("TEXT_DATA_CHANNEL", HostClipboardTransfers.Ask(pendingId, pendingKey, pendingFormat));
            report("clipboard-format-requested"); return;
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
            if (message.BlockId > 0) peer.SendData("FILE_DATA_CHANNEL", HostClipboardTransfers.AcknowledgeBlock(message, accepted));
            if (!accepted) { if (matches) ClearPending(); return; }
            blocks.Add(message.BlockId, message.Data.ToArray());
        }
        if (pendingKey is null || expectedBlocks < 0 || blocks.Count != expectedBlocks
            || Enumerable.Range(1, expectedBlocks).Any(i => !blocks.ContainsKey(i))) return;
        var bytes = blocks.Values.SelectMany(x => x).ToArray();
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0');
            await clipboard.WriteAsync(text, ct); lastText = text; report("clipboard-received");
        }
        finally { Array.Clear(bytes); ClearPending(); }
    }
    private void ClearPending()
    { foreach (var b in blocks.Values) Array.Clear(b); blocks.Clear(); pendingKey = null; expectedBlocks = -1; }
    public async ValueTask DisposeAsync() { lastText = null; ClearPending(); await clipboard.DisposeAsync(); gate.Dispose(); }
}
