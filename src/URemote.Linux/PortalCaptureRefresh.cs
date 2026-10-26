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
            if (match.Node == 0 && next.Count == previous.Count)
            {
                var byIndex = next[i];
                if (byIndex.Node != 0 && !used.Contains(byIndex.Node)) match = byIndex;
            }
            if (match.Node == 0 || !used.Add(match.Node)) continue;
            map[old.Node] = match.Node;
        }
        return map;
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
