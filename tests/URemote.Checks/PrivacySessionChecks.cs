using System.Collections.Concurrent;
using URemote.Host;
using URemote.Linux;
using URemote.Core;
using System.Threading.Channels;

internal static class PrivacySessionChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        byte[] Set(int mode) => Convert.FromHexString("0807AA01080A02082A3A02100" + mode);
        var sent = new ConcurrentQueue<byte[]>(); var text = new ConcurrentQueue<byte[]>(); var open = false;
        var screens = new List<Screen>(); Action? lose = null;
        Task<IPrivacyScreen> Factory(Action lost, CancellationToken ct)
        { lose = lost; var screen = new Screen(); screens.Add(screen); return Task.FromResult<IPrivacyScreen>(screen); }
        await using (var session = new HostPrivacySession(b => { sent.Enqueue(b); return true; },
            b => { if (!open) return false; text.Enqueue(b); return true; }, Factory, true, _ => { }, default))
        {
            await session.HandleAsync(Set(2), false, default);
            check(screens is [{ IsActive: true }], "normal privacy starts a local cover");
            await session.HandleAsync(Set(2), false, default);
            check(screens.Count == 1, "repeated enable does not stack overlays");
            await session.HandleAsync(Set(3), false, default);
            check(screens[0].IsActive, "unsupported physical-off mode preserves an existing normal cover");
            check(sent.Any(b => Convert.ToHexString(b).EndsWith("080110031802")), "unsupported mode replies with a failure and the actual active mode");
            check(text.IsEmpty, "TEXT responses wait for the native client channel to open");
            open = true; await Task.Delay(180);
            check(!text.IsEmpty && text.Any(b => Convert.ToHexString(b).Contains("B201")), "queued RPC responses reach TEXT after channel opens");
            screens[0].Active = false; lose!();
            check(sent.Any(b => Convert.ToHexString(b).EndsWith("080110021801")), "backend loss reports privacy off instead of a stale enabled state");
            await session.HandleAsync(Set(2), false, default);
            check(screens.Count == 2 && screens[0].Disposed && screens[1].IsActive, "enable after backend loss disposes old backend and creates a fresh cover");
            await session.HandleAsync(Set(1), false, default);
            check(screens[1].Disposed, "explicit disable removes the local cover");
            await session.HandleAsync(Set(2), false, default);
        }
        check(screens.All(s => s.Disposed), "session end always removes every privacy cover");
        var closedNetworkCover = new Screen();
        await using (var closed = new HostPrivacySession(_ => throw new IOException(), _ => throw new InvalidOperationException(),
            (_, _) => Task.FromResult<IPrivacyScreen>(closedNetworkCover), true, _ => { }, default))
        {
            closed.SendInitial(false);
            await closed.HandleAsync(Set(2), false, default);
        }
        check(closedNetworkCover.Disposed, "a concurrently closed data channel cannot prevent local screen restoration");
        await using (var unsupported = new HostPrivacySession(_ => true, _ => true, Factory, false, _ => { }, default))
        {
            var count = screens.Count;
            await unsupported.HandleAsync(Set(2), false, default);
            check(screens.Count == count, "unsupported or view-only sessions cannot enable privacy");
        }
        await using (var failed = new HostPrivacySession(b => { sent.Enqueue(b); return true; }, _ => true,
            (_, _) => throw new IOException(), true, _ => { }, default))
        {
            while (sent.TryDequeue(out _)) { }
            await failed.HandleAsync(Set(2), false, default);
            check(sent.Any(b => Convert.ToHexString(b).EndsWith("080110021801")), "backend initialization failure is reported truthfully without killing the remote session");
        }
        await ImageUpdatesAsync(check);
    }
    private static async Task ImageUpdatesAsync(Action<bool, string> check)
    {
        var loads = Channel.CreateUnbounded<TaskCompletionSource<PrivacyScreenImage?>>();
        var reports = new ConcurrentQueue<string>(); var replies = new ConcurrentQueue<byte[]>(); var cover = new Screen();
        async Task<PrivacyScreenImage?> Load(CancellationToken ct)
        {
            var next = new TaskCompletionSource<PrivacyScreenImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            loads.Writer.TryWrite(next); return await next.Task.WaitAsync(ct);
        }
        async Task Until(Func<bool> done)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!done()) await Task.Delay(10, deadline.Token);
        }
        var refresh = Convert.FromHexString("0807AA01070A02082ACA0100");
        await using (var session = new HostPrivacySession(b => { replies.Enqueue(b); return true; }, _ => true,
            (_, _) => Task.FromResult<IPrivacyScreen>(cover), true, reports.Enqueue, default, Load))
        {
            var initialLoad = await loads.Reader.ReadAsync();
            var initialPixels = new byte[] { 1, 2, 3, 255 };
            initialLoad.SetResult(new(1, 1, initialPixels));
            await Until(() => reports.Any(r => r.StartsWith("privacy-image-ready")));
            await session.HandleAsync(Convert.FromHexString("0807AA01080A02082A3A021002"), false, default);
            check(cover.ImageUpdates == 1, "enabling privacy applies the prefetched UU image");
            while (replies.TryDequeue(out _)) { }
            await session.HandleAsync(refresh, false, default);
            var update = await loads.Reader.ReadAsync();
            check(replies.IsEmpty, "screensaver update is not falsely acknowledged while the image is still loading");
            update.SetResult(new(1, 1, [4, 5, 6, 255]));
            await Until(() => replies.Any(b => Convert.ToHexString(b) == "0807B201070A02082AAA0100"));
            check(cover.ImageUpdates == 2 && initialPixels.All(b => b == 0), "live image update succeeds and clears the previous cached image");
            while (replies.TryDequeue(out _)) { }
            cover.RejectImage = true;
            await session.HandleAsync(refresh, false, default);
            var failed = await loads.Reader.ReadAsync(); var badPixels = new byte[] { 7, 8, 9, 255 };
            failed.SetResult(new(1, 1, badPixels));
            await Until(() => replies.Any(b => Convert.ToHexString(b) == "0807B201090A02082AAA01020801"));
            check(cover.IsActive && badPixels.All(b => b == 0), "failed image application preserves the active cover, clears the rejected image, and replies with an error");
            cover.RejectImage = false;
            await session.HandleAsync(refresh, false, default);
            var reset = await loads.Reader.ReadAsync(); reset.SetResult(null);
            await Until(() => cover.ImageUpdates == 3);
            check(cover.LastImage is null, "clearing the cloud selection resets an active cover to black");
        }
        check(cover.Disposed, "image refresh worker terminates and closes its cover when the session ends");
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loading = new HostPrivacySession(_ => true, _ => true, (_, _) => Task.FromResult<IPrivacyScreen>(new Screen()), true, _ => { }, default,
            async ct => { blocked.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return null; });
        await blocked.Task;
        await loading.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        check(true, "disconnect cancels pending cloud image loading without delaying restoration");
    }
    private sealed class Screen : IPrivacyScreen
    {
        internal bool Active = true;
        internal bool Disposed;
        public bool IsActive => Active;
        internal bool RejectImage;
        internal int ImageUpdates;
        internal PrivacyScreenImage? LastImage;
        public Task SetImageAsync(PrivacyScreenImage? image, CancellationToken ct)
        { if (RejectImage) throw new IOException(); ImageUpdates++; LastImage = image; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; Active = false; return ValueTask.CompletedTask; }
    }
}
