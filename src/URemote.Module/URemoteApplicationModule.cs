using AsterDock.Contracts;
using Avalonia.Controls;
namespace URemote.Module;
public sealed class URemoteApplicationModule : IApplicationModule, IApplicationContextAware
{
    private IApplicationContext? context;
    private readonly List<URemoteView> views = [];
    private bool sessionStarted;
    public void Initialize(IApplicationContext value) => context = value;
    public Control CreateView()
    {
        var current = context ?? throw new InvalidOperationException();
        // The desktop host, signal handler, and device discovery belong to the first page.
        // A later page is a new control bound to the same data directory and context.
        // It does not auto-start a second host.
        var startSession = !sessionStarted;
        sessionStarted = true;
        var created = new URemoteView(current.DataDirectory, current, startSession);
        views.Add(created);
        return created;
    }
    public void Dispose()
    {
        foreach (var item in views) item.Dispose();
        views.Clear();
        context = null;
    }
}
