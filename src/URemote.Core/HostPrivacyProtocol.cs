namespace URemote.Core;

public enum PrivacyRequestKind { Set, Metrics, ScreenSaver }
public sealed record PrivacyRequest(PrivacyRequestKind Kind, ulong Sequence, ulong RpcId = 0, int Mode = 1, bool GeneralMetrics = false);

public static class HostPrivacyProtocol
{
    // Independently mapped from official SwiftProtobuf metadata: RPC request 7,
    // response 3; modes 1=off, 2=privacy overlay, 3=physical display off.
    public static PrivacyRequest? Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is 0 or > 8192 || bytes.Span[0] == '{') return null;
        var root = ProtoFields.Read(bytes);
        var sequence = root.Items.FirstOrDefault(f => f.Tag == 1 && f.WireType == 0)?.Value ?? 0;
        if (root.Bytes(21) is { } request)
        {
            var rpc = ProtoFields.Read(request);
            var setting = rpc.Bytes(7); var saver = rpc.Bytes(25);
            if (setting is null && saver is null) return null;
            if (rpc.Items.Count(f => f.Tag is >= 2 and <= 29 && f.WireType == 2) > 1) throw new FormatException("Ambiguous privacy request.");
            if (rpc.Bytes(1) is not { } header) throw new FormatException("Missing privacy RPC header.");
            var id = ProtoFields.Read(header).Items.FirstOrDefault(f => f.Tag == 1 && f.WireType == 0)?.Value ?? 0;
            if (setting is null) return new(PrivacyRequestKind.ScreenSaver, sequence, id);
            var values = ProtoFields.Read(setting.Value);
            var enabled = values.Bool(1); var mode = values.Int(2);
            return new(PrivacyRequestKind.Set, sequence, id, mode == 0 ? enabled ? 2 : 1 : mode);
        }
        if (root.Bytes(13) is not { } metricsBytes) return null;
        var metrics = ProtoFields.Read(metricsBytes, [1]);
        if (metrics.Text(2).Length != 0 || metrics.Bytes(3) is not null || metrics.Bytes(4) is not null) return null;
        var requested = new List<int>();
        foreach (var field in metrics.Items.Where(f => f.Tag == 1))
        {
            if (field.WireType == 0) requested.Add((int)field.Value);
            else if (field.WireType == 2)
            {
                // Known metric IDs are single-byte enum values; retain unknown IDs
                // as unsupported rather than treating an empty query as a settings query.
                foreach (var value in field.Data.Span) { if (value >= 128) throw new FormatException("Invalid metric ID."); requested.Add(value); }
            }
            else throw new FormatException("Invalid metric list.");
        }
        return requested.Contains(2) ? new(PrivacyRequestKind.Metrics, sequence, GeneralMetrics: requested.Contains(1)) : null;
    }
    public static byte[] Reply(PrivacyRequest request, int currentMode, bool success)
    {
        var result = request.Kind == PrivacyRequestKind.ScreenSaver
            ? Blob(21, success ? [] : Int(1, 1))
            : Blob(3, StateBody(success ? 0 : 1, request.Mode, currentMode));
        return Join(Int(1, request.Sequence), Blob(22, Join(Blob(1, Int(1, request.RpcId)), result)));
    }
    public static byte[] State(ulong sequence, int requestedMode, int currentMode, bool success = true)
        => Join(Int(1, sequence), Blob(15, Blob(5, StateBody(success ? 0 : 1, requestedMode, currentMode))));
    public static byte[] Metrics(ulong sequence, int currentMode, bool general, bool supportTouch)
    {
        var sections = general ? new byte[] { 1, 2 } : new byte[] { 2 };
        var settings = Join(Int(4, currentMode == 1 ? 0u : 1u), Int(7, (ulong)currentMode));
        // Field 2 is the legacy JSON response string, not a boolean. Structured
        // general/setting sections are sufficient for current native clients.
        return Join(Int(1, sequence), Blob(13, Join(Blob(1, sections),
            general ? Blob(3, Int(1, supportTouch ? 1u : 0u)) : [], Blob(4, settings))));
    }
    private static byte[] StateBody(int error, int requested, int current)
        => Join(Int(1, (ulong)error), Int(2, unchecked((ulong)requested)), Int(3, (ulong)current));
    private static byte[] Join(params byte[][] parts) => FileTransferProtocol.Join(parts);
    private static byte[] Int(uint tag, ulong value) => FileTransferProtocol.Int(tag, value);
    private static byte[] Blob(uint tag, byte[] bytes) => FileTransferProtocol.Blob(tag, bytes);
}
