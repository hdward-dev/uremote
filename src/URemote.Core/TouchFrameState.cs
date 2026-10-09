namespace URemote.Core;

public enum TouchChangeKind { Down, Motion, Up }
public readonly record struct TouchChange(TouchChangeKind Kind, uint Id, double X, double Y)
{ public override string ToString() => "TouchChange(redacted)"; }

// One validated remote packet produces one complete input frame. Native resources
// and display mappings belong to the sink, never to client-supplied slot numbers.
public sealed class TouchFrameState
{
    private readonly HashSet<uint> active = [];
    public IReadOnlyList<TouchChange> Apply(HostTouchEvent input)
    {
        if (input.Phase is < 0 or > 4 || input.Points.Count > 16) throw new FormatException("Invalid touch frame.");
        var seen = new HashSet<uint>();
        foreach (var point in input.Points)
            if (!seen.Add(point.Id) || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
                || point.X < 0 || point.X > 1 || point.Y < 0 || point.Y > 1) throw new FormatException("Invalid touch contact.");
        if (input.Phase == 0) return [];
        if (input.Phase == 4 || input.Phase == 3 && input.Points.Count == 0) return Reset();
        var changes = new List<TouchChange>();
        if (input.Phase == 1 && active.Union(seen).Count() > 16) throw new FormatException("Too many active contacts.");
        foreach (var point in input.Points)
        {
            if (input.Phase == 3)
            {
                if (active.Remove(point.Id)) changes.Add(new(TouchChangeKind.Up,point.Id,0,0));
            }
            else if (active.Contains(point.Id)) changes.Add(new(TouchChangeKind.Motion,point.Id,point.X,point.Y));
            else if (input.Phase == 1)
            {
                active.Add(point.Id); changes.Add(new(TouchChangeKind.Down,point.Id,point.X,point.Y));
            }
            // A move without a down (including after switching screens) cannot
            // resurrect a released gesture. Wait for the next actual contact.
        }
        return changes;
    }
    public IReadOnlyList<TouchChange> Reset()
    {
        var changes = active.Select(id=>new TouchChange(TouchChangeKind.Up,id,0,0)).ToArray();
        active.Clear(); return changes;
    }
}
