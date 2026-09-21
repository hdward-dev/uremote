using System.Net.Http.Headers;
using System.Text.Json;
namespace URemote.Core;

public sealed class UuWallpaperApi(LoginState state,HttpMessageHandler? handler=null):IDisposable
{
    private readonly HttpClient http=new(handler??new SocketsHttpHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(30)};
    public async Task UploadAsync(byte[] png,CancellationToken ct)
    {
        if(png.Length is <8 or >5_000_000 || !png.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))throw new ArgumentException("Expected bounded PNG wallpaper.");
        using var request=UuMacHostProtocol.BuildRequest(state,HttpMethod.Get,"/api/v1/tool/fp/token?filetype=1","");
        using var token=await JsonAsync(request,ct);var data=token.RootElement.GetProperty("data");
        var url=SecureUrl(data.GetProperty("req_url").GetString()!);
        if(png.Length>data.GetProperty("max_size").GetInt64())throw new IOException("Wallpaper exceeds upload limit.");
        using var file=new ByteArrayContent(png);file.Headers.ContentType=new MediaTypeHeaderValue("image/png");
        using var upload=new HttpRequestMessage(HttpMethod.Post,url){Content=file};
        upload.Headers.TryAddWithoutValidation("Authorization",data.GetProperty("token").GetString()!);
        using var uploaded=await JsonAsync(upload,ct,false);
        var result=uploaded.RootElement.TryGetProperty("data",out var nested)?nested:uploaded.RootElement;
        var imageUrl=SecureUrl(result.GetProperty("url").GetString()!);
        using var update=UuMacHostProtocol.BuildRequest(state,HttpMethod.Post,"/api/v1/device/wallpaper",JsonSerializer.Serialize(new {wallpaper_url=imageUrl.AbsoluteUri}));
        using var updated=await JsonAsync(update,ct);
    }
    private async Task<JsonDocument> JsonAsync(HttpRequestMessage request,CancellationToken ct,bool api=true)
    {
        using var response=await http.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode){
            var error=await response.Content.ReadAsStringAsync(ct);
            if(api && response.StatusCode==System.Net.HttpStatusCode.UnprocessableEntity) {
                using var validation=JsonDocument.Parse(error);
                if(validation.RootElement.TryGetProperty("data",out var issues)&&issues.ValueKind==JsonValueKind.Array)
                    throw new IOException("Wallpaper validation: "+string.Join(";",issues.EnumerateArray().Select(x=>x.GetProperty("loc").GetRawText()+":"+x.GetProperty("msg").GetString())));
            }
            throw new HttpRequestException("Wallpaper HTTP status "+(int)response.StatusCode+";known-error="+(error is "Require Token" or "Invalid Token" or "Signature Not Match"?error:"other"));
        }
        var body=await response.Content.ReadAsByteArrayAsync(ct);if(body.Length>131072)throw new IOException("Upload response exceeds limit.");
        var doc=JsonDocument.Parse(body);
        if(doc.RootElement.TryGetProperty("code",out var code)&&code.GetInt32()!=0){doc.Dispose();throw new IOException("Wallpaper API rejected request.");}
        if(api&&!doc.RootElement.TryGetProperty("data",out _)){doc.Dispose();throw new IOException("Invalid wallpaper API response.");}
        return doc;
    }
    private static Uri SecureUrl(string value)
    {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length>0||uri.Fragment.Length>0)throw new IOException("Invalid wallpaper upload URL.");return uri;
    }
    public void Dispose()=>http.Dispose();
}
