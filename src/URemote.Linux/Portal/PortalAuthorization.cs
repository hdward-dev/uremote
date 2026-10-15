namespace URemote.Linux;

internal static class PortalAuthorization
{
    // Select both sources and devices before the only Start request. That request
    // is the consent boundary for screen sharing and every input capability.
    internal static async Task<PortalValue> StartAsync(PortalBus bus, string session, bool input, CancellationToken ct, PortalRestoreStore? restoreStore = null)
    {
        var iface = input ? PortalBus.RemoteDesktop : PortalBus.ScreenCast;
        var persistent = restoreStore is not null && await bus.PropertyAsync(iface, "version", ct) >= (input ? 2u : 4u);
        var saved = persistent ? restoreStore!.Take() : null;
        using var persist = PortalValue.UInt(2);
        using var restore = saved is null ? null : PortalValue.String(saved);
        var persistence = new List<(string Key, PortalValue Value)>();
        if (persistent) persistence.Add(("persist_mode", persist));
        if (restore is not null) persistence.Add(("restore_token", restore));
        if (input)
        {
            var available = await bus.PropertyAsync(PortalBus.RemoteDesktop, "AvailableDeviceTypes", ct);
            if ((available & 3) != 3) throw new NotSupportedException("桌面不支持键鼠控制。");
            using var types = PortalValue.UInt(available & 7);
            using var selected = await bus.RequestAsync(PortalBus.RemoteDesktop, "SelectDevices", session, false, [("types", types), ..persistence], ct);
        }
        using var monitor = PortalValue.UInt(1); using var multiple = PortalValue.Bool(true);
        var modes = await bus.PropertyAsync(PortalBus.ScreenCast, "AvailableCursorModes", ct);
        using var cursor = PortalValue.UInt((modes & 2) != 0 ? 2u : 1u);
        using (var selected = await bus.RequestAsync(PortalBus.ScreenCast, "SelectSources", session, false,
            [("types", monitor), ("multiple", multiple), ("cursor_mode", cursor), ..(input ? [] : persistence)], ct)) { }
        var result = await bus.RequestAsync(iface, "Start", session, true, [], ct);
        try
        {
            using var next = result.Get("restore_token");
            if (persistent) restoreStore!.Save(next?.Text());
            Console.WriteLine("portal-persistence=" + (persistent && next is not null ? "saved" : "unavailable"));
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}
