using System.Text;
using URemote.Core;
using URemote.Media;

internal static class ClipboardChecks
{
    public static async Task RunAsync()
    {
        var capabilities = FileTransferProtocol.Data(ControllerProtocol.ConnectOptions("fixture", enableClipboard: true), 11);
        var heartbeat = FileTransferProtocol.Data(FileTransferProtocol.Data(ControllerProtocol.Echo(1, true), 3), 4);
        // ConnectOptions.FeatureFlag.ff_clipboard is tag 8; SimpleAction.FeatureFlag
        // uses distinct useClipboard/autoClipboard fields at tags 1/2.
        if (FileTransferProtocol.Number(capabilities, 8) != 3 || FileTransferProtocol.Number(capabilities, 2) != 1
            || FileTransferProtocol.Number(heartbeat, 1) != 2 || FileTransferProtocol.Number(heartbeat, 2) != 2)
            throw new Exception("Clipboard version and permission fields were confused.");
        byte[] windowsFixture = [0x41, 0, 0x2d, 0x4e, 0x87, 0x65, 0x3d, 0xd8, 0x42, 0xde, 0, 0];
        if (HostClipboardProtocol.DecodeTextData(13, windowsFixture) != "A中文🙂")
            throw new Exception("Native Windows clipboard bytes decoded incorrectly.");
        if (HostClipboardProtocol.Decode(HostClipboardTransfers.Ask(1, "fixture", 0))?.Format != 1)
            throw new Exception("Named Mac UTF-8 format was lost.");
        var sent = new List<(string Channel, byte[] Bytes)>();
        string? local = null;
        var writes = 0;
        await using var clipboard = new HostClipboard((channel, bytes) => { sent.Add((channel, bytes.ToArray())); return true; }, _ => { },
            _ => Task.FromResult(local), (text, _) => { local = text; writes++; return Task.CompletedTask; });
        async Task Receive(string channel, byte[] bytes)
        {
            if (!await clipboard.HandleAsync(new PeerDataMessage(channel, false, bytes), CancellationToken.None))
                throw new Exception("Clipboard packet not handled.");
        }
        const string text = "中文 clipboard 🙂\nsecond line";
        await Receive("TEXT_DATA_CHANNEL", HostClipboardProtocol.Change(7, text));
        if (local != text || writes != 1 || sent.Single().Channel != "TEXT_DATA_CHANNEL")
            throw new Exception("Remote text not written or acknowledged.");

        // Receiving a change must not reflect it back to the sender on the next poll.
        sent.Clear();
        using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(1100)))
            try { await clipboard.WatchAsync(stop.Token); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        if (sent.Count != 0) throw new Exception("Remote text was echoed back.");

        local = "local → remote\n";
        using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(1100)))
            try { await clipboard.WatchAsync(stop.Token); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        if (sent.Count != 1 || HostClipboardProtocol.Decode(sent[0].Bytes)?.Text != local)
            throw new Exception("Text delivery was overwritten with an unsolicited format advertisement.");
        var sentId = HostClipboardProtocol.Decode(sent[0].Bytes)!.RequestId;
        await Receive("TEXT_DATA_CHANNEL", HostClipboardProtocol.Acknowledge(sentId, true));
        using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(2300)))
            try { await clipboard.WatchAsync(stop.Token); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        if (sent.Count != 1) throw new Exception("Acknowledged clipboard delivery must not fall back.");
        local = "fallback fixture"; sent.Clear();
        using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(1100)))
            try { await clipboard.WatchAsync(stop.Token); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        var rejectedId = HostClipboardProtocol.Decode(sent[0].Bytes)!.RequestId;
        await Receive("TEXT_DATA_CHANNEL", HostClipboardProtocol.Acknowledge(rejectedId, false));
        if (sent.Count != 2 || HostClipboardTransfers.Decode(sent[1].Bytes)?.Kind != "formats")
            throw new Exception("Rejected legacy delivery did not fall back to format exchange.");

        // Request a large remote value and deliver the data blocks before confirmation.
        sent.Clear();
        await Receive("TEXT_DATA_CHANNEL", HostClipboardTransfers.Advertise(50));
        var ask = sent.Select(x => HostClipboardProtocol.Decode(x.Bytes)).Single(x => x?.IsRead == true)!;
        var remote = new string('文', 18000) + "🙂\n";
        var response = HostClipboardProtocol.ReadResponse(ask, remote).ToArray();
        var before = writes;
        foreach (var block in response.Skip(1).Reverse()) await Receive(block.Channel, block.Data);
        if (writes != before) throw new Exception("Partial clipboard data was applied.");
        await Receive(response[0].Channel, response[0].Data);
        if (local != remote || writes != before + 1) throw new Exception("Out-of-order UTF-8 blocks did not assemble correctly.");

        // Serve native CF_UNICODETEXT as UTF-16LE with its terminating null.
        sent.Clear();
        await Receive("TEXT_DATA_CHANNEL", HostClipboardTransfers.Ask(80, "fixture-key", 13));
        var data = sent.Select(x => HostClipboardTransfers.Decode(x.Bytes)).Where(x => x?.Kind == "block").SelectMany(x => x!.Data).ToArray();
        if (new UnicodeEncoding(false, false, true).GetString(data) != remote + '\0') throw new Exception("Clipboard read response encoding changed.");
        Array.Clear(data);
        Console.WriteLine("PASS: distinct capability schemas, native Windows text, Mac format name, bidirectional text and out-of-order blocks");
    }
}
