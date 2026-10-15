namespace URemote.Core;

// Presence belongs to the application; each permission generation owns only control resources.
public sealed class HostAdmission : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource changed = new();
    private bool enabled;
    public HostAdmission(bool enabled = true) => this.enabled = enabled;
    public (bool Enabled, CancellationToken Changed) Snapshot()
    { lock (gate) return (enabled, changed.Token); }
    public bool Enabled { get { lock (gate) return enabled; } }
    public void SetEnabled(bool value)
    {
        lock (gate)
        {
            if (enabled == value) return;
            enabled = value;
            var previous = changed;
            changed = new();
            previous.Cancel();
            previous.Dispose();
        }
    }
    public void Dispose() { lock (gate) { changed.Cancel(); changed.Dispose(); } }
}
