namespace URemote.Linux;

internal static class PortalAuthorization
{
    // Select both sources and devices before the only Start request. That request
    // is the consent boundary for screen sharing and every input capability.
    internal static async Task<PortalValue> StartAsync(PortalBus bus, string session, bool input, CancellationToken ct, PortalRestoreStore? restoreStore = null)
    {
        var iface = input ? PortalBus.RemoteDesktop : PortalBus.ScreenCast;
        var persistent = restoreStore is not null && await bus.PropertyAsync(iface, "version", ct) >= (input ? 2u : 4u);
        // Keep the file until Start succeeds or the portal explicitly denies the grant.
        var saved = persistent ? restoreStore!.Peek() : null;
        using var persist = PortalValue.UInt(2);
        using var restore = saved is null ? null : PortalValue.String(saved);
        var persistence = new List<(string Key, PortalValue Value)>();
        if (persistent) persistence.Add(("persist_mode", persist));
        if (restore is not null) persistence.Add(("restore_token", restore));
        PortalValue result;
        try
        {
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
            result = await bus.RequestAsync(iface, "Start", session, true, [], ct);
        }
        catch (InvalidOperationException)
        {
            // The portal answered with a denial after the token was presented.
            if (persistent && saved is not null) restoreStore!.Discard();
            throw;
        }
        var savedNew = false;
        try
        {
            using var next = result.Get("restore_token");
            if (persistent)
            {
                var replacement = next?.Text();
                if (PortalRestoreStore.Accepts(replacement)) { restoreStore!.Save(replacement!); savedNew = true; }
                else restoreStore!.Discard();
            }
            Console.WriteLine("portal-persistence=" + (persistent && savedNew ? "saved" : "unavailable"));
            return result;
        }
        catch
        {
            // Start already consumed the grant. Do not leave the presented token behind.
            if (persistent && !savedNew) restoreStore!.Discard();
            result.Dispose();
            throw;
        }
    }
}
