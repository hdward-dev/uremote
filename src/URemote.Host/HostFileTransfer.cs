using System.IO.Compression;
using System.Text;
using URemote.Core;
using URemote.Media;
using static URemote.Core.FileTransferProtocol;

namespace URemote.Host;
public static class HostTransferPaths
{
    public static string SharedDirectory => Environment.GetEnvironmentVariable("UREMOTE_FILE_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Download", "uurc");
}

// A dedicated shared directory is the only remotely accessible filesystem root.
public sealed class HostFileTransfer : IAsyncDisposable
{
    private readonly string root;
    private readonly Func<string,byte[],bool> send;
    private readonly Action<string> report;
    private readonly Dictionary<ulong, ReceiveTask> receiving = [];
    private readonly CancellationTokenSource stop;
    private readonly Dictionary<(ulong Task, ulong Index, int Kind, ulong Block), TaskCompletionSource<TransferMessage>> replies = [];
    private readonly List<Task> downloads = [];
    private long sequence = 1000;
    private readonly HashSet<int> observedKinds = [];
    private readonly HashSet<ulong> completedUploads = [];
    private sealed record FileSpec(string Name, long Size, ulong Modified);
    private sealed class ReceiveTask(ulong id, string target, List<FileSpec> files)
    {
        public ulong Id = id; public string Target = target; public List<FileSpec> Files = files;
        public FileStream? Stream; public string? Temporary; public string? Final;
        public ulong Index, NextBlock; public long Written;
    }
    public HostFileTransfer(Func<string,byte[],bool> send, Action<string> report, CancellationToken ct, string? directory = null)
    { this.send=send;this.report=report;root=Path.GetFullPath(directory ?? HostTransferPaths.SharedDirectory);stop=CancellationTokenSource.CreateLinkedTokenSource(ct); }
    private string Resolve(string remote, bool createRoot = true)
    {
        if (createRoot) Directory.CreateDirectory(root);
        // Official UU clients use this virtual path for the host's configured
        // receive directory. It is a protocol token, not a filesystem URI.
        var normalized = remote.Replace('\\','/');
        if(normalized is ":/" or ":/Default")normalized="/";
        else if(normalized.StartsWith(":/Default/",StringComparison.Ordinal))normalized=normalized[10..];
        var relative = normalized.TrimStart('/');
        if(relative.Split('/').Any(x=>x is ".." or ".") || relative.Contains(':') || relative.Contains('\0')) throw new UnauthorizedAccessException("remote-path-invalid");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if(full!=root && !full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.Ordinal)) throw new UnauthorizedAccessException("remote-path-outside-root");
        for(string? p=full; p is not null && p.Length>=root.Length; p=Path.GetDirectoryName(p))
            if(File.Exists(p)||Directory.Exists(p)) { if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0) throw new UnauthorizedAccessException("remote-path-symlink"); }
        return full;
    }
    private static string SafeName(string value)
    {
        if(string.IsNullOrWhiteSpace(value)||value.Length>4096||value.Split('/','\\').Any(x=>x is ".." or "." || x.Length==0)||Path.IsPathRooted(value)||value.Contains(':')||value.Contains('\0')) throw new UnauthorizedAccessException("file-name-invalid");
        return value.Replace('\\','/');
    }
    private static ulong TaskId(byte[] body) => Number(Data(body,1),1);
    private static ulong Index(byte[] body) => Number(Data(body,1),2);
    private static byte[] Id(ulong task, ulong index=0) => Blob(1,Join(Int(1,task),Int(2,index)));
    private void Reply(TransferMessage request,int kind,byte[] body,string channel="TEXT_DATA_CHANNEL")
    { if(!send(channel,Encode(true,request.RequestId,kind,body))) throw new IOException("File reply channel unavailable."); }
    private static byte[] Inflate(byte[] compressed)
    {
        if(compressed.Length==0)return [];
        using var input=new MemoryStream(compressed); using Stream zip=compressed[0]==0x1f ? new GZipStream(input,CompressionMode.Decompress) : new ZLibStream(input,CompressionMode.Decompress);
        using var output=new MemoryStream();var buffer=new byte[8192];int n;
        while((n=zip.Read(buffer))>0) { if(output.Length+n>524288)throw new FormatException("File list exceeds limit.");output.Write(buffer,0,n); } return output.ToArray();
    }
    private static byte[] Compress(byte[] data)
    {
        using var output=new MemoryStream();
        using(var zip=new ZLibStream(output,CompressionLevel.Fastest,true))zip.Write(data);
        return output.ToArray();
    }
    private static byte[] Metadata(FileSpec file) => Join(Text(1,file.Name),Int(2,(ulong)file.Size),Int(3,file.Modified));
    private static List<FileSpec> Files(byte[] body,int directTag,int compressedTag)
    {
        var direct=Read(body).Where(x=>x.Tag==directTag).Select(x=>x.Data).ToArray();
        if(direct.Length==0 && Data(body,compressedTag).Length>0) direct=Read(Inflate(Data(body,compressedTag))).Where(x=>x.Tag==1).Select(x=>x.Data).ToArray();
        if(direct.Length is <1 or >4096)throw new FormatException("Invalid file count.");
        return direct.Select(x=>new FileSpec(SafeName(String(x,1)),checked((long)Number(x,2)),Number(x,3))).ToList();
    }
    public async Task<bool> HandleAsync(PeerDataMessage packet)
    {
        TransferMessage? message;
        try { message=Decode(packet.Bytes); } catch(FormatException){return false;}
        if(message is null)return false;
        if(message.Response)
        {
            // Only these responses belong to an outgoing transfer. Other FTP
            // responses start with a path or error enum, not a nested TaskId.
            if(message.Kind is not (3 or 4 or 6))return true;
            var key=(TaskId(message.Body),Index(message.Body),message.Kind,message.Kind==4?Number(message.Body,2):0);
            lock(replies) if(replies.Remove(key,out var pending))pending.TrySetResult(message);
            return true;
        }
        if(observedKinds.Add(message.Kind)) report("file-request;kind="+message.Kind+";fields="+string.Join(",",Read(message.Body).Select(x=>x.Tag)));
        ulong id=0,index=0;
        try
        {
            // FileExist, Rename, DirCreate and RemoveFile use field 1 for a path.
            // Decode a TaskId only for operation schemas that actually contain one.
            if(message.Kind is 1 or 2 or 3 or 4 or 8 or 9 or 10 or 13)
            { id=TaskId(message.Body);index=Index(message.Body); }
            switch(message.Kind)
            {
                case 10: ReadDirectory(message);break;
                case 1: Receive(message);break;
                case 3: await AskAsync(message);break;
                case 4: await BlockAsync(message);break;
                case 9: await CompleteAsync(message);break;
                case 2: StartDownload(message);break;
                case 6:
                    Directory.CreateDirectory(Resolve(String(message.Body,1)));
                    Reply(message,5,Join(Int(1,1),Text(3,String(message.Body,1))));break;
                case 11:
                    var path=Resolve(String(message.Body,1));
                    var names=Read(message.Body).Where(x=>x.Tag==2).Select(x=>new UTF8Encoding(false,true).GetString(x.Data)).Take(4096);
                    var results=names.Select(n=>Blob(2,Join(Text(1,n),Int(2,
                        File.Exists(Resolve(String(message.Body,1).TrimEnd('/')+"/"+SafeName(n))) ? 1UL : 0UL)))).ToArray();
                    Reply(message,8,Join(new[]{Text(1,String(message.Body,1))}.Concat(results).ToArray()));break;
                default: Reply(message,5,Join(Int(1,4),Text(2,"Unsupported file operation")));break;
            }
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or FormatException or OverflowException or ArgumentException or KeyNotFoundException)
        {
            var error=e is UnauthorizedAccessException ? 4UL : e is FileNotFoundException or DirectoryNotFoundException ? 9UL : 3UL;
            report("file-transfer-rejected;kind="+message.Kind+";error="+error+";site="+new System.Diagnostics.StackTrace(e).GetFrame(0)?.GetMethod()?.Name);
            if(e is UnauthorizedAccessException && e.Message.StartsWith("remote-path-",StringComparison.Ordinal))report("file-path-reason="+e.Message);
            if(message.Kind is 1 or 11) {
                var pathText=String(message.Body,message.Kind==11?1:2);
                // Record only the protocol prefix, never a filename or path suffix.
                var marker=System.Text.RegularExpressions.Regex.Match(pathText,@"^([A-Za-z_]{0,32}):");
                report("file-path-marker="+(marker.Success?marker.Groups[1].Value:"non-scheme")+";leading-colons="+pathText.TakeWhile(c=>c==':').Count());
                report("file-path-shape;empty="+(pathText.Length==0)+";root="+(pathText=="/")+";homeAlias="+pathText.StartsWith("~")+";drive="+(pathText.Length>1&&pathText[1]==':')+";uri="+pathText.StartsWith("file:")+";dot="+pathText.Replace('\\','/').Split('/').Any(x=>x is "." or "..")+";colon="+pathText.Contains(':'));
            }
            var body=message.Kind switch {
                10=>Join(Id(id),Text(2,String(message.Body,2)),Int(4,error)),
                1=>Join(Id(id),Int(3,error)), 2=>Join(Id(id),Int(2,error)),
                3=>Join(Id(id,index),Int(3,error)), 4=>Join(Id(id,index),Int(2,Number(message.Body,2)),Int(3,error)),
                _=>Join(Id(id,index),Int(2,error)) };
            Reply(message,message.Kind switch {10=>7,1=>1,2=>2,3=>3,4=>4,_=>6},body);
        }
        return true;
    }
    private void ReadDirectory(TransferMessage request)
    {
        var remote=String(request.Body,2);var path=Resolve(remote);
        if(!Directory.Exists(path)) {
            report("file-directory-missing;is-file="+File.Exists(path)+";physical-root-prefix="+remote.StartsWith(root,StringComparison.Ordinal)+";segments="+remote.Replace('\\','/').Split('/',StringSplitOptions.RemoveEmptyEntries).Length);
            throw new DirectoryNotFoundException();
        }
        var entries=new List<byte[]>();var bytes=0;
        // UU's virtual root is a navigation page of volumes/folders. iOS
        // treats its entries as directory destinations, even for a file type.
        if(remote==":/")entries.Add(Blob(3,Join(Int(1,0),Text(2,"uurc"),Text(5,"/"),Text(6,"folder"))));
        foreach(var entry in remote==":/" ? Enumerable.Empty<FileSystemInfo>() : new DirectoryInfo(path).EnumerateFileSystemInfos().OrderBy(x=>x.Name).Take(4096))
        {
            if((entry.Attributes&FileAttributes.ReparsePoint)!=0 || entry.Name.StartsWith(".uremote-",StringComparison.Ordinal))continue;
            var dir=entry is DirectoryInfo;var full="/"+Path.GetRelativePath(root,entry.FullName).Replace('\\','/');
            var encoded=Blob(3,Join(Int(1,dir?0UL:4UL),Text(2,entry.Name),Int(3,dir?0UL:(ulong)((FileInfo)entry).Length),Int(4,(ulong)new DateTimeOffset(entry.LastWriteTimeUtc).ToUnixTimeSeconds()),Text(5,full),Text(6,dir?"folder":"file")));
            bytes+=encoded.Length;if(bytes>110000)break;entries.Add(encoded);
        }
        var compressed=Compress(Join(entries.Select(x=>Blob(1,Data(x,3))).ToArray()));
        Reply(request,7,Join(new[]{Id(TaskId(request.Body)),Text(2,remote),Int(4,1),Blob(5,compressed)}.Concat(entries).ToArray()));report("file-directory-listed;count="+entries.Count);
    }
    private void Receive(TransferMessage request)
    {
        var id=TaskId(request.Body);if(receiving.Count>=8||receiving.ContainsKey(id))throw new FormatException("Transfer already exists.");
        completedUploads.Remove(id);
        var files=Files(request.Body,3,6);
        if(files.Any(x=>x.Size<0)||files.Sum(x=>(decimal)x.Size)>1_099_511_627_776m)throw new FormatException("Transfer exceeds limit.");
        var target=Resolve(String(request.Body,2));Directory.CreateDirectory(target);
        var folder=String(request.Body,5);if(folder.Length>0)target=Resolve(Path.GetRelativePath(root,target).Trim('.')+"/"+SafeName(folder));
        Directory.CreateDirectory(target); receiving.Add(id,new(id,target,files));
        Reply(request,1,Join(Id(id),Int(3,1)));report("file-transfer-receiving;files="+files.Count);
    }
    private async Task AskAsync(TransferMessage request)
    {
        var task=receiving[TaskId(request.Body)];var index=Index(request.Body);
        report($"file-ask-shape;index={index};count={task.Files.Count};open={task.Stream is not null};size={Number(request.Body,3)};modified={Number(request.Body,2)}");
        if(index==0||index>(ulong)task.Files.Count||task.Stream is not null)throw new FormatException("File index or stream state mismatch.");
        var spec=task.Files[checked((int)index)-1];report($"file-metadata;size={spec.Size};modified={spec.Modified}");
        if(Number(request.Body,3)!=(ulong)spec.Size)throw new FormatException("File size mismatch.");
        var relative=Path.GetRelativePath(root,task.Target);var final=Resolve((relative=="."?"":relative+"/")+spec.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        // Never silently replace a pre-existing local file.
        if(File.Exists(final)) { var stem=Path.GetFileNameWithoutExtension(final);var extension=Path.GetExtension(final);var parent=Path.GetDirectoryName(final)!;var suffix=1;do{final=Path.Combine(parent,stem+" ("+suffix+++ ")"+extension);}while(File.Exists(final)); }
        task.Temporary=Path.Combine(Path.GetDirectoryName(final)!,".uremote-"+Guid.NewGuid().ToString("N")+".part"); task.Final=final;
        task.Stream=new FileStream(task.Temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true); task.Index=index;task.Written=0;task.NextBlock=0;
        Reply(request,3,Join(Id(task.Id,index),Int(3,1)));await Task.CompletedTask;
    }
    private async Task BlockAsync(TransferMessage request)
    {
        var task=receiving[TaskId(request.Body)];var block=Number(request.Body,2);var data=Data(request.Body,3);
        if(task.Stream is null||Index(request.Body)!=task.Index||block!=task.NextBlock||data.Length>262144||task.Written+data.Length>task.Files[checked((int)task.Index)-1].Size)throw new FormatException("Invalid block.");
        await task.Stream.WriteAsync(data,stop.Token);task.Written+=data.Length;task.NextBlock++;
        Reply(request,4,Join(Id(task.Id,task.Index),Int(2,block),Int(3,1),Int(4,(ulong)data.Length)));
        if(task.Written==task.Files[checked((int)task.Index)-1].Size || task.NextBlock%32==0)report($"file-transfer-progress;bytes={task.Written};total={task.Files[checked((int)task.Index)-1].Size}");
    }
    private async Task CompleteAsync(TransferMessage request)
    {
        var id=TaskId(request.Body);
        if(!receiving.TryGetValue(id,out var task)) {
            // Clients send an aggregate completion after the last file, and may
            // retry a completion whose acknowledgement was lost.
            if(completedUploads.Contains(id)&&Number(request.Body,2)==1) { Reply(request,6,Join(Id(id,Index(request.Body)),Int(2,1)));return; }
            throw new FormatException();
        }
        if(Number(request.Body,2)!=1)
        {
            if(task.Stream is not null)await task.Stream.DisposeAsync();task.Stream=null;
            if(task.Temporary is not null)File.Delete(task.Temporary);
            receiving.Remove(id);Reply(request,6,Join(Id(id,Index(request.Body)),Int(2,7)));report("file-transfer-canceled");return;
        }
        if(task.Stream is null || task.Index!=Index(request.Body) || task.Written!=task.Files[checked((int)task.Index)-1].Size || Number(request.Body,2)!=1)throw new FormatException("Incomplete file.");
        await task.Stream.FlushAsync(stop.Token);await task.Stream.DisposeAsync();task.Stream=null;
        File.Move(task.Temporary!,task.Final!,false);task.Temporary=null;
        Reply(request,6,Join(Id(id,task.Index),Int(2,1)));report("file-transfer-received");
        if(task.Index==(ulong)task.Files.Count) {
            receiving.Remove(id);if(completedUploads.Count>=512)completedUploads.Remove(completedUploads.First());completedUploads.Add(id);
        }
    }
    private void StartDownload(TransferMessage request)
    {
        if(downloads.Count(x=>!x.IsCompleted)>=4)throw new FormatException("Too many downloads.");
        var path=Resolve(String(request.Body,2));
        var selected=new List<(string Path,FileSpec Spec)>();
        if(Data(request.Body,3).Length>0) {
            if(!Directory.Exists(path))throw new DirectoryNotFoundException();
            foreach(var requested in Files(request.Body,-1,3)) {
                var relative=Path.GetRelativePath(root,path);
                var full=Resolve((relative=="."?"":relative+"/")+requested.Name);
                if(!File.Exists(full))throw new FileNotFoundException();
                var info=new FileInfo(full);
                selected.Add((full,new(requested.Name,info.Length,(ulong)new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds())));
            }
        } else {
            if(!File.Exists(path))throw new FileNotFoundException();
            var info=new FileInfo(path);
            selected.Add((path,new(info.Name,info.Length,(ulong)new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds())));
        }
        var task=TaskId(request.Body);
        var list=selected.Select(x=>Blob(1,Metadata(x.Spec))).ToArray();
        var direct=selected.Select(x=>Blob(3,Metadata(x.Spec))).ToArray();
        var reply=Join(new[]{Id(task),Int(2,1),Blob(5,Compress(Join(list)))}.Concat(direct).ToArray());
        if(reply.Length>131000)throw new FormatException("Selection exceeds response limit.");
        Reply(request,2,reply);
        downloads.Add(SendFilesAsync(task,selected));
    }
    private async Task SendFilesAsync(ulong task,List<(string Path,FileSpec Spec)> files)
    {
        for(var i=0;i<files.Count;i++)if(!await SendFileAsync(task,(ulong)i+1,files[i].Path,files[i].Spec))return;
        try {
            // Official UU sends Complete(fileIndex = -1) after all individual
            // file completions. Without it iOS stays at 100% indefinitely.
            var completed=await RequestAsync(task,ulong.MaxValue,9,6,Join(Id(task,ulong.MaxValue),Int(2,1)));
            if(Number(completed.Body,2)!=1)throw new IOException("Transfer task not confirmed.");
            report("file-transfer-sent");
        } catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        catch {report("file-transfer-send-failed;stage=task-completion");}
    }

    private async Task<TransferMessage> RequestAsync(ulong task,ulong index,int kind,int responseKind,byte[] body,ulong block=0)
    {
        var pending=new TaskCompletionSource<TransferMessage>(TaskCreationOptions.RunContinuationsAsynchronously);var key=(task,index,responseKind,block);
        lock(replies)replies.Add(key,pending);
        try {
            var packet=Encode(false,(ulong)Interlocked.Increment(ref sequence),kind,body);
            if(!send(kind==4?"FILE_DATA_CHANNEL":"TEXT_DATA_CHANNEL",packet))throw new IOException("File channel unavailable.");
            return await pending.Task.WaitAsync(TimeSpan.FromSeconds(20),stop.Token);
        } finally {lock(replies)replies.Remove(key);}
    }
    private async Task<bool> SendFileAsync(ulong task,ulong index,string path,FileSpec spec)
    {
        try {
            var confirm=await RequestAsync(task,index,3,3,Join(Id(task,index),Int(2,spec.Modified),Int(3,(ulong)spec.Size)));
            if(Number(confirm.Body,3)!=1||Number(confirm.Body,2)!=0)throw new IOException("File declined.");
            var resume=Number(confirm.Body,4);if(resume>(ulong)spec.Size)throw new FormatException();
            await using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,true);stream.Position=(long)resume;
            var buffer=new byte[32768];ulong block=0;int n;
            while((n=await stream.ReadAsync(buffer,stop.Token))>0) {
                var ack=await RequestAsync(task,index,4,4,Join(Id(task,index),Int(2,block),Blob(3,buffer.AsSpan(0,n).ToArray())),block);
                if(Number(ack.Body,3)!=1||Number(ack.Body,4)!=(ulong)n)throw new IOException("Block declined.");block++;
                if(block%32==0)report($"file-transfer-progress;bytes={stream.Position};total={spec.Size}");
            }
            var done=await RequestAsync(task,index,9,6,Join(Id(task,index),Int(2,1)));
            if(Number(done.Body,2)!=1)throw new IOException("Transfer not confirmed.");report("file-item-sent");return true;
        } catch(OperationCanceledException) when(stop.IsCancellationRequested) {return false;}
        catch {report("file-transfer-send-failed");return false;}
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();await Task.WhenAll(downloads);
        foreach(var task in receiving.Values) {if(task.Stream is not null)await task.Stream.DisposeAsync();if(task.Temporary is not null)try{File.Delete(task.Temporary);}catch(IOException){}}
        receiving.Clear();stop.Dispose();
    }
}
