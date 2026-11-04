using URemote.Linux;

internal static class ClipboardOwnerChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"uremote-clipboard-owner-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string Script(string name,string code)
        {
            var path=Path.Combine(root,name);File.WriteAllText(path,"#!/bin/sh\n"+code);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            return path;
        }
        try
        {
            var contents=Path.Combine(root,"contents");
            var paste=Script("paste","cat '"+contents+"' 2>/dev/null\n");
            var fail=Script("fail","cat >/dev/null\nexit 1\n");
            await using(var owner=new WaylandClipboard(paste,fail))
            {
                try { await owner.WriteAsync("fixture",default);check(false,"failed copy"); }
                catch(IOException) {check(true,"wl-copy failure cannot be acknowledged as a successful write");}
            }
            var copy=Script("copy","cat > '"+contents+"'\nsleep 30\n");
            await using(var owner=new WaylandClipboard(paste,copy))
            {
                using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await owner.WriteAsync("fixture",stop.Token);
                check(await owner.ReadAsync(stop.Token)=="fixture","write completes only after clipboard ownership and contents can be confirmed");
            }
            var stall=Script("stall","cat >/dev/null\nsleep 30\n");
            await using(var owner=new WaylandClipboard(paste,stall))
            {
                try { await owner.WriteAsync("other",default);check(false,"unconfirmed write"); }
                catch(IOException) {check(true,"unconfirmed clipboard ownership is bounded by a deadline");}
            }
        }
        finally {Directory.Delete(root,true);}
    }
}
