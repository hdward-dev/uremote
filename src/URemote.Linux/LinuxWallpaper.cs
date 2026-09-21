using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
namespace URemote.Linux;

public static class LinuxWallpaper
{
    public static async Task<byte[]?> RenderAsync(string ffmpeg,CancellationToken ct,string? home=null,string? shell=null)
    {
        home??=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var stateRoot=Environment.GetEnvironmentVariable("XDG_STATE_HOME")??Path.Combine(home,".local/state");
        var configRoot=Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")??Path.Combine(home,".config");
        var session=Read(Path.Combine(stateRoot,"DankMaterialShell/session.json"));
        var settings=Read(Path.Combine(configRoot,"DankMaterialShell/settings.json"));
        var source=Environment.GetEnvironmentVariable("UREMOTE_WALLPAPER");
        if(string.IsNullOrEmpty(source)) {
            if(session is null)return null;
            var light=session["isLightMode"]?.GetValue<bool>()??false;
            source=session["wallpaperPath"]?.GetValue<string>()??"";
            if(session["perModeWallpaper"]?.GetValue<bool>()==true)source=session[light?"wallpaperPathLight":"wallpaperPathDark"]?.GetValue<string>()??source;
            if(session["perMonitorWallpaper"]?.GetValue<bool>()==true && session["monitorWallpapers"] is JsonObject monitors)
                source=monitors.FirstOrDefault(x=>x.Value is JsonValue).Value?.GetValue<string>()??source;
        }
        if(source.StartsWith("file://",StringComparison.Ordinal))source=new Uri(source).LocalPath;
        var scratch=Path.Combine(Path.GetTempPath(),"uremote-wallpaper-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);
        try {
            if(source.Length==0 || source.StartsWith('#')) {
                shell??=FindShell();if(shell is null)return null;
                var svg=CreateBackdrop(session!,settings??new(),shell,source);
                var input=Path.Combine(scratch,"wallpaper.svg");var output=Path.Combine(scratch,"wallpaper.png");
                await File.WriteAllTextAsync(input,svg,ct);
                var renderer=FindExecutable("rsvg-convert","*librsvg*/bin/rsvg-convert");
                if(renderer is null)throw new IOException("Wallpaper SVG renderer unavailable.");
                await RunAsync(renderer,["-w","960","-h","540","-o",output,input],ct);
                return await File.ReadAllBytesAsync(output,ct);
            }
            if(!Path.IsPathFullyQualified(source)||!File.Exists(source)||new FileInfo(source).Length>100_000_000)return null;
            var target=Path.Combine(scratch,"wallpaper.png");
            await RunAsync(ffmpeg,["-hide_banner","-loglevel","error","-nostdin","-i",source,"-frames:v","1","-vf","scale=960:540:force_original_aspect_ratio=increase,crop=960:540","-map_metadata","-1",target],ct);
            var bytes=await File.ReadAllBytesAsync(target,ct);if(bytes.Length>5_000_000)throw new IOException("Wallpaper preview exceeds size limit.");return bytes;
        } finally {Directory.Delete(scratch,true);}
    }
    private static JsonObject? Read(string path)=>File.Exists(path)&&new FileInfo(path).Length<2_000_000?JsonNode.Parse(File.ReadAllText(path)) as JsonObject:null;
    public static string? FindShell()
    {
        foreach(var p in Directory.EnumerateDirectories("/proc"))try {
            if(!File.ReadAllText(Path.Combine(p,"comm")).Contains("quickshell",StringComparison.Ordinal))continue;
            var args=File.ReadAllText(Path.Combine(p,"cmdline")).Split('\0');
            for(int i=0;i+1<args.Length;i++)if(args[i]=="-p"&&File.Exists(Path.Combine(args[i+1],"Widgets/DankBackdrop.qml")))return args[i+1];
        }catch(IOException){}catch(UnauthorizedAccessException){}
        return null;
    }
    private static string? FindExecutable(string name,string pattern)
    {
        foreach(var dir in (Environment.GetEnvironmentVariable("PATH")??"").Split(':'))if(File.Exists(Path.Combine(dir,name)))return Path.Combine(dir,name);
        if(Directory.Exists("/nix/store"))foreach(var dir in Directory.EnumerateDirectories("/nix/store",pattern.Split('/')[0])) {var path=Path.Combine(dir,"bin",name);if(File.Exists(path))return path;}
        return null;
    }
    public static string CreateBackdrop(JsonObject session,JsonObject settings,string shell,string source)
    {
        string Color(string value)=>Regex.IsMatch(value,"^#[0-9a-fA-F]{6}$")?value:throw new FormatException("Invalid wallpaper color.");
        var light=session["isLightMode"]?.GetValue<bool>()??false;
        var name=settings["currentThemeName"]?.GetValue<string>()??"purple";
        var themes=File.ReadAllText(Path.Combine(shell,"Common/StockThemes.js"));
        var mode=themes.Split("LIGHT:")[light?1:0];
        var theme=Regex.Match(mode,@"\b"+Regex.Escape(name)+@":\s*\{([^}]+)\}").Groups[1].Value;
        if(theme.Length==0)throw new IOException("Wallpaper theme is not a stock theme.");
        string Get(string key)=>Color(Regex.Match(theme,key+":\\s*\"(#[0-9a-fA-F]{6})\"").Groups[1].Value);
        var primary=Get("primary");var secondary=Get("secondary");var background=source.Length>0?Color(source):Get("background");
        XNamespace ns="http://www.w3.org/2000/svg";
        var root=new XElement(ns+"svg",new XAttribute("viewBox","0 0 2560 1440"),new XAttribute("width",960),new XAttribute("height",540),
            new XElement(ns+"rect",new XAttribute("width",2560),new XAttribute("height",1440),new XAttribute("fill",background)));
        if(source.Length==0) {
            void Rect(double x,double y,double w,double h,string color,double opacity) {
                string N(double n)=>n.ToString(CultureInfo.InvariantCulture);
                root.Add(new XElement(ns+"rect",new XAttribute("x",N(x)),new XAttribute("y",N(y)),new XAttribute("width",N(w)),new XAttribute("height",N(h)),new XAttribute("fill",color),new XAttribute("opacity",N(opacity)),new XAttribute("transform",$"rotate(35 {N(x+w/2)} {N(y+h/2)})")));
            }
            Rect(2560*.7,-1440*.3,2560*.8,1440*1.5,primary,.16);Rect(2560*.85,-1440*.2,2560*.4,1440*1.2,secondary,.08);
            var logo=XElement.Load(Path.Combine(shell,"assets/danklogonormal.svg"));
            logo.SetAttributeValue("x",48);logo.SetAttributeValue("y",1440-48-200*(569.94629/506.50931));logo.SetAttributeValue("width",200);logo.SetAttributeValue("height",200*(569.94629/506.50931));
            root.Add(new XElement(ns+"defs",new XElement(ns+"filter",new XAttribute("id","tint"),new XElement(ns+"feFlood",new XAttribute("flood-color",primary)),new XElement(ns+"feComposite",new XAttribute("in2","SourceAlpha"),new XAttribute("operator","in")))));
            root.Add(new XElement(ns+"g",new XAttribute("opacity",.25),new XAttribute("filter","url(#tint)"),logo));
        }
        return root.ToString();
    }
    private static async Task RunAsync(string exe,string[] args,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var p=new Process{StartInfo=new(exe){UseShellExecute=false,RedirectStandardError=true,RedirectStandardOutput=true}};
        foreach(var arg in args)p.StartInfo.ArgumentList.Add(arg);p.Start();var error=p.StandardError.ReadToEndAsync(timeout.Token);var output=p.StandardOutput.ReadToEndAsync(timeout.Token);
        try {await p.WaitForExitAsync(timeout.Token);await Task.WhenAll(error,output);if(p.ExitCode!=0)throw new IOException("Wallpaper rendering failed.");}
        finally {if(!p.HasExited)p.Kill(true);}
    }
}
