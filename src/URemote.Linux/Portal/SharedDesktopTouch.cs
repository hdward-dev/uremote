using URemote.Core;

namespace URemote.Linux;

// The host owns authorization. A peer owns only contacts, never the Portal session.
internal sealed class SharedDesktopTouch(IDesktopTouch input, Func<bool> allowed) : IAsyncDisposable
{
    private readonly IDesktopTouch backend = input;
    private readonly Func<bool> authorized = allowed;
    private readonly object gate = new();
    private Lease? active;
    private bool disposed;
    internal IDesktopTouch? Acquire()
    {
        lock (gate)
        {
            if (disposed || active is not null || !authorized() || !backend.Available) return null;
            return active = new Lease(this);
        }
    }
    internal void Reset() { lock (gate) { if (!disposed) backend.Reset(); } }
    private sealed class Lease(SharedDesktopTouch owner) : IDesktopTouch
    {
        public bool Available { get { lock (owner.gate) return owner.active == this && !owner.disposed && owner.authorized() && owner.backend.Available; } }
        public bool Apply(uint output, HostTouchEvent input)
        { lock (owner.gate) return Available && owner.backend.Apply(output, input); }
        public void Reset() { lock (owner.gate) { if (owner.active == this && !owner.disposed) owner.backend.Reset(); } }
        public ValueTask DisposeAsync()
        {
            lock (owner.gate)
            {
                if (owner.active == this)
                {
                    owner.backend.Reset();
                    owner.active = null;
                }
            }
            return ValueTask.CompletedTask;
        }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; active = null; backend.Reset();
        }
        await backend.DisposeAsync();
    }
}
