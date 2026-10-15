namespace URemote.Linux;

internal static class PortalAuthorization
{
    // Select both sources and devices before the only Start request. That request
    // is the consent boundary for screen sharing and every input capability.
    internal static async Task<PortalValue> StartAsync(PortalBus bus, string session, bool input, CancellationToken ct)
    {
        if (input)
        {
            var available = await bus.PropertyAsync(PortalBus.RemoteDesktop, "AvailableDeviceTypes", ct);
            if ((available & 3) != 3) throw new NotSupportedException("桌面不支持键鼠控制。");
            using var types = PortalValue.UInt(available & 7);
            using var selected = await bus.RequestAsync(PortalBus.RemoteDesktop, "SelectDevices", session, false, [("types", types)], ct);
        }
        using var monitor = PortalValue.UInt(1); using var multiple = PortalValue.Bool(true);
        var modes = await bus.PropertyAsync(PortalBus.ScreenCast, "AvailableCursorModes", ct);
        using var cursor = PortalValue.UInt((modes & 2) != 0 ? 2u : 1u);
        using (var selected = await bus.RequestAsync(PortalBus.ScreenCast, "SelectSources", session, false,
            [("types", monitor), ("multiple", multiple), ("cursor_mode", cursor)], ct)) { }
        return await bus.RequestAsync(input ? PortalBus.RemoteDesktop : PortalBus.ScreenCast, "Start", session, true, [], ct);
    }
}
