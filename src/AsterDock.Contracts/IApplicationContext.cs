using Avalonia.Controls;

namespace AsterDock.Contracts;

public interface IApplicationContext
{
    string ApplicationId { get; }
    string DataDirectory { get; }
    IWindowService Windows { get; }
    IApplicationShell Shell { get; }
    ISystemMetricsService SystemMetrics { get; }
    ILoadingService Loading { get; }
    IUserFeedbackService Feedback { get; }
}

public interface IApplicationContextAware
{
    void Initialize(IApplicationContext context);
}

public interface IWindowService
{
    void Show(Window window, bool owned = true);
    Window ShowOrActivate(string key, Func<Window> windowFactory, bool owned = true);
    Task<TResult> ShowDialogAsync<TResult>(Window window);
    void CloseAll();
}
