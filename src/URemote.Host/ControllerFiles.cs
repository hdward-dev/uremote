using System.Collections.Concurrent;
using System.IO.Compression;
using System.Threading.Channels;
using URemote.Core;
using static URemote.Core.FileTransferProtocol;
namespace URemote.Host;

public sealed record RemoteFileEntry(string Name, string Path, bool Directory, ulong Size);

// One user-initiated operation at a time; downloaded names never determine local paths.
public sealed class ControllerFiles(Func<string, byte[], bool> send)
{
    private long sequence = 100;
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<TransferMessage>> pending = new();
    private readonly Channel<TransferMessage> incoming = Channel.CreateBounded<TransferMessage>(32);
    private readonly SemaphoreSlim operation = new(1);
    private long downloadId;
    private Exception? receiveError;
    public event Action<long, long>? Progress;
    private static byte[] Id(ulong task, ulong index = 0) => Blob(1, Join(Int(1, task), Int(2, index)));
    private ulong Next() => (ulong)Interlocked.Increment(ref sequence);
    public void Receive(string channel, byte[] bytes)
    {
        if (channel is not ("TEXT_DATA_CHANNEL" or "FILE_DATA_CHANNEL" or "BINARY_DATA_CHANNEL")) return;
        try
        {
            var m = Decode(bytes); if (m is null) return;
            if (m.Response) { if (pending.TryRemove(m.RequestId, out var t)) t.TrySetResult(m); }
            else if (m.Kind is 3 or 4 or 9 && Volatile.Read(ref downloadId) != 0 && Number(Data(m.Body, 1), 1) == (ulong)Volatile.Read(ref downloadId))
            { if (!incoming.Writer.TryWrite(m)) receiveError = new IOException("接收队列已满，请重新传输。"); }
        }
        catch (FormatException) { }
    }
    private async Task<TransferMessage> Request(int kind, byte[] body, CancellationToken ct)
    {
        var id = Next(); var done = new TaskCompletionSource<TransferMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = done;
        try
        {
            if (!send(kind == 4 ? "FILE_DATA_CHANNEL" : "TEXT_DATA_CHANNEL", Encode(false, id, kind, body))) throw new IOException("文件通道尚未连接。");
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        }
        finally { pending.TryRemove(id, out _); }
    }
    private static void Require(TransferMessage m, int kind, int statusTag)
    { if (m.Kind != kind || Number(m.Body, statusTag) != 1) throw new IOException("远端拒绝操作，请检查路径、权限或客户端支持情况。"); }
    private void Reply(TransferMessage m, int kind, byte[] body)
    { if (!send("TEXT_DATA_CHANNEL", Encode(true, m.RequestId, kind, body))) throw new IOException("连接已断开。"); }
    private static byte[] Compress(byte[] bytes)
    { using var output = new MemoryStream(); using (var z = new ZLibStream(output, CompressionLevel.Fastest, true)) z.Write(bytes); return output.ToArray(); }
    private static byte[] Inflate(byte[] bytes)
    {
        using var input = new MemoryStream(bytes); using Stream zip = bytes.FirstOrDefault() == 31 ? new GZipStream(input, CompressionMode.Decompress) : new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int n;
        while ((n = zip.Read(buffer)) > 0) { if (output.Length + n > 524288) throw new IOException("目录内容过大。"); output.Write(buffer, 0, n); }
        return output.ToArray();
    }
    public async Task<IReadOnlyList<RemoteFileEntry>> ListAsync(string path, CancellationToken ct)
    {
        await operation.WaitAsync(ct);
        try
        {
            var result = await Request(10, Join(Id(Next()), Text(2, path)), ct); Require(result, 7, 4);
            var entries = Read(result.Body).Where(f => f.Tag == 3).Select(f => f.Data).ToArray();
            if (entries.Length == 0 && Data(result.Body, 5).Length > 0) entries = Read(Inflate(Data(result.Body, 5))).Where(f => f.Tag == 1).Select(f => f.Data).ToArray();
            return entries.Select(b => new RemoteFileEntry(String(b, 2), String(b, 5), Number(b, 1) != 4, Number(b, 3))).ToArray();
        }
        finally { operation.Release(); }
    }
    public async Task UploadAsync(string directory, string name, Stream input, long length, CancellationToken ct)
    {
        if (length < 0 || length > 1_099_511_627_776L || string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', '\0']) >= 0 || name is "." or "..") throw new ArgumentException("无效文件。");
        await operation.WaitAsync(ct); var task = Next(); var success = false;
        try
        {
            var exists = await Request(11, Join(Text(1, directory), Text(2, name)), ct);
            if (exists.Kind != 8 || Number(Data(exists.Body, 2), 2) != 0) throw new IOException("远端已存在同名文件，请先改名。");
            var modified = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var metadata = Join(Text(1, name), Int(2, (ulong)length), Int(3, modified));
            Require(await Request(1, Join(Id(task), Text(2, directory), Blob(6, Compress(Blob(1, metadata)))), ct), 1, 3);
            Require(await Request(3, Join(Id(task, 1), Int(2, modified), Int(3, (ulong)length)), ct), 3, 3);
            var buffer = new byte[32768]; long total = 0; ulong block = 0;
            try
            {
                while (total < length)
                {
                    var n = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - total)), ct);
                    if (n == 0) throw new IOException("本地文件长度已变化。");
                    var reply = await Request(4, Join(Id(task, 1), Int(2, block++), Blob(3, buffer.AsSpan(0, n).ToArray())), ct); Require(reply, 4, 3);
                    if (Number(reply.Body, 4) != (ulong)n) throw new IOException("远端未完整确认数据。");
                    total += n; Progress?.Invoke(total, length);
                }
            }
            finally { Array.Clear(buffer); }
            Require(await Request(9, Join(Id(task, 1), Int(2, 1)), ct), 6, 2);
            Require(await Request(9, Join(Id(task, ulong.MaxValue), Int(2, 1)), ct), 6, 2); success = true;
        }
        finally
        {
            if (!success) send("TEXT_DATA_CHANNEL", Encode(false, Next(), 9, Join(Id(task, 1), Int(2, 7))));
            operation.Release();
        }
    }
    public async Task DownloadAsync(string directory, RemoteFileEntry file, Stream output, CancellationToken ct)
    {
        if (file.Directory || file.Size > 1_099_511_627_776UL) throw new ArgumentException("请选择一个文件。");
        await operation.WaitAsync(ct); var task = Next(); var success = false;
        try
        {
            while (incoming.Reader.TryRead(out _)) { } receiveError = null; Volatile.Write(ref downloadId, (long)task);
            var metadata = Join(Text(1, file.Name), Int(2, file.Size));
            Require(await Request(2, Join(Id(task), Text(2, directory), Blob(3, Compress(Blob(1, metadata)))), ct), 2, 2);
            ulong block = 0, total = 0; var asked = false; var completed = false;
            while (true)
            {
                if (receiveError is not null) throw receiveError;
                var m = await incoming.Reader.ReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct);
                var index = Number(Data(m.Body, 1), 2);
                if (m.Kind == 3 && index == 1 && !asked && Number(m.Body, 3) == file.Size)
                { asked = true; Reply(m, 3, Join(Id(task, 1), Int(3, 1))); }
                else if (m.Kind == 4 && index == 1 && asked && !completed && Number(m.Body, 2) == block)
                {
                    var chunk = Data(m.Body, 3);
                    if ((ulong)chunk.Length + total > file.Size) throw new IOException("远端文件超出声明长度。");
                    await output.WriteAsync(chunk, ct); total += (ulong)chunk.Length;
                    Reply(m, 4, Join(Id(task, 1), Int(2, block++), Int(3, 1), Int(4, (ulong)chunk.Length)));
                    Array.Clear(chunk); Progress?.Invoke((long)total, (long)file.Size);
                }
                else if (m.Kind == 9 && Number(m.Body, 2) == 1 && total == file.Size && asked)
                {
                    if (index == 1) { await output.FlushAsync(ct); completed = true; }
                    else if (index != ulong.MaxValue || !completed) throw new IOException("文件完成序列异常。");
                    Reply(m, 6, Join(Id(task, index), Int(2, 1)));
                    if (index == ulong.MaxValue) { success = true; return; }
                }
                else throw new IOException("文件传输顺序或长度异常。");
            }
        }
        finally
        {
            Volatile.Write(ref downloadId, 0);
            if (!success) send("TEXT_DATA_CHANNEL", Encode(false, Next(), 9, Join(Id(task, 1), Int(2, 7))));
            operation.Release();
        }
    }
}
