using System.Text.Json.Nodes;
using URemote.Core;
internal static class ConnectionInfoChecks
{
    public static void Run()
    {
        var options = StreamerConnectOptions.Decode(ControllerProtocol.ConnectOptions("fixture-device", assistance: true));
        var info = HostControlConnection.FromRequest(new JsonObject {
            ["controller_platform"] = 1, ["subscriber_country"] = "中国", ["subscriber_province"] = "上海", ["subscriber_city"] = "上海",
            ["publisher_city"] = "wrong-side"
        }, options);
        if (info.DeviceId != "fixture-device" || info.PlatformName != "Windows" || !info.Assistance
            || info.Location != "中国 · 上海" || info.SessionName != "远程控制" || info.ElapsedText != "00:00:00")
            throw new Exception("Incorrect control connection metadata.");
        if (HostControlConnection.FormatElapsed(TimeSpan.FromHours(25) + TimeSpan.FromSeconds(61)) != "25:01:01"
            || HostControlConnection.FormatElapsed(TimeSpan.FromSeconds(-1)) != "00:00:00")
            throw new Exception("Invalid connection duration formatting.");
        if (info.ToString().Contains("fixture-device")) throw new Exception("Connection identity leaked into diagnostics.");
        Console.WriteLine("PASS: control device metadata, source location, monotonic duration and redacted diagnostics");
    }
}
