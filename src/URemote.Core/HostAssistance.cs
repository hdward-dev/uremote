using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
namespace URemote.Core;

// Temporary codes live only in this process and rotate on restart or explicit refresh.
public static class HostAssistance
{
    private static readonly ConcurrentDictionary<string,string> Codes = new();
    private sealed class Permission
    {
        public CancellationTokenSource Stop = new();
        public string CustomCode = "";
        public bool UseCustom;
    }
    private static readonly ConcurrentDictionary<string,Permission> Permissions = new();
    public static CancellationToken PermissionToken(string deviceId)
    {
        var p = Permissions.GetOrAdd(deviceId, _ => new());
        lock(p) return p.Stop.Token;
    }
    public static bool IsEnabled(string deviceId) => !PermissionToken(deviceId).IsCancellationRequested;
    public static void SetEnabled(string deviceId, bool enabled)
    {
        var p = Permissions.GetOrAdd(deviceId, _ => new());
        lock(p)
        {
            if(enabled && p.Stop.IsCancellationRequested) p.Stop = new();
            else if(!enabled && !p.Stop.IsCancellationRequested) { p.Stop.Cancel(); Rotate(deviceId); }
        }
    }
    public static void ValidateCustomCode(string code)
    {
        if (code.Length is < 8 or > 32 || code.Any(c => c < 33 || c > 126)
            || !code.Any(char.IsAsciiLetter) || !code.Any(char.IsAsciiDigit))
            throw new ArgumentException("自定义验证码需为 8–32 位，包含字母和数字，不含空格。");
    }
    public static void Configure(string deviceId, string customCode, bool useCustom)
    {
        if (customCode.Length > 0 || useCustom) ValidateCustomCode(customCode);
        var p = Permissions.GetOrAdd(deviceId, _ => new());
        lock (p) { p.CustomCode = customCode; p.UseCustom = useCustom; }
    }
    // The selected mode controls presentation only, never authentication.
    public static string DisplayCode(string deviceId)
    {
        var p = Permissions.GetOrAdd(deviceId, _ => new());
        lock (p) return p.UseCustom ? p.CustomCode : Code(deviceId);
    }
    public static AssistanceSignatures Signatures(string deviceId, string salt)
    {
        var p = Permissions.GetOrAdd(deviceId, _ => new());
        lock (p)
        {
            if (p.Stop.IsCancellationRequested) return new(false, "", "");
            return new(true, Sign(salt, Code(deviceId)), p.CustomCode.Length > 0 ? Sign(salt, p.CustomCode) : "");
        }
    }
    public static bool RotateAfterConnection(string deviceId)
    {
        var p = Permissions.GetOrAdd(deviceId, _ => new());
        lock (p) { Rotate(deviceId); return true; }
    }
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public static string Code(string deviceId) => Codes.GetOrAdd(deviceId, _ => Generate());
    public static string Rotate(string deviceId) => Codes[deviceId] = Generate();
    private static string Generate() => new(Enumerable.Range(0,8).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
    public static string Sign(string salt, string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(salt + code)));
    public static (string ControlId,string Salt)? Challenge(JsonNode? node, int depth = 0)
    {
        if(depth > 4 || node is null) return null;
        if(node is JsonObject obj)
        {
            if(obj["control_id"] is JsonValue id && id.TryGetValue<string>(out var control)
                && obj["salt"] is JsonValue salt && salt.TryGetValue<string>(out var value)
                && control.Length is > 0 and <= 256 && value.Length is > 0 and <= 1024) return (control,value);
            foreach(var p in obj.Take(32)) if(Challenge(p.Value,depth+1) is { } result) return result;
        }
        if(node is JsonArray array) foreach(var item in array.Take(16)) if(Challenge(item,depth+1) is { } result) return result;
        return null;
    }
}
public sealed record HostAssistanceInfo(string ConnectId)
{
    public override string ToString() => "HostAssistanceInfo(redacted)";
}

public sealed record AssistanceSignatures(bool Allowed, string Primary, string Backup)
{
    public override string ToString() => "AssistanceSignatures(redacted)";
}
