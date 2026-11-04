using AsterDock.Contracts;

namespace AsterDock.UI.Services;

public sealed class LoadingService : ILoadingService
{
    private readonly object _gate = new();
    private readonly List<Scope> _scopes = [];

    public bool IsLoading { get; private set; }
    public string? Text { get; private set; }
    public bool IsCancellable { get; private set; }
    public event EventHandler? Changed;

    public IDisposable Begin(string? text = null) => BeginCore(text, cancellable: false, out _);

    public IDisposable Begin(string? text, out CancellationToken cancellationToken)
        => BeginCore(text, cancellable: true, out cancellationToken);

    public void Cancel()
    {
        Scope? target;
        lock (_gate)
        {
            target = null;
            for (var i = _scopes.Count - 1; i >= 0; i--)
            {
                if (!_scopes[i].Cancellable) continue;
                target = _scopes[i];
                break;
            }
        }

        target?.Cancel();
    }

    public async Task RunAsync(Func<Task> action, string? text = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var scope = Begin(text);
        await action().ConfigureAwait(false);
    }

    public async Task RunAsync(Func<CancellationToken, Task> action, string? text = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var scope = Begin(text, out var token);
        await action(token).ConfigureAwait(false);
    }

    private Scope BeginCore(string? text, bool cancellable, out CancellationToken cancellationToken)
    {
        var scope = new Scope(this, text, cancellable);
        cancellationToken = scope.Token;
        bool changed;
        lock (_gate)
        {
            _scopes.Add(scope);
            changed = Refresh();
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return scope;
    }

    private void End(Scope scope)
    {
        bool changed;
        lock (_gate)
        {
            if (!_scopes.Remove(scope)) return;
            changed = Refresh();
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool Refresh()
    {
        string? text = null;
        var cancellable = false;
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (text is null && _scopes[i].Text is not null) text = _scopes[i].Text;
            if (_scopes[i].Cancellable) cancellable = true;
        }

        var loading = _scopes.Count > 0;
        if (!loading) text = null;
        var changed = loading != IsLoading || text != Text || cancellable != IsCancellable;
        IsLoading = loading;
        Text = text;
        IsCancellable = cancellable;
        return changed;
    }

    private sealed class Scope : IDisposable
    {
        private readonly LoadingService _owner;
        private readonly CancellationTokenSource? _cancellation;
        private int _disposed;

        public Scope(LoadingService owner, string? text, bool cancellable)
        {
            _owner = owner;
            Text = text;
            Cancellable = cancellable;
            if (cancellable) _cancellation = new CancellationTokenSource();
        }

        public string? Text { get; }
        public bool Cancellable { get; }
        public CancellationToken Token => _cancellation?.Token ?? CancellationToken.None;

        public void Cancel() => _cancellation?.Cancel();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.End(this);
            _cancellation?.Dispose();
        }
    }
}
