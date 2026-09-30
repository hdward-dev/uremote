using System.Buffers.Binary;
namespace URemote.Linux;

public static class WaylandOutputRefresh
{
    // wl_output.mode refresh is in millihertz; only CURRENT (bit 0) describes the active mode.
    public static async Task<IReadOnlyDictionary<uint, int>> ReadAsync(CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var connection = await WaylandConnection.OpenAsync(deadline.Token);
        var bindings = new Dictionary<uint, uint>();
        foreach (var output in connection.Globals.Where(g => g.Interface == "wl_output"))
            bindings.Add(await connection.BindAsync(output, 1, deadline.Token), output.Name);
        var sync = connection.AllocateId();
        await connection.SendAsync(1, 0, WaylandConnection.Words(sync), deadline.Token);
        var rates = new Dictionary<uint, int>();
        while (true)
        {
            var e = await connection.ReadEventAsync(deadline.Token);
            if (e.Target == sync && e.Opcode == 0) return rates;
            if (!bindings.TryGetValue(e.Target, out var global) || e.Opcode != 1 || e.Data.Length != 16) continue;
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(e.Data);
            var refresh = BinaryPrimitives.ReadInt32LittleEndian(e.Data.AsSpan(12));
            if ((flags & 1) != 0 && refresh > 0) rates[global] = refresh;
        }
    }
}
