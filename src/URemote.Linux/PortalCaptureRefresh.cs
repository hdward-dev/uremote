namespace URemote.Linux;

// Unlock/unblank must refresh a stuck PipeWire lease without dropping the viewer.
public readonly record struct PortalRecycleDecision(bool CancelPreview, bool ClearControlRoom, bool RefreshCapture)
{
    public static PortalRecycleDecision For(string reason) => reason is "unlock" or "unblank"
        ? new(CancelPreview: false, ClearControlRoom: false, RefreshCapture: true)
        : new(CancelPreview: true, ClearControlRoom: true, RefreshCapture: false);

    // The control room is cleared only when a recycle actually ends the preview and the host is still running.
    // Viewer disconnect and host stop clear it on their own paths.
    public static bool ShouldClearControlRoom(PortalRecycleDecision decision, bool hostStopping)
        => !hostStopping && decision.CancelPreview && decision.ClearControlRoom;
}

internal static class PortalStreamMap
{
    internal static Dictionary<uint, uint> Match(IReadOnlyList<PortalStreamBox> previous, IReadOnlyList<PortalStreamBox> next)
    {
        var map = new Dictionary<uint, uint>();
        var used = new HashSet<uint>();
        for (var i = 0; i < previous.Count; i++)
        {
            var old = previous[i];
            if (old.Node == 0) continue;
            var match = next.FirstOrDefault(box => box.Node != 0 && !used.Contains(box.Node)
                && old.Width > 0 && old.Height > 0 && box.Width == old.Width && box.Height == old.Height && box.X == old.X && box.Y == old.Y);
            // Logical size can change across a portal restore while the panel stays put.
            // Pair that panel by position before falling back to order, or the left screen
            // becomes the right screen's PipeWire node.
            if (match.Node == 0)
                match = next.FirstOrDefault(box => box.Node != 0 && !used.Contains(box.Node)
                    && box.X == old.X && box.Y == old.Y && (old.X != 0 || old.Y != 0 || old.Width > 0));
            if (match.Node == 0 && next.Count == previous.Count)
            {
                var distinct = previous.Select(box => (box.X, box.Y)).Distinct().Count() > 1
                    && next.Select(box => (box.X, box.Y)).Distinct().Count() > 1;
                var byOrder = distinct
                    ? Ranked(next, Rank(previous, old.Node))
                    : next[i];
                if (byOrder.Node != 0 && !used.Contains(byOrder.Node)) match = byOrder;
            }
            if (match.Node == 0 || !used.Add(match.Node)) continue;
            map[old.Node] = match.Node;
        }
        return map;
    }

    private static int Rank(IReadOnlyList<PortalStreamBox> layout, uint node)
    {
        var ordered = layout.OrderBy(box => box.X).ThenBy(box => box.Y).ThenBy(box => box.Node).ToArray();
        for (var i = 0; i < ordered.Length; i++)
            if (ordered[i].Node == node) return i;
        return -1;
    }

    private static PortalStreamBox Ranked(IReadOnlyList<PortalStreamBox> layout, int rank)
    {
        if (rank < 0) return default;
        var ordered = layout.OrderBy(box => box.X).ThenBy(box => box.Y).ThenBy(box => box.Node).ToArray();
        return rank < ordered.Length ? ordered[rank] : default;
    }
}

internal static class PortalTopology
{
    internal static string Signature(IReadOnlyList<PortalStreamBox> layout) =>
        string.Join(";", layout.OrderBy(box => box.X).ThenBy(box => box.Y).ThenBy(box => box.Width).ThenBy(box => box.Height)
            .Select(box => box.X.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
                + box.Y.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
                + box.Width.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
                + box.Height.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}
