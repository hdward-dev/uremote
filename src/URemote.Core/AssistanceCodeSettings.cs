using System.Text.Json;
namespace URemote.Core;

public sealed record AssistanceCodeSettings(string DeviceId, string CustomCode, bool UseCustom)
{
    public override string ToString() => "AssistanceCodeSettings(redacted)";
    public static AssistanceCodeSettings Load(string path, string deviceId)
    {
        if (!File.Exists(path)) return new(deviceId, "", false);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 8192) throw new IOException("无效验证码设置文件。");
        if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("验证码设置文件必须仅当前用户可读写。");
        var value = JsonSerializer.Deserialize<AssistanceCodeSettings>(File.ReadAllText(path)) ?? throw new IOException();
        if (value.CustomCode is null) throw new IOException("无效验证码设置文件。");
        if (value.DeviceId != deviceId) return new(deviceId, "", false);
        if (value.CustomCode.Length > 0 || value.UseCustom) HostAssistance.ValidateCustomCode(value.CustomCode);
        return value;
    }
    public void Save(string path)
    {
        if (CustomCode.Length > 0 || UseCustom) HostAssistance.ValidateCustomCode(CustomCode);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temp, options)) { JsonSerializer.Serialize(file, this); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
