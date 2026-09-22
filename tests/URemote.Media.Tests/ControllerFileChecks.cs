using System.Threading.Channels;
using URemote.Core;
using URemote.Host;
using URemote.Media;
static class ControllerFileChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "uremote-controller-files-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var wire = Channel.CreateUnbounded<PeerDataMessage>();
        var client = new ControllerFiles((label, bytes) => wire.Writer.TryWrite(new(label, false, bytes)));
        var host = new HostFileTransfer((label, bytes) => { client.Receive(label, bytes); return true; }, _ => { }, stop.Token, root);
        var pump = Task.Run(async () => { try { await foreach (var packet in wire.Reader.ReadAllAsync(stop.Token)) await host.HandleAsync(packet); } catch (OperationCanceledException) { } });
        try
        {
            var data = new byte[100003]; new Random(42).NextBytes(data);
            await client.UploadAsync("/", "测试.bin", new MemoryStream(data), data.Length, stop.Token);
            if (!File.ReadAllBytes(Path.Combine(root, "测试.bin")).SequenceEqual(data)) throw new Exception("Upload corruption");
            var entries = await client.ListAsync("/", stop.Token); var file = entries.Single();
            using var output = new MemoryStream(); await client.DownloadAsync("/", file, output, stop.Token);
            if (!output.ToArray().SequenceEqual(data)) throw new Exception("Download corruption");
            Console.WriteLine("PASS: controller directory listing, Unicode filename, multi-block upload and download with aggregate completion");
            try { await client.UploadAsync("/", "测试.bin", new MemoryStream(data), data.Length, stop.Token); throw new Exception("Existing file was accepted"); }
            catch (IOException) { }
            if (!File.ReadAllBytes(Path.Combine(root, "测试.bin")).SequenceEqual(data)) throw new Exception("Existing file changed");
            Console.WriteLine("PASS: controller refuses to overwrite remote same-name file");
            await client.UploadAsync("/", "empty.bin", new MemoryStream(), 0, stop.Token);
            using var empty = new MemoryStream(); await client.DownloadAsync("/", (await client.ListAsync("/", stop.Token)).Single(f => f.Name == "empty.bin"), empty, stop.Token);
            if (empty.Length != 0) throw new Exception("Empty download");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await client.ListAsync("/", canceled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
            Console.WriteLine("PASS: zero-byte transfer and cancellation");
            using var media = new ControllerMediaPeer(dataOnly: true);
            var offer = await media.OfferAsync(stop.Token);
            if (offer.Contains("m=video") || offer.Contains("m=audio")) throw new Exception("Tool session requested video/audio");
            Console.WriteLine("PASS: tools negotiate data channels without screen/audio tracks");
        }
        finally { stop.Cancel(); await pump; await host.DisposeAsync(); Directory.Delete(root, true); }
    }
}
