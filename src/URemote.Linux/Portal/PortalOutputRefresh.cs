namespace URemote.Linux;

public readonly record struct PortalStreamBox(uint Node, int X, int Y, int Width, int Height);

// Portal stream ids are PipeWire nodes, not wl_output names. Match geometry, then fall back
// so a single shared monitor still receives its real refresh instead of 0.
internal static class PortalOutputRefresh
{
    internal static Dictionary<uint, int> Map(IReadOnlyList<PortalStreamBox> streams, IReadOnlyList<WaylandOutputMode> outputs)
    {
        var result = new Dictionary<uint, int>();
        var available = outputs.Where(item => item.MilliHz > 0).ToList();
        if (streams.Count == 0 || available.Count == 0) return result;
        var used = new HashSet<uint>();
        foreach (var stream in streams)
        {
            var match = Find(available, used, item => stream.Width > 0 && stream.Height > 0
                && item.Width == stream.Width && item.Height == stream.Height && item.X == stream.X && item.Y == stream.Y);
            if (match.MilliHz == 0)
                match = Find(available, used, item => item.X == stream.X && item.Y == stream.Y && (stream.X != 0 || stream.Y != 0 || available.Count == 1));
            if (match.MilliHz == 0)
                match = Find(available, used, item => stream.Width > 0 && stream.Height > 0 && item.Width == stream.Width && item.Height == stream.Height);
            if (match.MilliHz <= 0) continue;
            result[stream.Node] = match.MilliHz;
            used.Add(match.Name);
        }
        if (result.Count == streams.Count) return result;
        if (available.Count == 1)
        {
            foreach (var stream in streams) result[stream.Node] = available[0].MilliHz;
            return result;
        }
        var leftStreams = streams.Where(item => !result.ContainsKey(item.Node)).OrderBy(item => item.X).ThenBy(item => item.Y).ThenBy(item => item.Node).ToList();
        var leftOutputs = available.Where(item => !used.Contains(item.Name)).OrderBy(item => item.X).ThenBy(item => item.Y).ThenBy(item => item.Name).ToList();
        if (leftOutputs.Count == 0) return result;
        if (leftOutputs.Select(item => item.MilliHz).Distinct().Count() == 1)
        {
            foreach (var stream in leftStreams) result[stream.Node] = leftOutputs[0].MilliHz;
            return result;
        }
        var pairs = Math.Min(leftStreams.Count, leftOutputs.Count);
        for (var i = 0; i < pairs; i++) result[leftStreams[i].Node] = leftOutputs[i].MilliHz;
        return result;
    }
    private static WaylandOutputMode Find(List<WaylandOutputMode> available, HashSet<uint> used, Func<WaylandOutputMode, bool> match)
        => available.FirstOrDefault(item => !used.Contains(item.Name) && match(item));
}
