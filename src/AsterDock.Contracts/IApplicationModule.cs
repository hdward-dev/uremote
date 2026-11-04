using Avalonia.Controls;

namespace AsterDock.Contracts;

public interface IApplicationModule : IDisposable
{
    /// <summary>
    /// Creates a new page. Every call returns a new control.
    /// The host keeps one instance in the main window and asks for another for an independent window.
    /// </summary>
    Control CreateView();
}

public interface IApplicationNavigationProvider
{
    bool CanGoBack { get; }
    event EventHandler? NavigationStateChanged;
    void GoBack();
}
