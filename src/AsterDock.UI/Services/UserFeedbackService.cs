using AsterDock.Contracts;
using Avalonia.Threading;

namespace AsterDock.UI.Services;

public enum DialogTone
{
    Info,
    Success,
    Warning,
    Error,
    Confirm
}

public sealed class FeedbackDialog
{
    internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Guid Id { get; } = Guid.NewGuid();
    public DialogTone Tone { get; init; }
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Details { get; init; }
    public string PrimaryText { get; init; } = "确定";
    public string? SecondaryText { get; init; }
    public bool Danger { get; init; }
    public string? HostId { get; internal set; }
    internal CancellationToken Cancellation { get; init; }
}

public sealed class FeedbackToast
{
    public Guid Id { get; } = Guid.NewGuid();
    public FeedbackKind Kind { get; init; }
    public string? Title { get; init; }
    public string Message { get; init; } = "";
    public string? ActionText { get; set; }
    public Action? Action { get; set; }
    public int RepeatCount { get; internal set; } = 1;
    public DateTimeOffset ShownAt { get; internal set; }
    public DateTimeOffset? DismissAt { get; internal set; }
    internal TimeSpan? Duration { get; init; }
}

public sealed class UserFeedbackService : IUserFeedbackService
{
    public const int DefaultMaxVisibleToasts = 4;

    private readonly object _gate = new();
    private readonly Action<Action> _post;
    private readonly List<FeedbackDialog> _pending = [];
    private readonly List<FeedbackToast> _visible = [];
    private readonly List<FeedbackToast> _toastQueue = [];
    private readonly List<DeferredFeedback> _deferred = [];
    private readonly Dictionary<string, DateTimeOffset> _systemRecent = [];
    private readonly Dictionary<string, SystemNotificationRequest> _posted = new(StringComparer.Ordinal);
    private ISystemNotificationBackend? _system;
    private FeedbackDialog? _active;
    private string? _activeHostId;
    private int _maxVisible = DefaultMaxVisibleToasts;

    public UserFeedbackService(Action<Action>? postToUi = null)
    {
        _post = postToUi ?? DefaultPost;
        Clock = static () => DateTimeOffset.UtcNow;
    }

    public Func<DateTimeOffset> Clock { get; set; }

    public IFeedbackPresence? Presence { get; set; }

    public TimeSpan SystemNotificationThrottle { get; set; } = TimeSpan.FromSeconds(8);

    public ISystemNotificationBackend? SystemNotifications
    {
        get => _system;
        set
        {
            if (ReferenceEquals(_system, value)) return;
            if (_system is not null) _system.Activated -= OnSystemActivated;
            _system = value;
            if (_system is not null) _system.Activated += OnSystemActivated;
        }
    }

    public int DeferredCount
    {
        get { lock (_gate) return _deferred.Count; }
    }

    public event EventHandler<SystemNotificationRequest>? SystemNotificationActivated;

    public FeedbackDialog? ActiveDialog
    {
        get { lock (_gate) return _active; }
    }

    public int QueuedDialogCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    public IReadOnlyList<FeedbackToast> VisibleToasts
    {
        get { lock (_gate) return _visible.ToArray(); }
    }

    public int QueuedToastCount
    {
        get { lock (_gate) return _toastQueue.Count; }
    }

    public int MaxVisibleToasts
    {
        get { lock (_gate) return _maxVisible; }
        set
        {
            var changed = false;
            lock (_gate)
            {
                var next = Math.Max(1, value);
                if (next == _maxVisible) return;
                _maxVisible = next;
                changed = FillToastsLocked();
            }

            if (changed) Publish();
        }
    }

    public string? ActiveHostId
    {
        get { lock (_gate) return _activeHostId; }
    }

    public event EventHandler? Changed;

    public void ActivateHost(string hostId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostId);
        var changed = false;
        lock (_gate)
        {
            if (_activeHostId != hostId)
            {
                _activeHostId = hostId;
                changed = true;
            }

            if (_active is { HostId: null })
            {
                _active.HostId = hostId;
                changed = true;
            }
        }

