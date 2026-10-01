using System.Text.Json.Nodes;
using URemote.Core;

static class HostSessionTests
{
    // Independent upstream TypeScript fixture, with synthetic identity only.
    public const string OptionsBase64 = "CAEQ////////////ARoQCAIQAxgBIAEqBgiADxC4CCIMCDwQARiAHiDwECgBMggIgA8QuAgYPEACSgx3ZWItZGV2aWNlLTFQAVoOCAIQARgCIAIwAjgCQANiBjQuMzkuMQ==";
    public static void Run(Action<bool, string> check)
    {
        var bytes = Convert.FromBase64String(OptionsBase64);
        var options = StreamerConnectOptions.Decode(bytes);
        check(options.CaptureType == 1 && options.TypeValue == -1 && options.ClientType == 2 && options.ClientVersion == "4.39.1", "connect options decode upstream independent fixture");
        check(options.Capture?.Fps == 2 && options.Capture.CursorCapture && options.Capture.LocalResolution == new ScreenResolution(1920, 1080)
            && options.DecoderCapabilities.Single().Width == 3840 && options.FeatureFlags[8] == 3, "capture, codec and feature flag fields decode");
        check(!options.ToString().Contains("web-device-1"), "protobuf model log redacts device identity");
        try { StreamerConnectOptions.Decode(new byte[] { 0x4a, 0x7f, 0 }); check(false, "truncated protobuf rejected"); }
        catch (FormatException) { check(true, "truncated protobuf rejected"); }
        try { StreamerConnectOptions.Decode(new byte[] { 8, 1, 8, 2 }); check(false, "duplicate singular protobuf rejected"); }
        catch (FormatException) { check(true, "duplicate singular protobuf rejected"); }
        var sessions = new HostSignalSessions();
        var request = Frame("be-controlled", new JsonObject
        {
            ["client_id"] = "fixture-client", ["ice_id"] = "fixture-ice", ["app_control_id"] = "fixture-control",
            ["app_data"] = new JsonObject { ["_placeholder"] = true, ["num"] = 0 }
        }, [bytes]);
        check(sessions.Accept(request).Action == HostSignalAction.Requested && sessions.Peers.Count == 1, "be-controlled creates a peer session");
        var offer = Frame("soac", new JsonObject
        {
            ["client_id"] = "fixture-client", ["data"] = new JsonObject
            { ["ice_id"] = "fixture-ice", ["app_control_id"] = "fixture-control", ["type"] = "offer", ["sdp"] = "v=0\r\ns=fixture\r\n" }
        });
        check(sessions.Accept(offer).Action == HostSignalAction.Offer && sessions.Peers[0].Phase == HostPeerPhase.OfferReceived, "offer associated with correct peer");
        check(sessions.Accept(request).Action == HostSignalAction.Ignored && sessions.Peers[0].Phase == HostPeerPhase.OfferReceived, "duplicate control notification cannot reset peer state");
        var staleRelease = Frame("released", new JsonObject { ["client_id"] = "fixture-client", ["ice_id"] = "old-ice" });
        check(sessions.Accept(staleRelease).Action == HostSignalAction.Ignored && sessions.Peers.Count == 1, "stale release cannot remove new ICE session");
        var staleOfficialRelease = Frame("released", new JsonObject { ["user_device_id"] = "fixture-device", ["ice_id"] = "stale-ice" });
        check(sessions.Accept(staleOfficialRelease).Action == HostSignalAction.Ignored && sessions.Peers.Count == 1,
            "official stale release without client_id cannot remove active peer");
        var wrongClientRelease = Frame("released", new JsonObject { ["client_id"] = "another-client", ["ice_id"] = "fixture-ice" });
        check(sessions.Accept(wrongClientRelease).Action == HostSignalAction.Ignored && sessions.Peers.Count == 1,
            "conflicting client identity cannot release current peer");
        try { sessions.Accept(Frame("released", new JsonObject())); check(false, "missing release identity rejected"); }
        catch (FormatException) { check(sessions.Peers.Count == 1, "invalid release leaves peer state intact"); }
        var officialRelease = Frame("released", new JsonObject { ["user_device_id"] = "fixture-device", ["ice_id"] = "fixture-ice" });
        check(sessions.Accept(officialRelease).Action == HostSignalAction.Released && sessions.Peers.Count == 0,
            "official release uses ice_id without requiring client_id");
        sessions.Accept(request);
        var release = Frame("released", new JsonObject { ["client_id"] = "fixture-client", ["ice_id"] = "fixture-ice" });
        check(sessions.Accept(release).Action == HostSignalAction.Released && sessions.Peers.Count == 0, "release removes matching session");
        check(sessions.Accept(offer).Action == HostSignalAction.Ignored, "late offer cannot resurrect released session");
        var retry = sessions.Accept(request);
        check(retry.Action == HostSignalAction.Requested, "same controller can reconnect after release");
        sessions.Remove(retry.Peer!.Id);
        check(sessions.Peers.Count == 0 && sessions.Accept(request).Action == HostSignalAction.Requested,
            "failed media peer removal permits a fresh session without publisher restart");
    }
    private static UuSignalFrame Frame(string name, JsonObject data, byte[][]? binary = null) =>
        new(4, Packet: new(binary is null ? 2 : 5, Attachments: binary?.Length ?? 0, Data: new JsonArray(JsonValue.Create(name), data)), Attachments: binary ?? []);
}
