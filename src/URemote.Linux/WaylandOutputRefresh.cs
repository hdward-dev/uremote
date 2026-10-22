using System.Buffers.Binary;
namespace URemote.Linux;

public readonly record struct WaylandOutputMode(uint Name, int X, int Y, int Width, int Height, int MilliHz);

public static class WaylandOutputRefresh
{
    // wl_output.mode refresh is in millihertz; only CURRENT (bit 0) describes the active mode.
    public static async Task<IReadOnlyDictionary<uint, int>> ReadAsync(CancellationToken ct = default)
    {
        var outputs = await ReadOutputsAsync(ct);
        return outputs.Where(item => item.MilliHz > 0).ToDictionary(item => item.Name, item => item.MilliHz);
    }

    public static async Task<IReadOnlyList<WaylandOutputMode>> ReadOutputsAsync(CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var connection = await WaylandConnection.OpenAsync(deadline.Token);
        var bindings = new Dictionary<uint, uint>();
        foreach (var output in connection.Globals.Where(g => g.Interface == "wl_output"))
            bindings.Add(await connection.BindAsync(output, 1, deadline.Token), output.Name);
        var sync = connection.AllocateId();
        await connection.SendAsync(1, 0, WaylandConnection.Words(sync), deadline.Token);
        var partials = new Dictionary<uint, MutableMode>();
        while (true)
        {
            var e = await connection.ReadEventAsync(deadline.Token);
            if (e.Target == sync && e.Opcode == 0)
                return partials.Values.Select(item => new WaylandOutputMode(item.Name, item.X, item.Y, item.Width, item.Height, item.MilliHz)).ToArray();
            if (!bindings.TryGetValue(e.Target, out var global)) continue;
            if (!partials.TryGetValue(global, out var mode)) partials[global] = mode = new MutableMode { Name = global };
            if (e.Opcode == 0 && e.Data.Length >= 8)
            {
                mode.X = BinaryPrimitives.ReadInt32LittleEndian(e.Data);
                mode.Y = BinaryPrimitives.ReadInt32LittleEndian(e.Data.AsSpan(4));
            }
            else if (e.Opcode == 1 && e.Data.Length == 16)
            {
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(e.Data);
                var width = BinaryPrimitives.ReadInt32LittleEndian(e.Data.AsSpan(4));
                var height = BinaryPrimitives.ReadInt32LittleEndian(e.Data.AsSpan(8));
                var refresh = BinaryPrimitives.ReadInt32LittleEndian(e.Data.AsSpan(12));
                if ((flags & 1) != 0 && refresh > 0 && width > 0 && height > 0)
                { mode.Width = width; mode.Height = height; mode.MilliHz = refresh; }
            }
        }
    }

    private sealed class MutableMode
    {
        internal uint Name;
        internal int X, Y, Width, Height, MilliHz;
    }
}
