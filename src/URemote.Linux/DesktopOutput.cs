using System.Buffers.Binary;
using System.Text;

namespace URemote.Linux;

public sealed record DesktopOutput(uint Id, string Key, string Name, int RefreshMilliHz = 0);

public static class DesktopOutputSelection
{
    public static uint[] Select(IReadOnlyList<DesktopOutput> available, IReadOnlyCollection<string> keys)
    {
        var unique = available.GroupBy(x => x.Key).Where(g => g.Count() == 1).Select(g => g.Single());
        return unique.Where(x => keys.Contains(x.Key, StringComparer.Ordinal)).Select(x => x.Id).ToArray();
    }
}

internal static class WaylandOutputDetails
{
    internal static async Task<IReadOnlyList<DesktopOutput>> ReadAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var connection = await WaylandConnection.OpenAsync(deadline.Token);
        var bindings = new Dictionary<uint, uint>();
        foreach (var output in connection.Globals.Where(g => g.Interface == "wl_output"))
            bindings.Add(await connection.BindAsync(output, Math.Min(4u, output.Version), deadline.Token), output.Name);
        var sync = connection.AllocateId();
        await connection.SendAsync(1, 0, WaylandConnection.Words(sync), deadline.Token);
        var names = new Dictionary<uint, string>(); var rates = new Dictionary<uint, int>();
        while (true)
        {
            var e = await connection.ReadEventAsync(deadline.Token);
            if (e.Target == sync && e.Opcode == 0) break;
            if (!bindings.TryGetValue(e.Target, out var global)) continue;
            if (e.Opcode == 1 && e.Data.Length == 16)
            {
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(e.Data);
                var rate = BinaryPrimitives.ReadInt32LittleEndian(e.Data.AsSpan(12));
                if ((flags & 1) != 0 && rate > 0) rates[global] = rate;
            }
            if (e.Opcode == 4 && e.Data.Length >= 8)
            {
                var length = BinaryPrimitives.ReadUInt32LittleEndian(e.Data);
                if (length is < 2 or > 4096 || length > e.Data.Length - 4 || e.Data[3 + length] != 0)
                    throw new FormatException("Invalid output name.");
                names[global] = new UTF8Encoding(false, true).GetString(e.Data, 4, (int)length - 1);
            }
        }
        // Old wl_output versions have no persistent connector name. Scope the
        // fallback to this desktop session so a reboot cannot silently select it.
        var scope = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "wayland-0";
        var boot = File.Exists("/proc/sys/kernel/random/boot_id") ? File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim() : Environment.ProcessId.ToString();
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "";
        var socketPath = Path.IsPathRooted(scope) ? scope : Path.Combine(runtime, scope);
        var socketCreated = File.GetCreationTimeUtc(socketPath).Ticks;
        return bindings.Values.Select(id => new DesktopOutput(id,
            names.TryGetValue(id, out var name) ? "wayland:" + name : $"wayland-session:{boot}:{scope}:{socketCreated}:{id}",
            names.GetValueOrDefault(id) ?? "显示器 " + id, rates.GetValueOrDefault(id))).ToArray();
    }
}
