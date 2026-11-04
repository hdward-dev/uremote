using AsterDock.Contracts;

namespace AsterDock.UI.Services;

/// <summary>
/// Stamps one light application's id onto the shared feedback service.
/// Plugins keep calling <see cref="IUserFeedbackService"/> without a module argument.
/// </summary>
public sealed class ModuleFeedbackService : IUserFeedbackService
{
    private readonly UserFeedbackService _inner;
    private readonly string _moduleId;

    public ModuleFeedbackService(UserFeedbackService inner, string moduleId)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        _inner = inner;
        _moduleId = moduleId;
    }

    public string ModuleId => _moduleId;

    public Task ShowInfoAsync(string title, string message, CancellationToken cancellationToken = default, FeedbackDelivery delivery = FeedbackDelivery.Auto)
        => _inner.ShowInfoFor(_moduleId, title, message, cancellationToken, delivery);

    public Task ShowSuccessAsync(string title, string message, CancellationToken cancellationToken = default, FeedbackDelivery delivery = FeedbackDelivery.Auto)
        => _inner.ShowSuccessFor(_moduleId, title, message, cancellationToken, delivery);

    public Task ShowWarningAsync(string title, string message, CancellationToken cancellationToken = default, FeedbackDelivery delivery = FeedbackDelivery.Auto)
        => _inner.ShowWarningFor(_moduleId, title, message, cancellationToken, delivery);

    public Task ShowErrorAsync(string title, string message, Exception? details = null, CancellationToken cancellationToken = default, FeedbackDelivery delivery = FeedbackDelivery.Auto)
        => _inner.ShowErrorFor(_moduleId, title, message, details, cancellationToken, delivery);

    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", string cancelText = "取消", bool danger = false, CancellationToken cancellationToken = default)
        => _inner.ConfirmAsync(title, message, confirmText, cancelText, danger, cancellationToken);

    public void Toast(FeedbackKind kind, string message, string? title = null, string? actionText = null, Action? action = null, TimeSpan? duration = null, FeedbackDelivery delivery = FeedbackDelivery.Auto)
        => _inner.ToastFor(_moduleId, kind, message, title, actionText, action, duration, delivery);

    public async Task RunAsync(Func<Task> action, string? errorTitle = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(string.IsNullOrWhiteSpace(errorTitle) ? "操作未完成" : errorTitle, exception.GetBaseException().Message, exception)
                .ConfigureAwait(false);
        }
    }

    public void Run(Func<Task> action, string? errorTitle = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = RunAsync(action, errorTitle);
    }
}
