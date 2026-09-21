using AsterDock.Contracts;
using Avalonia.Controls;
namespace URemote.Module;
public sealed class URemoteApplicationModule : IApplicationModule, IApplicationContextAware
{
    private IApplicationContext? context;
    private URemoteView? view;
    public void Initialize(IApplicationContext value) => context = value;
    public Control CreateView() => view ??= new URemoteView((context ?? throw new InvalidOperationException()).DataDirectory);
    public void Dispose() { view?.Dispose(); view = null; context = null; }
}
