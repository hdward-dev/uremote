using System.Security.Cryptography;
using System.Text.Json;
using URemote.Core;
using URemote.Linux;
namespace URemote.Host;

internal static class HostWallpaper
{
    private sealed record Cache(string Hash,DateTimeOffset Uploaded);
    public static async Task RunAsync(LoginState state,string ffmpeg,string identityPath,Action<string> report,CancellationToken ct)
    {
        if(!OperatingSystem.IsLinux())return;
        using var api=new UuWallpaperApi(state);
        var cachePath=identityPath+".wallpaper.json";
        Cache? cache=null;
        try {if(File.Exists(cachePath))cache=JsonSerializer.Deserialize<Cache>(await File.ReadAllTextAsync(cachePath,ct));}catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException){}
        var missingReported=false;var failed=false;
        while(!ct.IsCancellationRequested) {
            try {
                var png=await LinuxWallpaper.RenderAsync(ffmpeg,ct);
                if(png is null) {if(!missingReported)report("wallpaper-source-unavailable");missingReported=true;}
                else {
                    missingReported=false;
                    var hash=Convert.ToHexString(SHA256.HashData(png));
                    if(cache?.Hash!=hash || DateTimeOffset.UtcNow-cache.Uploaded>TimeSpan.FromDays(7)) {
                        await api.UploadAsync(png,ct);cache=new(hash,DateTimeOffset.UtcNow);failed=false;
                        var temporary=cachePath+"."+Guid.NewGuid().ToString("N")+".tmp";
                        try {
                            await using(var stream=new FileStream(temporary,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite}))
                                await JsonSerializer.SerializeAsync(stream,cache,cancellationToken:ct);
                            File.Move(temporary,cachePath,true);
                        } finally {if(File.Exists(temporary))File.Delete(temporary);}
                        report("wallpaper-updated");
                    }
                }
            } catch(OperationCanceledException) when(ct.IsCancellationRequested) {break;}
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or FormatException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or OperationCanceledException) {
                if(!failed)report("wallpaper-update-failed;type="+e.GetType().Name);failed=true;
            }
            try {await Task.Delay(TimeSpan.FromSeconds(failed?120:30),ct);}catch(OperationCanceledException){break;}
        }
    }
}
