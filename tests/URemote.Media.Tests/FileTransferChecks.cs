using System.Security.Cryptography;
using System.IO.Compression;
using System.Threading.Channels;
using URemote.Core;
using URemote.Host;
using URemote.Media;
using static URemote.Core.FileTransferProtocol;
static class FileTransferChecks
{
    public static async Task RunAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"uremote-files-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var wire=Channel.CreateUnbounded<byte[]>();using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try {
            await using var host=new HostFileTransfer((_,bytes)=>wire.Writer.TryWrite(bytes),_=>{},deadline.Token,root);
            async Task<TransferMessage> Request(int kind,byte[] body) {
                await host.HandleAsync(new("FILE_DATA_CHANNEL",false,Encode(false,77,kind,body)));
                return Decode(await wire.Reader.ReadAsync(deadline.Token))!;
            }
            byte[] TaskId(ulong id,ulong index=1)=>Blob(1,Join(Int(1,id),Int(2,index)));
            var data=new byte[100003];new Random(7).NextBytes(data);
            var metadata=Blob(3,Join(Text(1,"fixture.bin"),Int(2,(ulong)data.Length),Int(3,1700000000)));
            var exists=await Request(11,Join(Text(1,":/Default"),Text(2,"文件.bin")));
            if(exists.Kind!=8 || String(exists.Body,1)!=":/Default" || String(Data(exists.Body,2),1)!="文件.bin" || Number(Data(exists.Body,2),2)!=0)
                throw new Exception("Preflight file existence response wrong.");
            var created=await Request(6,Text(1,"/子目录"));
            if(created.Kind!=5||Number(created.Body,1)!=1||!Directory.Exists(Path.Combine(root,"子目录")))throw new Exception("Directory create decoded path as TaskId.");
            Directory.Delete(Path.Combine(root,"子目录"));
            await host.HandleAsync(new("TEXT_DATA_CHANNEL",true,Encode(true,78,8,Join(Text(1,"/"),Blob(2,Text(1,"文件.bin"))))));
            if(wire.Reader.TryRead(out _))throw new Exception("Unsolicited directory response triggered a reply.");
            Console.WriteLine("PASS: official upload preflight uses path schema, including Unicode names and non-task responses");
            using var listBytes=new MemoryStream();
            using(var compressor=new ZLibStream(listBytes,CompressionLevel.Fastest,true))compressor.Write(Blob(1,Data(metadata,3)));
            var receive=await Request(1,Join(TaskId(1),Text(2,":/Default"),Blob(6,listBytes.ToArray()),Text(7,"synthetic-upload")));
            if(receive.Kind!=1||Number(receive.Body,3)!=1)throw new Exception("Receive request failed.");
            var invalidIndex=await Request(3,Join(TaskId(1,0),Int(3,(ulong)data.Length)));
            if(Number(invalidIndex.Body,3)==1)throw new Exception("Zero-based file index was accepted.");
            var ask=await Request(3,Join(TaskId(1),Int(2,1700000000),Int(3,(ulong)data.Length)));
            if(ask.Kind!=3||Number(ask.Body,3)!=1)throw new Exception("File ask failed.");
            var block=0UL;
            for(var at=0;at<data.Length;at+=32768) {
                var count=Math.Min(32768,data.Length-at);
                var ack=await Request(4,Join(TaskId(1),Int(2,block++),Blob(3,data.AsSpan(at,count).ToArray())));
                if(ack.Kind!=4||Number(ack.Body,4)!=(ulong)count||Number(ack.Body,3)!=1)throw new Exception("Chunk acknowledgement wrong.");
            }
            var completed=await Request(9,Join(TaskId(1),Int(2,1)));
            if(completed.Kind!=6||Number(completed.Body,2)!=1||!SHA256.HashData(File.ReadAllBytes(Path.Combine(root,"fixture.bin"))).SequenceEqual(SHA256.HashData(data)))throw new Exception("Received file integrity wrong.");
            Console.WriteLine("PASS: bounded multi-block upload commits exact file after completion");
            var existing=await Request(11,Join(Text(1,"/"),Text(2,"fixture.bin")));
            if(Number(Data(existing.Body,2),2)!=1)throw new Exception("Existing upload target not reported.");
            var aggregate=await Request(9,Join(TaskId(1,ulong.MaxValue),Int(2,1)));
            if(Number(aggregate.Body,2)!=1)throw new Exception("Aggregate upload completion rejected.");
            var navigation=await Request(10,Join(TaskId(2),Text(2,":/")));
            var sharedFolder=Data(navigation.Body,3);
            if(Number(sharedFolder,1)!=0||String(sharedFolder,2)!="uurc"||String(sharedFolder,5)!="/")throw new Exception("Virtual root must expose a folder entry, not loose files.");
            var listing=await Request(10,Join(TaskId(2),Text(2,String(sharedFolder,5))));
            if(listing.Kind!=7||Number(listing.Body,4)!=1||Read(listing.Body).Count(x=>x.Tag==3)!=1)throw new Exception("Directory reply wrong.");
            // Official 4.41 /etc/hosts response: regular files are type 4;
            // ordinary directories omit type (0), while type 3 is not a file.
            if(Number(Data(listing.Body,3),1)!=4)throw new Exception("Regular file advertised as a directory or drive.");
            Directory.CreateDirectory(Path.Combine(root,"subfolder"));
            var withFolder=await Request(10,Join(TaskId(2),Text(2,"/")));
            var folder=Read(withFolder.Body).Where(x=>x.Tag==3).Single(x=>String(x.Data,2)=="subfolder");
            if(Number(folder.Data,1)!=0)throw new Exception("Ordinary directory type differs from official host.");
            Directory.Delete(Path.Combine(root,"subfolder"));
            using(var compressed=new ZLibStream(new MemoryStream(Data(listing.Body,5)),CompressionMode.Decompress))
            using(var unpacked=new MemoryStream()) {
                compressed.CopyTo(unpacked);
                if(!Data(unpacked.ToArray(),1).SequenceEqual(Data(listing.Body,3)))throw new Exception("Compressed directory list differs.");
            }
            var echo=HostControlEcho.Reply(Blob(3,[]),1,1,false,false,true)!;
            if(Number(Data(Data(echo,3),4),6)!=2)throw new Exception("FTP capability missing.");
            var aliasTraversal=await Request(10,Join(TaskId(2),Text(2,":/Default/../")));
            if(Number(aliasTraversal.Body,4)!=4)throw new Exception("Default alias permits traversal.");
            var unknownAlias=await Request(10,Join(TaskId(2),Text(2,":/Unknown")));
            if(Number(unknownAlias.Body,4)!=4)throw new Exception("Unknown protocol alias accepted.");
            var traversal=await Request(10,Join(TaskId(2),Text(2,"/../")));
            if(Number(traversal.Body,4)!=4)throw new Exception("Path traversal allowed.");
            Directory.CreateSymbolicLink(Path.Combine(root,"escape"),Path.GetTempPath());
            var symlink=await Request(10,Join(TaskId(2),Text(2,"/escape")));
            if(Number(symlink.Body,4)!=4)throw new Exception("Symlink escape allowed.");
            Console.WriteLine("PASS: official directory reply shape; traversal and symlink escape rejected");
            // Official selection requests carry a parent directory plus a compressed file list.
            var send=await Request(2,Join(TaskId(3),Text(2,":/"),Blob(3,listBytes.ToArray())));
            if(send.Kind!=2||Number(send.Body,2)!=1)throw new Exception("Download request failed.");
            using var downloaded=new MemoryStream();
            while(true) {
                var request=Decode(await wire.Reader.ReadAsync(deadline.Token))!;if(request.Response)throw new Exception("Expected sender request.");
                var index=Number(Data(request.Body,1),2);
                if(index!=1 && !(request.Kind==9 && index==ulong.MaxValue))throw new Exception("Download file/task index differs from official host.");
                int kind;byte[] reply;
                if(request.Kind==3){kind=3;reply=Join(TaskId(3),Int(3,1));}
                else if(request.Kind==4){var chunk=Data(request.Body,3);downloaded.Write(chunk);kind=4;reply=Join(TaskId(3),Int(2,Number(request.Body,2)),Int(3,1),Int(4,(ulong)chunk.Length));}
                else if(request.Kind==9){kind=6;reply=Join(TaskId(3,index),Int(2,1));}
                else throw new Exception("Unexpected transfer operation.");
                await host.HandleAsync(new("TEXT_DATA_CHANNEL",false,Encode(true,request.RequestId,kind,reply)));
                if(request.Kind==9 && index==ulong.MaxValue)break;
            }
            if(!downloaded.ToArray().SequenceEqual(data))throw new Exception("Download integrity wrong.");
            Console.WriteLine("PASS: download preserves bytes and completes file index 1 followed by task index -1 acknowledgement");
            await Request(1,Join(TaskId(4),Text(2,"/"),metadata));await Request(3,Join(TaskId(4),Int(3,(ulong)data.Length)));
            var duplicate=await Request(4,Join(TaskId(4),Int(2,3),Blob(3,[1])));
            if(Number(duplicate.Body,3)==1)throw new Exception("Out-of-order block accepted.");
            if(!File.ReadAllBytes(Path.Combine(root,"fixture.bin")).SequenceEqual(data))throw new Exception("Existing file overwritten.");
            Console.WriteLine("PASS: out-of-order chunk rejected and original same-name file preserved");
            await Request(9,Join(TaskId(4),Int(2,7)));
            if(Directory.EnumerateFiles(root,".uremote-*.part").Any())throw new Exception("Canceled partial file was retained.");
            Console.WriteLine("PASS: cancellation removes partial file without changing existing files");
        } finally { Directory.Delete(root,true); }
    }
}
