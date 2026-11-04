namespace AsterDock.Contracts;

public enum FeedbackKind
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// Where a toast or message dialog is shown.
/// Confirmation dialogs always stay in the window because they return a choice.
/// </summary>
public enum FeedbackDelivery
{
    /// <summary>
    /// In-window when the owning window is active and this module's page is visible; otherwise a system notification.
    /// </summary>
    Auto,

    /// <summary>Always the in-window toast or dialog.</summary>
    InApp,

    /// <summary>System notification. If that is unavailable, queue an in-window prompt until the page is shown.</summary>
    System,

    /// <summary>In-window prompt and a system notification when the platform can show one.</summary>
    Both
}

/// <summary>
/// In-window dialogs and toasts for the host and every light application.
/// Calls are thread-safe and marshalled onto the UI thread.
/// </summary>
public interface IUserFeedbackService
{
    Task ShowInfoAsync(string title, string message, CancellationToken cancellationToken = default, FeedbackDelivery delivery = FeedbackDelivery.Auto);

    Task ShowSuccessAsync(string title, string message, CancellationToken cancellationToken = default, FeedbackDelivery delivery = FeedbackDelivery.Auto);

    Task ShowWarningAsync(string title, string message, CancellationToken cancellationToken = default, FeedbackDelivery delivery = FeedbackDelivery.Auto);

    /// <summary>
    /// Modal error dialog. <paramref name="details"/> is shown in a collapsed section with a copy action.
    /// A system notification completes when it is posted; activating it opens this dialog in the window.
    /// </summary>
    Task ShowErrorAsync(string title, string message, Exception? details = null, CancellationToken cancellationToken = default, FeedbackDelivery delivery = FeedbackDelivery.Auto);

    /// <summary>
    /// Modal confirmation. Returns <see langword="true"/> when the primary button is used.
    /// Escape and the secondary button return <see langword="false"/>.
    /// </summary>
    Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText = "确定",
        string cancelText = "取消",
        bool danger = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Non-blocking toast. Errors stay until dismissed unless <paramref name="duration"/> is set.
    /// Identical kind, title, and message values collapse into one toast.
    /// </summary>
    void Toast(
        FeedbackKind kind,
        string message,
        string? title = null,
        string? actionText = null,
        Action? action = null,
        TimeSpan? duration = null,
        FeedbackDelivery delivery = FeedbackDelivery.Auto);

    /// <summary>
    /// Runs an async UI action and reports thrown exceptions through <see cref="ShowErrorAsync"/>.
    /// </summary>
    Task RunAsync(Func<Task> action, string? errorTitle = null);

    /// <summary>
    /// Same as <see cref="RunAsync"/> for event handlers that cannot return a task.
    /// </summary>
    void Run(Func<Task> action, string? errorTitle = null);
}

public static class UserFeedback
{
    public static void Report(this IUserFeedbackService? feedback, string title, Exception exception)
    {
        if (feedback is null || exception is OperationCanceledException) return;
        _ = feedback.ShowErrorAsync(title, exception.GetBaseException().Message, exception);
    }

    public static void Notify(this IUserFeedbackService? feedback, FeedbackKind kind, string message, string? title = null)
    {
        if (feedback is null || string.IsNullOrWhiteSpace(message)) return;
        feedback.Toast(kind, message, title);
    }
}
