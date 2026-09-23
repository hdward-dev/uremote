using System.Text.Json;
namespace URemote.Core;

public sealed class AssistanceRequest
{
    private string code;
    public AssistanceRequest(string connectId, string connectCode) { Validate(connectId, connectCode); ConnectId = connectId; code = connectCode; }
    public string ConnectId { get; }
    public string TakeCode() => Interlocked.Exchange(ref code, "");
    public static void Validate(string id, string code)
    {
        if (id.Length is < 6 or > 12 || !id.All(char.IsAsciiDigit)) throw new ArgumentException("协助码应为 6–12 位数字。");
        if (string.IsNullOrWhiteSpace(code) || code.Length > 128 || code.Any(char.IsControl)) throw new ArgumentException("请输入对方的验证码。");
    }
    public override string ToString() => "AssistanceRequest(redacted)";
}
public sealed record AssistanceRoom(HostRoomConfiguration Room, int Platform)
{
    public override string ToString() => "AssistanceRoom(redacted)";
    public static AssistanceRoom Parse(JsonElement root)
    {
        var data = root.GetProperty("data");
        var containers = new List<JsonElement> { data, root };
        foreach (var parent in new[] { data, root })
            foreach (var key in new[] { "room_config", "roomConfig", "room_info", "roomInfo", "streamer_room_config", "streamerRoomConfig" })
                if (parent.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Object) containers.Add(value);
        var platform = 0;
        foreach (var key in new[] { "publisher_platform", "device_platform", "platform" })
        {
            foreach (var container in containers)
                if (container.TryGetProperty(key, out var value) && int.TryParse(value.ToString(), out var number) && number > 0) { platform = number; break; }
            if (platform > 0) break;
        }
        if (platform == 0) throw new InvalidDataException("Missing assistance target platform.");
        var config = containers.FirstOrDefault(x => x.TryGetProperty("signaling_server", out _));
        if (config.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Missing assistance room.");
        return new(UuMacHostProtocol.ParseRoomResponse(JsonSerializer.Serialize(new { code = 0, data = config })), platform);
    }
}
