using URemote.Core;

internal static class PrivacyTests
{
    internal static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        byte[] Packet(string body) => Convert.FromHexString("0807AA01080A02082A3A02" + body);
        var normal = HostPrivacyProtocol.Decode(Packet("1002"))!;
        check(normal is { Kind: PrivacyRequestKind.Set, Mode: 2, Sequence: 7, RpcId: 42 }, "official privacy RPC field 7 and normal mode decoded");
        check(HostPrivacyProtocol.Decode(Packet("0801"))?.Mode == 2, "legacy enable-only privacy request maps to normal privacy");
        check(HostPrivacyProtocol.Decode(Packet("0800"))?.Mode == 1, "legacy disabled request restores local visibility");
        check(HostPrivacyProtocol.Decode(Packet("1003"))?.Mode == 3, "physical display off remains a distinct request");
        check(HostPrivacyProtocol.Decode(Packet("1001"))?.Mode == 1, "explicit privacy type controls disabling");
        check(Convert.ToHexString(HostPrivacyProtocol.Reply(normal, 2, true)) == "0807B2010C0A02082A1A06080010021802", "privacy success echoes RPC ID and confirmed actual mode");
        check(Convert.ToHexString(HostPrivacyProtocol.Reply(normal, 1, false)).EndsWith("080110021801"), "failed privacy request reports off rather than falsely claiming a black screen");
        check(Convert.ToHexString(HostPrivacyProtocol.State(9, 2, 2)) == "08097A082A06080010021802", "state change publishes PrivateScreenState field 5");
        var both = HostPrivacyProtocol.Decode(Convert.FromHexString("08016A040A020102"));
        check(both is { Kind: PrivacyRequestKind.Metrics, GeneralMetrics: true }, "packed general and setting metrics query recognized");
        check(HostPrivacyProtocol.Decode(Convert.FromHexString("6A0408010802"))?.GeneralMetrics == true, "unpacked repeated metric IDs accepted");
        check(HostPrivacyProtocol.Decode(Convert.FromHexString("6A020801")) is null, "general-only query stays with touch handler");
        var metrics = HostPrivacyProtocol.Metrics(1, 2, true, true);
        check(Convert.ToHexString(metrics) == "08016A0E0A0201021A020801220420013802", "metrics lists its sections and reports current privacy type and touch capability");
        check(HostPrivacyProtocol.Decode(metrics) is null, "metrics response never creates a response loop");
        check(HostPrivacyProtocol.Decode(Convert.FromHexString("6A0512037B7D20")) is null, "legacy JSON response string is never treated as a query");
        check(HostPrivacyProtocol.Decode(Array.Empty<byte>()) is null && HostPrivacyProtocol.Decode("{}"u8.ToArray()) is null, "empty and JSON messages ignored");
        reject(() => HostPrivacyProtocol.Decode(Packet("0802")), "invalid privacy boolean rejected");
        reject(() => HostPrivacyProtocol.Decode(Convert.FromHexString("AA010A0A02082A3A0410021001")), "duplicate privacy types rejected");
        var saver = HostPrivacyProtocol.Decode(Convert.FromHexString("0807AA01070A02082ACA0100"));
        check(saver?.Kind == PrivacyRequestKind.ScreenSaver && Convert.ToHexString(HostPrivacyProtocol.Reply(saver, 1, true)) == "0807B201070A02082AAA0100", "screen saver setup is acknowledged without opening remote paths");
        var echo = HostControlEcho.Reply(ControllerProtocol.Echo(7), 8, 9, true, captureSettingVersion: 4, privacyScreenVersion: 1)!;
        check(Convert.ToHexString(echo).Contains("080418032001"), "KDE host advertises normal privacy and privacy settings metrics only");
    }
}
