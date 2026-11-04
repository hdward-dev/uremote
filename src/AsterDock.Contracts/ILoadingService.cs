namespace AsterDock.Contracts;

/// <summary>
/// Host-wide loading scopes. The main window shows one overlay; nested scopes stay until each is disposed.
/// </summary>
public interface ILoadingService
{
    bool IsLoading { get; }

    /// <summary>Latest non-null scope text. A null text keeps the previous message.</summary>
    string? Text { get; }

    bool IsCancellable { get; }

    event EventHandler? Changed;

    IDisposable Begin(string? text = null);

    /// <summary>Starts a scope whose <paramref name="cancellationToken"/> is cancelled by <see cref="Cancel"/>.</summary>
    IDisposable Begin(string? text, out CancellationToken cancellationToken);

    void Cancel();

    Task RunAsync(Func<Task> action, string? text = null);

    Task RunAsync(Func<CancellationToken, Task> action, string? text = null);
}
