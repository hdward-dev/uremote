using URemote.Linux;

static class PreviewRecycleChecks
{
    public static void Run(Action<bool, string> check)
    {
        Decision(check);
        Topology(check);
        Aliases(check);
    }

    private static void Decision(Action<bool, string> check)
    {
        using var preview = new CancellationTokenSource();
        var cleared = false;
        var refreshed = new List<string>();
        void Recycle(string reason)
        {
            // Same branch DesktopHostSession uses while a viewer peer is connected.
            var decision = PortalRecycleDecision.For(reason);
            if (decision.RefreshCapture)
            {
                refreshed.Add(reason);
                return;
            }
            if (decision.CancelPreview) preview.Cancel();
            if (PortalRecycleDecision.ShouldClearControlRoom(decision, hostStopping: false)) cleared = true;
        }

        Recycle("unblank");
        check(!preview.IsCancellationRequested && !cleared && refreshed.Single() == "unblank",
            "unblank refreshes capture without cancelling preview or clearing the control room");
        Recycle("unlock");
        check(!preview.IsCancellationRequested && !cleared && refreshed.Count == 2,
            "unlock keeps a connected preview and its control room");
        var stopping = PortalRecycleDecision.For("unblank");
        check(!PortalRecycleDecision.ShouldClearControlRoom(stopping, hostStopping: true),
            "host stop does not clear the room because of an unblank decision");
        Recycle("low-bitrate");
        check(preview.IsCancellationRequested && cleared,
            "a stuck capture still restarts preview and clears the room");
        check(PortalRecycleDecision.For("low-luma") == new PortalRecycleDecision(true, true, false),
            "low-luma stays a hard preview recycle");
        var failed = PortalRecycleDecision.For("refresh-failed");
        check(failed.CancelPreview && failed.ClearControlRoom && !failed.RefreshCapture,
            "a failed in-place refresh still ends the preview and clears the room");
        check(!DesktopBackend.ShouldQueueRecycle("low-bitrate", portalOpen: true, alreadyPending: false, TimeSpan.FromSeconds(1), out _),
            "low-bitrate stays cooled down after an unlock refresh");
        check(DesktopBackend.ShouldQueueRecycle("refresh-failed", portalOpen: true, alreadyPending: false, TimeSpan.FromSeconds(1), out var queued)
            && queued == "refresh-failed",
            "a failed in-place refresh bypasses the unlock cooldown");
    }

    private static void Topology(Action<bool, string> check)
    {
        var locked = new[]
        {
            new PortalStreamBox(4, 0, 0, 1920, 1080),
            new PortalStreamBox(8, 1920, 0, 2560, 1440)
        };
        var refreshed = new[]
        {
            new PortalStreamBox(15, 1920, 0, 2560, 1440),
            new PortalStreamBox(11, 0, 0, 1920, 1080)
        };
        check(PortalTopology.Signature(locked) == PortalTopology.Signature(refreshed),
            "replacing PipeWire node ids does not look like a display topology change");
        check(PortalTopology.Signature(locked) != PortalTopology.Signature([new PortalStreamBox(4, 0, 0, 1280, 720)]),
            "a real mode change still changes topology");
        var map = PortalStreamMap.Match(locked, refreshed);
        check(map[4] == 11 && map[8] == 15, "refresh maps each monitor by its position and size");
    }

    private static void Aliases(Action<bool, string> check)
    {
        DesktopBackend.ResetOutputAliasesForTests();
        check(DesktopBackend.ResolveOutput(4) == 4, "stream ids pass through before a capture refresh");
        DesktopBackend.NoteOutputAliases(new Dictionary<uint, uint> { [4] = 9, [8] = 10 });
        check(DesktopBackend.ResolveOutput(4) == 9 && DesktopBackend.ResolveOutput(8) == 10,
            "capture keeps the original stream id across a portal refresh");
        DesktopBackend.NoteOutputAliases(new Dictionary<uint, uint> { [9] = 12, [10] = 13 });
        check(DesktopBackend.ResolveOutput(4) == 12 && DesktopBackend.ResolveOutput(8) == 13,
            "a second refresh still follows the stream id the preview started with");
        DesktopBackend.ResetOutputAliasesForTests();
        check(DesktopBackend.ResolveOutput(4) == 4, "alias reset leaves later captures on the live id");
    }
}
