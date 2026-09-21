using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace URemote.Core;

public sealed record HostPeerId(string ClientId, string IceId, string AppControlId)
{
    public override string ToString() => "HostPeerId(redacted)";
}
public enum HostPeerPhase { AwaitingOffer, OfferReceived, AnswerSent }
public sealed record HostPeerSession(HostPeerId Id, HostPeerPhase Phase, StreamerConnectOptions Options,
    int RemoteCandidateCount = 0, int OfferRevision = 0)
{
    public override string ToString() => $"HostPeerSession(Phase={Phase}, identity redacted)";
}
public enum HostSignalAction { Ignored, Requested, Offer, Candidate, Released }
public sealed record HostSignalUpdate(HostSignalAction Action, HostPeerSession? Peer = null,
    string? Sdp = null, JsonObject? Candidate = null)
{
    public override string ToString() => $"HostSignalUpdate(Action={Action}, payload redacted)";
}

// Protocol state only. AnswerSent means a local SDP was sent; it does NOT mean media is connected.
public sealed class HostSignalSessions
{
    private readonly Dictionary<(string Client, string Ice), HostPeerSession> peers = [];
    private readonly object gate = new();
    public IReadOnlyList<HostPeerSession> Peers { get { lock (gate) return peers.Values.ToArray(); } }

    public HostSignalUpdate Accept(UuSignalFrame frame)
    {
        if (frame.Packet?.Type is not (2 or 5) || frame.Packet.Data is not JsonArray values || values.Count < 2
            || values[0] is not JsonValue nameValue || !nameValue.TryGetValue<string>(out var name)
            || values[1] is not JsonObject payload) return new(HostSignalAction.Ignored);
        if (name is not ("be-controlled" or "soac" or "released")) return new(HostSignalAction.Ignored);
        lock (gate)
        {
            var clientId = RequiredString(payload, "client_id");
            var data = name == "soac" ? payload["data"] as JsonObject
                ?? throw new FormatException("Missing soac payload.") : payload;
            var iceId = RequiredString(data, "ice_id");
            var key = (clientId, iceId);
            if (name == "released")
                return peers.Remove(key, out var released) ? new(HostSignalAction.Released, released) : new(HostSignalAction.Ignored);
            if (name == "be-controlled")
            {
                var id = new HostPeerId(clientId, iceId, RequiredString(payload, "app_control_id"));
                if (peers.TryGetValue(key, out var existing))
                {
                    if (existing.Id != id) throw new FormatException("Conflicting peer identity.");
                    return new(HostSignalAction.Ignored, existing);
                }
                if (peers.Count >= 16) throw new FormatException("Too many simultaneous peer sessions.");
                var options = StreamerConnectOptions.Decode(Binary(frame, payload["app_data"]));
                var peer = new HostPeerSession(id, HostPeerPhase.AwaitingOffer, options);
                peers[key] = peer;
                return new(HostSignalAction.Requested, peer);
            }
            // Late packets for released/unknown peers must not resurrect a session.
            if (!peers.TryGetValue(key, out var session)) return new(HostSignalAction.Ignored);
            if (RequiredString(data, "app_control_id") != session.Id.AppControlId)
                throw new FormatException("soac does not belong to this peer session.");
            var kind = RequiredString(data, "type");
            if (kind == "offer")
            {
                var sdp = data["gzip_sdp"] is not null ? UuSignalReader.DecodeGzipSdp(frame) : RequiredString(data, "sdp");
                if (Encoding.UTF8.GetByteCount(sdp) > UuSignalReader.MaximumBytes || !sdp.StartsWith("v=0\r\n", StringComparison.Ordinal))
                    throw new FormatException("Invalid remote SDP.");
                session = session with { Phase = HostPeerPhase.OfferReceived, OfferRevision = session.OfferRevision + 1 };
                peers[key] = session;
                return new(HostSignalAction.Offer, session, sdp);
            }
            if (kind == "candidate")
            {
                if (data["candidate"] is not JsonObject candidate) throw new FormatException("Missing ICE candidate.");
                if (session.RemoteCandidateCount >= 256) throw new FormatException("Too many ICE candidates.");
                session = session with { RemoteCandidateCount = session.RemoteCandidateCount + 1 };
                peers[key] = session;
                return new(HostSignalAction.Candidate, session, Candidate: (JsonObject)candidate.DeepClone());
            }
            return new(HostSignalAction.Ignored);
        }
    }

    public async Task SendAnswerAsync(UuSignalClient client, HostPeerId id, string sdp, CancellationToken ct = default)
    {
        int revision;
        lock (gate)
        {
            if (!peers.TryGetValue((id.ClientId, id.IceId), out var peer) || peer.Id != id || peer.Phase != HostPeerPhase.OfferReceived)
                throw new InvalidOperationException("No pending offer for this peer.");
            revision = peer.OfferRevision;
        }
        var (payload, binary) = BuildAnswer(id, sdp);
        await client.SendEventAsync("soac", payload, [binary], ct: ct);
        lock (gate)
        {
            if (peers.TryGetValue((id.ClientId, id.IceId), out var peer) && peer.Id == id && peer.OfferRevision == revision)
                peers[(id.ClientId, id.IceId)] = peer with { Phase = HostPeerPhase.AnswerSent };
        }
    }

    public static (JsonObject Payload, byte[] Binary) BuildAnswer(HostPeerId id, string sdp)
    {
        if (!sdp.StartsWith("v=0\r\n", StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(sdp) > UuSignalReader.MaximumBytes)
            throw new ArgumentException("A valid local SDP is required.", nameof(sdp));
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(Encoding.UTF8.GetBytes(sdp));
        var payload = new JsonObject
        {
            ["client_id"] = id.ClientId,
            ["data"] = new JsonObject
            {
                ["type"] = "answer", ["app_control_id"] = id.AppControlId, ["ice_id"] = id.IceId,
                ["ice_network_type"] = 0, ["sdp"] = "", ["gzip_sdp"] = new JsonObject { ["_placeholder"] = true, ["num"] = 0 }
            }
        };
        return (payload, buffer.ToArray());
    }

    public void Remove(HostPeerId id) { lock (gate) peers.Remove((id.ClientId, id.IceId)); }
    public void Reset() { lock (gate) peers.Clear(); }
    private static string RequiredString(JsonObject data, string key)
    {
        if (data[key] is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrEmpty(text) || text.Length > 4096)
            throw new FormatException("Missing or invalid peer protocol field.") { Data = { ["field"] = key } };
        return text;
    }
    public static byte[] Binary(UuSignalFrame frame, JsonNode? placeholder)
    {
        if (placeholder is not JsonObject obj || obj["_placeholder"]?.GetValue<bool>() != true
            || obj["num"] is not JsonValue number || !number.TryGetValue<int>(out var index)
            || index < 0 || frame.Attachments is null || index >= frame.Attachments.Count)
            throw new FormatException("Missing binary protocol payload.");
        return frame.Attachments[index];
    }
}
