using Avalonia.Controls;

namespace AsterDock.Contracts;

public interface IApplicationModule : IDisposable
{
    Control CreateView();
}

public interface IApplicationNavigationProvider
{
    bool CanGoBack { get; }
    event EventHandler? NavigationStateChanged;
    void GoBack();
}
