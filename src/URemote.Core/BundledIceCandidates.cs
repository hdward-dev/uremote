using System.Text.Json.Nodes;
namespace URemote.Core;

public sealed class BundledIceCandidates
{
    private readonly List<string?> mids = [];
    private readonly HashSet<string> bundled = [];
    public BundledIceCandidates(string sdp)
    {
        foreach (var raw in sdp.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("m=")) mids.Add(null);
            else if (line.StartsWith("a=mid:") && mids.Count > 0) mids[^1] = line[6..];
            else if (line.StartsWith("a=group:BUNDLE ")) foreach (var mid in line[15..].Split(' ', StringSplitOptions.RemoveEmptyEntries)) bundled.Add(mid);
        }
    }
    public JsonObject Normalize(JsonObject candidate)
    {
        var result = (JsonObject)candidate.DeepClone();
        var index = result["sdpMLineIndex"]?.GetValue<int>() ?? 0;
        var mid = result["sdpMid"]?.GetValue<string>();
        if (mid is not null) { var found = mids.IndexOf(mid); if (found < 0) return result; index = found; }
        if (index <= 0 || index >= mids.Count || mids[0] is not { } first || mids[index] is not { } selected
            || !bundled.Contains(first) || !bundled.Contains(selected)) return result;
        result["sdpMLineIndex"] = 0; result["sdpMid"] = first;
        return result;
    }
}