        if (changed) Publish();
    }

    public void ReleaseHost(string hostId)
    {
        var changed = false;
        lock (_gate)
        {
            if (_activeHostId == hostId)
            {
                _activeHostId = null;
                changed = true;
            }
        }

        if (changed) Publish();
    }

    public Task ShowInfoAsync(string title, string message, CancellationToken cancellationToken = default, FeedbackDelivery delivery = FeedbackDelivery.Auto)
        => ShowInfoFor(null, title, message, cancellationToken, delivery);

    public Task ShowSuccessAsync(string title, string message, CancellationToken cancellationToken = default, FeedbackDelivery delivery = FeedbackDelivery.Auto)
        => ShowSuccessFor(null, title, message, cancellationToken, delivery);

    public Task ShowWarningAsync(string title, string message, CancellationToken cancellationToken = default, FeedbackDelivery delivery = FeedbackDelivery.Auto)
        => ShowWarningFor(null, title, message, cancellationToken, delivery);

    public Task ShowErrorAsync(string title, string message, Exception? details = null, CancellationToken cancellationToken = default, FeedbackDelivery delivery = FeedbackDelivery.Auto)
        => ShowErrorFor(null, title, message, details, cancellationToken, delivery);

    internal Task ShowInfoFor(string? moduleId, string title, string message, CancellationToken cancellationToken, FeedbackDelivery delivery)
        => ShowReported(moduleId, DialogTone.Info, FeedbackKind.Info, title, message, null, false, cancellationToken, delivery);

    internal Task ShowSuccessFor(string? moduleId, string title, string message, CancellationToken cancellationToken, FeedbackDelivery delivery)
        => ShowReported(moduleId, DialogTone.Success, FeedbackKind.Success, title, message, null, false, cancellationToken, delivery);

    internal Task ShowWarningFor(string? moduleId, string title, string message, CancellationToken cancellationToken, FeedbackDelivery delivery)
        => ShowReported(moduleId, DialogTone.Warning, FeedbackKind.Warning, title, message, null, false, cancellationToken, delivery);

    internal Task ShowErrorFor(string? moduleId, string title, string message, Exception? details, CancellationToken cancellationToken, FeedbackDelivery delivery)
        => ShowReported(moduleId, DialogTone.Error, FeedbackKind.Error, title, message, details?.ToString(), true, cancellationToken, delivery);

    public Task<bool> ConfirmAsync(
        string title,
        string message,
        string confirmText = "确定",
        string cancelText = "取消",
        bool danger = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmText);
        ArgumentException.ThrowIfNullOrWhiteSpace(cancelText);
        return ShowToneAsync(DialogTone.Confirm, title, message, null, confirmText, cancelText, danger, cancellationToken);
    }

    public void Toast(
        FeedbackKind kind,
        string message,
        string? title = null,
        string? actionText = null,
        Action? action = null,
        TimeSpan? duration = null,
        FeedbackDelivery delivery = FeedbackDelivery.Auto)
        => ToastFor(null, kind, message, title, actionText, action, duration, delivery);

    internal void ToastFor(
        string? moduleId,
        FeedbackKind kind,
        string message,
        string? title,
        string? actionText,
        Action? action,
        TimeSpan? duration,
        FeedbackDelivery delivery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        actionText = string.IsNullOrWhiteSpace(actionText) ? null : actionText.Trim();
        var toast = new FeedbackToast
        {
            Kind = kind,
            Title = title,
            Message = message.Trim(),
            ActionText = actionText,
            Action = action,
            Duration = NormalizeDuration(kind, duration)
        };
        var route = Resolve(moduleId, delivery);
        if (route == FeedbackRoute.SystemOnly && !PublishSystem(moduleId, kind, title ?? "", toast.Message, null, false))
            route = PageVisible(moduleId) ? FeedbackRoute.InAppImmediate : FeedbackRoute.InAppDeferred;
        else if (route is FeedbackRoute.BothImmediate or FeedbackRoute.BothDeferred)
            PublishSystem(moduleId, kind, title ?? "", toast.Message, null, false);

        if (route is FeedbackRoute.SystemOnly) return;
        if (route is FeedbackRoute.InAppDeferred or FeedbackRoute.BothDeferred)
        {
            DeferToast(moduleId, toast);
            return;
        }

        lock (_gate) EnqueueToastLocked(toast);
        Publish();
    }

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
        _ = Observe(action, errorTitle);
    }

    public void AcceptActiveDialog() => CompleteActive(accepted: true);

    public void CancelActiveDialog() => CompleteActive(accepted: false);

    public void DismissToast(Guid id)
    {
        var changed = false;
        lock (_gate)
        {
            if (_visible.RemoveAll(item => item.Id == id) > 0) changed = true;
            if (_toastQueue.RemoveAll(item => item.Id == id) > 0) changed = true;
            if (FillToastsLocked()) changed = true;
        }

        if (changed) Publish();
    }

    public void InvokeToastAction(Guid id)
    {
        Action? action;
        lock (_gate)
        {
            action = _visible.Concat(_toastQueue).FirstOrDefault(item => item.Id == id)?.Action;
        }

        if (action is not null)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                _ = ShowErrorAsync("操作未完成", exception.GetBaseException().Message, exception);
            }
        }

        DismissToast(id);
    }

    public void Advance(DateTimeOffset now)
    {
        var changed = false;
        lock (_gate)
        {
            var expired = _visible.Where(item => item.DismissAt is { } due && due <= now).Select(item => item.Id).ToArray();
            if (expired.Length > 0)
            {
                _visible.RemoveAll(item => expired.Contains(item.Id));
                changed = true;
            }

            if (FillToastsLocked()) changed = true;
        }

        if (changed) Publish();
    }

    public static TimeSpan DefaultDuration(FeedbackKind kind) => kind switch
    {
        FeedbackKind.Success => TimeSpan.FromMilliseconds(3500),
        FeedbackKind.Warning => TimeSpan.FromSeconds(6),
        FeedbackKind.Error => Timeout.InfiniteTimeSpan,
        _ => TimeSpan.FromSeconds(4)
    };

    public static string KindLabel(FeedbackKind kind) => kind switch
    {
        FeedbackKind.Success => "成功",
        FeedbackKind.Warning => "警告",
        FeedbackKind.Error => "错误",
        _ => "信息"
    };

    public static string ToneLabel(DialogTone tone) => tone switch
    {
        DialogTone.Success => "成功",
        DialogTone.Warning => "警告",
        DialogTone.Error => "错误",
        DialogTone.Confirm => "确认",
        _ => "信息"
    };

    private Task<bool> ShowToneAsync(
        DialogTone tone,
        string title,
        string message,
        string? details,
        string primary,
        string? secondary,
        bool danger,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(message);
        var dialog = new FeedbackDialog
        {
            Tone = tone,
            Title = title.Trim(),
            Message = message,
            Details = string.IsNullOrWhiteSpace(details) ? null : details,
            PrimaryText = primary,
            SecondaryText = secondary,
            Danger = danger,
            Cancellation = cancellationToken
        };
        if (cancellationToken.IsCancellationRequested)
        {
            dialog.Completion.TrySetCanceled(cancellationToken);
            return dialog.Completion.Task;
        }

        lock (_gate)
        {
            if (_active is null)
            {
                _active = dialog;
                dialog.HostId = _activeHostId;
            }
            else
            {
                _pending.Add(dialog);
            }
        }

        if (cancellationToken.CanBeCanceled)
            cancellationToken.Register(() => CancelRequest(dialog.Id));
        Publish();
        return dialog.Completion.Task;
    }

    private void CompleteActive(bool accepted)
    {
        FeedbackDialog? current;
        lock (_gate)
        {
            current = _active;
            if (current is null) return;
            _active = null;
            PromoteDialogLocked();
        }

        Finish(current, accepted);
        Publish();
    }

    private void CancelRequest(Guid id)
    {
        FeedbackDialog? removed = null;
        lock (_gate)
        {
            if (_active?.Id == id)
            {
                removed = _active;
                _active = null;
                PromoteDialogLocked();
            }
            else
            {
                var index = _pending.FindIndex(item => item.Id == id);
                if (index >= 0)
                {
                    removed = _pending[index];
                    _pending.RemoveAt(index);
                }
                else
                {
                    var deferred = _deferred.FindIndex(item => item.Dialog?.Id == id);
                    if (deferred >= 0)
                    {
                        removed = _deferred[deferred].Dialog;
                        _deferred.RemoveAt(deferred);
                    }
                }
            }
        }

        if (removed is null) return;
        removed.Completion.TrySetCanceled(removed.Cancellation);
        Publish();
    }

    private static void Finish(FeedbackDialog dialog, bool accepted)
    {
        if (dialog.Cancellation.IsCancellationRequested) dialog.Completion.TrySetCanceled(dialog.Cancellation);
        else dialog.Completion.TrySetResult(accepted);
    }

    private void PromoteDialogLocked()
    {
        if (_active is not null || _pending.Count == 0) return;
        _active = _pending[0];
        _pending.RemoveAt(0);
        _active.HostId ??= _activeHostId;
    }

    private FeedbackToast? FindToastLocked(FeedbackKind kind, string? title, string message)
    {
        foreach (var toast in _visible)
        {
            if (Same(toast, kind, title, message)) return toast;
        }

        foreach (var toast in _toastQueue)
        {
            if (Same(toast, kind, title, message)) return toast;
        }

        return null;
    }

    private static bool Same(FeedbackToast toast, FeedbackKind kind, string? title, string message)
        => toast.Kind == kind
           && string.Equals(toast.Title, title, StringComparison.Ordinal)
           && string.Equals(toast.Message, message.Trim(), StringComparison.Ordinal);

    private void ShowToastLocked(FeedbackToast toast)
    {
        RestartTimerLocked(toast);
        _visible.Add(toast);
    }

    private void RestartTimerLocked(FeedbackToast toast)
    {
        toast.ShownAt = Clock();
        toast.DismissAt = toast.Duration is { } duration ? toast.ShownAt + duration : null;
    }

    private bool FillToastsLocked()
    {
        var changed = false;
        while (_visible.Count < _maxVisible && _toastQueue.Count > 0)
        {
            var next = _toastQueue[0];
            _toastQueue.RemoveAt(0);
            ShowToastLocked(next);
            changed = true;
        }

        return changed;
    }

    private static TimeSpan? NormalizeDuration(FeedbackKind kind, TimeSpan? duration)
    {
        var value = duration ?? DefaultDuration(kind);
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan) return null;
        return value;
    }

    private async Task Observe(Func<Task> action, string? errorTitle)
    {
        try
        {
            await RunAsync(action, errorTitle).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The dialog task can be canceled with the caller's token. Never surface that on the process.
        }
    }

    public void RefreshDelivery()
    {
        var promoted = false;
        lock (_gate)
        {
            for (var i = _deferred.Count - 1; i >= 0; i--)
            {
                var item = _deferred[i];
                if (Presence is not null && !Presence.IsPageVisible(item.ModuleId)) continue;
                _deferred.RemoveAt(i);
                if (item.Toast is not null) EnqueueToastLocked(item.Toast);
                if (item.Dialog is not null) EnqueueDialogLocked(item.Dialog);
                promoted = true;
            }
        }

        if (promoted) Publish();
    }

    private Task<bool> ShowReported(
        string? moduleId,
        DialogTone tone,
        FeedbackKind kind,
        string title,
        string message,
        string? details,
        bool openErrorDialog,
        CancellationToken cancellationToken,
        FeedbackDelivery delivery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(message);
        var route = Resolve(moduleId, delivery);
        var summary = title.Trim();
        if (route == FeedbackRoute.SystemOnly)
        {
            if (PublishSystem(moduleId, kind, summary, message, details, openErrorDialog))
                return Task.FromResult(true);
            route = PageVisible(moduleId) ? FeedbackRoute.InAppImmediate : FeedbackRoute.InAppDeferred;
        }
        else if (route is FeedbackRoute.BothImmediate or FeedbackRoute.BothDeferred)
        {
            PublishSystem(moduleId, kind, summary, message, details, openErrorDialog);
        }

        var dialog = new FeedbackDialog
        {
            Tone = tone,
            Title = summary,
            Message = message,
            Details = string.IsNullOrWhiteSpace(details) ? null : details,
            PrimaryText = "确定",
            Cancellation = cancellationToken
        };
        if (cancellationToken.IsCancellationRequested)
        {
            dialog.Completion.TrySetCanceled(cancellationToken);
            return dialog.Completion.Task;
        }

        if (route is FeedbackRoute.InAppDeferred or FeedbackRoute.BothDeferred)
        {
            lock (_gate) _deferred.Add(new DeferredFeedback(moduleId, null, dialog));
            if (cancellationToken.CanBeCanceled)
                cancellationToken.Register(() => CancelRequest(dialog.Id));
            Publish();
            return dialog.Completion.Task;
        }

        lock (_gate) EnqueueDialogLocked(dialog);
        if (cancellationToken.CanBeCanceled)
            cancellationToken.Register(() => CancelRequest(dialog.Id));
        Publish();
        return dialog.Completion.Task;
    }

    private void EnqueueDialogLocked(FeedbackDialog dialog)
    {
        if (_active is null)
        {
            _active = dialog;
            dialog.HostId = _activeHostId;
        }
        else
        {
            _pending.Add(dialog);
        }
    }

    private void EnqueueToastLocked(FeedbackToast toast)
    {
        var existing = FindToastLocked(toast.Kind, toast.Title, toast.Message);
        if (existing is not null)
        {
            existing.RepeatCount++;
            if (toast.ActionText is not null)
            {
                existing.ActionText = toast.ActionText;
                existing.Action = toast.Action;
            }

            if (_visible.Contains(existing)) RestartTimerLocked(existing);
            return;
        }

        if (_visible.Count < _maxVisible && _toastQueue.Count == 0) ShowToastLocked(toast);
        else _toastQueue.Add(toast);
    }

    private void DeferToast(string? moduleId, FeedbackToast toast)
    {
        lock (_gate)
        {
            foreach (var item in _deferred)
            {
                if (item.Toast is not null && Same(item.Toast, toast.Kind, toast.Title, toast.Message))
                {
                    item.Toast.RepeatCount++;
                    return;
                }
            }

            _deferred.Add(new DeferredFeedback(moduleId, toast, null));
        }

        Publish();
    }

    private FeedbackRoute Resolve(string? moduleId, FeedbackDelivery delivery)
    {
        var allowed = Presence is not { AllowSystemNotifications: false } && SystemNotifications?.IsAvailable == true;
        if (Presence is null) allowed = SystemNotifications?.IsAvailable == true;
        return FeedbackRouter.Decide(
            delivery,
            Presence is not null,
            Presence?.IsWindowForeground(moduleId) == true,
            Presence?.IsPageVisible(moduleId) == true,
            allowed);
    }

    private bool PageVisible(string? moduleId) => Presence is null || Presence.IsPageVisible(moduleId);

    private bool PublishSystem(string? moduleId, FeedbackKind kind, string title, string body, string? details, bool openErrorDialog)
    {
        var backend = SystemNotifications;
        if (backend is null || !backend.IsAvailable) return false;
        var key = string.Join('\u001f', moduleId, kind, title, body.Trim());
        var now = Clock();
        lock (_gate)
        {
            if (_systemRecent.TryGetValue(key, out var at) && now - at < SystemNotificationThrottle)
                return true;
        }

        var request = new SystemNotificationRequest
        {
            Token = Guid.NewGuid().ToString("N"),
            ModuleId = moduleId,
            Kind = kind,
            Title = title,
            Body = body.Trim(),
            Details = string.IsNullOrWhiteSpace(details) ? null : details,
            OpenErrorDialog = openErrorDialog
        };
        if (!backend.TryPublish(request)) return false;
        lock (_gate)
        {
            _systemRecent[key] = now;
            _posted[request.Token] = request;
            if (_systemRecent.Count > 64)
            {
                foreach (var stale in _systemRecent.Where(pair => now - pair.Value >= SystemNotificationThrottle).Select(pair => pair.Key).ToArray())
                    _systemRecent.Remove(stale);
            }
        }

        return true;
    }

    private void OnSystemActivated(object? sender, SystemNotificationActivation e)
    {
        SystemNotificationRequest? request;
        lock (_gate)
        {
            if (!_posted.Remove(e.Token, out request)) return;
        }

        _post(() => SystemNotificationActivated?.Invoke(this, request));
    }

    private void Publish() => _post(() => Changed?.Invoke(this, EventArgs.Empty));

    private sealed record DeferredFeedback(string? ModuleId, FeedbackToast? Toast, FeedbackDialog? Dialog);

    private static void DefaultPost(Action action)
    {
        var dispatcher = Dispatcher.UIThread;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.Post(action);
    }
}
