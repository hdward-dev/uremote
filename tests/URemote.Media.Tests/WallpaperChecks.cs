using System.Net;
using System.Text.Json;
using URemote.Core;
static class WallpaperChecks
{
    public static async Task RunAsync()
    {
        var png=new byte[]{137,80,78,71,13,10,26,10,0,255,128,0};
        using var handler=new Handler(png);
        using var api=new UuWallpaperApi(new("account-secret","user","client","device"),handler);
        await api.UploadAsync(png,CancellationToken.None);
        if(handler.Step!=3)throw new Exception("Wallpaper update sequence incomplete.");
        Console.WriteLine("PASS: upload credential scope, raw PNG bytes, Authorization and wallpaper_url match verified API flow");
        try {await api.UploadAsync([1,2,3],CancellationToken.None);throw new Exception("Invalid image accepted.");}catch(ArgumentException){}
        if(handler.Step!=3)throw new Exception("Invalid image made network request.");
        Console.WriteLine("PASS: invalid wallpaper rejected before network access");
    }
    private sealed class Handler(byte[] expected):HttpMessageHandler
    {
        public int Step;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            var step=Step++;
            string response;
            if(step==0) {
                if(request.Method!=HttpMethod.Get || request.RequestUri!.PathAndQuery!="/api/v1/tool/fp/token?filetype=1" || request.Headers.Authorization?.Parameter!="account-secret")throw new Exception("Credential request wrong.");
                response="""{"code":0,"data":{"token":"upload-only-token","req_url":"https://fp.ps.netease.com/nrd-ugc/file/new/","max_size":5000000}}""";
            } else if(step==1) {
                if(request.Headers.GetValues("Authorization").Single()!="upload-only-token"||request.Headers.Contains("X-Param-device-id"))throw new Exception("Wrong credentials sent to file storage.");
                if(request.Content!.Headers.ContentType!.MediaType!="image/png"||!(await request.Content.ReadAsByteArrayAsync(ct)).SequenceEqual(expected))throw new Exception("Image bytes altered or multipart encoded.");
                response="""{"url":"https://fp.ps.netease.com/file/test-preview","fsize":12,"mime":"image/png"}""";
            } else if(step==2) {
                using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if(request.Method!=HttpMethod.Post||request.RequestUri!.AbsolutePath!="/api/v1/device/wallpaper"||!body.RootElement.TryGetProperty("wallpaper_url",out var url)||url.GetString()!="https://fp.ps.netease.com/file/test-preview")throw new Exception("Wallpaper binding wrong.");
                response="""{"code":0,"data":{}}""";
            } else throw new Exception("Unexpected request.");
            return new(HttpStatusCode.OK){Content=new StringContent(response)};
        }
    }
}
