namespace URemote.Linux;

public readonly record struct PortalStreamBox(uint Node, int X, int Y, int Width, int Height, int RefreshMilliHz = 0, int ModeWidth = 0, int ModeHeight = 0);

public readonly record struct PortalDisplayBinding(int RefreshMilliHz, int Width, int Height);

// Portal stream ids are PipeWire nodes, not wl_output names. KDE fractional scale makes
// logical_size (and sometimes position) differ from the hardware mode, so a 2560x1440 panel
// must not inherit the neighboring 3840x2160 mode or its refresh.
internal static class PortalOutputRefresh
{
    internal static Dictionary<uint, int> Map(IReadOnlyList<PortalStreamBox> streams, IReadOnlyList<WaylandOutputMode> outputs)
        => Bind(streams, outputs).Where(item => item.Value.RefreshMilliHz > 0)
            .ToDictionary(item => item.Key, item => item.Value.RefreshMilliHz);

    internal static Dictionary<uint, PortalDisplayBinding> Bind(IReadOnlyList<PortalStreamBox> streams, IReadOnlyList<WaylandOutputMode> outputs)
    {
        var result = new Dictionary<uint, PortalDisplayBinding>();
        var available = outputs.Where(item => item.MilliHz > 0 && item.Width > 0 && item.Height > 0).ToList();
        if (streams.Count == 0 || available.Count == 0) return result;
        var used = new HashSet<uint>();
        foreach (var stream in streams)
        {
            var match = Find(available, used, item => stream.Width > 0 && stream.Height > 0
                && item.Width == stream.Width && item.Height == stream.Height && item.X == stream.X && item.Y == stream.Y);
            if (match.MilliHz == 0)
                match = Find(available, used, item => item.X == stream.X && item.Y == stream.Y && KnownPosition(stream, streams.Count));
            if (match.MilliHz == 0)
                match = Find(available, used, item => stream.Width > 0 && stream.Height > 0 && item.Width == stream.Width && item.Height == stream.Height);
            if (match.MilliHz <= 0 || !used.Add(match.Name)) continue;
            result[stream.Node] = new(match.MilliHz, match.Width, match.Height);
        }
        if (result.Count == streams.Count) return result;
        var unknown = streams.Where(item => !result.ContainsKey(item.Node)).ToList();
        // One shared monitor can fill a stream that has no geometry. Several unsized streams
        // may share its refresh, but not its pixel size: that is how a 1440p panel became 4K.
        if (available.Count == 1 && streams.Count == 1)
        {
            result[streams[0].Node] = new(available[0].MilliHz, available[0].Width, available[0].Height);
            return result;
        }
        if (available.Count == 1 && unknown.All(item => item.Width <= 0 && item.X == 0 && item.Y == 0))
        {
            foreach (var stream in unknown)
                result[stream.Node] = new(available[0].MilliHz, 0, 0);
            return result;
        }
        var leftStreams = unknown.OrderBy(item => item.X).ThenBy(item => item.Y).ThenBy(item => item.Node).ToList();
        var leftOutputs = available.Where(item => !used.Contains(item.Name)).OrderBy(item => item.X).ThenBy(item => item.Y).ThenBy(item => item.Name).ToList();
        if (leftOutputs.Count == 0 || leftStreams.Count != leftOutputs.Count) return result;
        if (leftOutputs.Select(item => item.MilliHz).Distinct().Count() == 1 && leftStreams.All(item => item.Width <= 0))
        {
            foreach (var stream in leftStreams)
                result[stream.Node] = new(leftOutputs[0].MilliHz, 0, 0);
            return result;
        }
        for (var i = 0; i < leftStreams.Count; i++)
            result[leftStreams[i].Node] = new(leftOutputs[i].MilliHz, leftOutputs[i].Width, leftOutputs[i].Height);
        return result;
    }

    // A stream parked at the origin with a real size is that panel. A stream with neither
    // size nor a non-zero position has not reported geometry and must not claim the left monitor.
    private static bool KnownPosition(PortalStreamBox stream, int streamCount)
        => stream.X != 0 || stream.Y != 0 || stream.Width > 0 || streamCount == 1;

    private static WaylandOutputMode Find(List<WaylandOutputMode> available, HashSet<uint> used, Func<WaylandOutputMode, bool> match)
        => available.FirstOrDefault(item => !used.Contains(item.Name) && match(item));
}
