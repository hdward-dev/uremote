using URemote.Core;
using URemote.Linux;

internal static class SharedTouchChecks
{
    internal static async Task RunAsync(Action<bool,string> check)
    {
        var fake = new Input(); var authorized = true;
        var owner = new SharedDesktopTouch(fake, () => authorized);
        var first = owner.Acquire()!;
        check(first.Available && owner.Acquire() is null, "one controller owns touch contacts at a time");
        check(first.Apply(1,new(1,[new(1,.5,.5)])), "authorized controller can send touch");
        await first.DisposeAsync();
        check(fake.Resets == 1 && fake.Disposals == 0, "disconnect releases contacts but retains authorization");
        var second = owner.Acquire()!;
        check(second.Available && !first.Available, "reconnect reuses host authorization and invalidates previous lease");
        await first.DisposeAsync();
        check(fake.Resets == 1, "late cleanup from old controller cannot clear new contacts");
        authorized = false;
        check(!second.Available && !second.Apply(1,new(1,[new(1,.5,.5)])), "revoked screen permission blocks retained touch session");
        await owner.DisposeAsync(); await second.DisposeAsync(); await owner.DisposeAsync();
        check(fake.Disposals == 1 && owner.Acquire() is null, "stopping host closes touch authorization exactly once");
    }
    private sealed class Input : IDesktopTouch
    {
        internal int Resets, Disposals;
        public bool Available => Disposals == 0;
        public bool Apply(uint output, HostTouchEvent input) => true;
        public void Reset() => Resets++;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
