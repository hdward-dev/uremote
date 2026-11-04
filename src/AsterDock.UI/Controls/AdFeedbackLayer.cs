using System.Diagnostics;
using AsterDock.UI.Motion;
using AsterDock.UI.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Irihi.Avalonia.Shared.Contracts;
using Ursa.Controls;

namespace AsterDock.UI.Controls;

/// <summary>
/// Hosts Ursa <see cref="OverlayDialog"/> cards and the top-right toast stack for one window.
/// </summary>
public class AdFeedbackLayer : Panel
{
    public static readonly StyledProperty<UserFeedbackService?> ServiceProperty =
        AvaloniaProperty.Register<AdFeedbackLayer, UserFeedbackService?>(nameof(Service));

    private readonly OverlayDialogHost _dialogs;
    private readonly AdToastHost _toasts;
    private readonly DispatcherTimer _clock;
    private Window? _window;
    private bool _presenting;
    private bool _attached;
    private bool _overlayOpen;
    private int _generation;

    public AdFeedbackLayer()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        HostId = "ad-feedback-" + Guid.NewGuid().ToString("N");
        _dialogs = new OverlayDialogHost
        {
            HostId = HostId,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
            IsHitTestVisible = false
        };
        _toasts = new AdToastHost
        {
            Margin = new Thickness(0, 16, 16, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        };
        Children.Add(_toasts);
        Children.Add(_dialogs);
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _clock.Tick += (_, _) => Service?.Advance(Service.Clock());
        MotionPreferences.ReduceMotionChanged += OnMotionChanged;
    }

    public string HostId { get; }

    public UserFeedbackService? Service
    {
        get => GetValue(ServiceProperty);
        set => SetValue(ServiceProperty, value);
    }

    static AdFeedbackLayer()
    {
        ServiceProperty.Changed.AddClassHandler<AdFeedbackLayer>((layer, args) =>
        {
            if (args.OldValue is UserFeedbackService previous) previous.Changed -= layer.OnServiceChanged;
            if (args.NewValue is UserFeedbackService next)
            {
                next.Changed += layer.OnServiceChanged;
                if (layer._attached) layer.OnServiceChanged(next, EventArgs.Empty);
            }
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = true;
        base.OnAttachedToVisualTree(e);
        ApplyMask();
        _dialogs.IsAnimationDisabled = !FeedbackMotion.AnimationsEnabled;
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            _window = window;
            window.Activated += OnWindowActivated;
            if (window.IsActive || Service?.ActiveHostId is null) Service?.ActivateHost(HostId);
        }
        else if (Service?.ActiveHostId is null)
        {
            Service?.ActivateHost(HostId);
        }

        _clock.Start();
        OnServiceChanged(Service, EventArgs.Empty);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _clock.Stop();
        if (_window is not null)
        {
            _window.Activated -= OnWindowActivated;
            _window = null;
        }

        var service = Service;
        service?.ReleaseHost(HostId);
        if (service?.ActiveDialog?.HostId == HostId) service.CancelActiveDialog();
        _generation++;
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var finite = new Size(
            double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height);
        var dialog = _overlayOpen && Service?.ActiveDialog?.HostId == HostId;
        _toasts.Measure(new Size(Math.Min(380, finite.Width), finite.Height));
        if (!dialog)
        {
            _dialogs.Measure(new Size(0, 0));
            return _toasts.IsVisible ? _toasts.DesiredSize : default;
        }

        _dialogs.Measure(finite);
        return finite;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var dialog = _overlayOpen && Service?.ActiveDialog?.HostId == HostId;
        if (dialog) _dialogs.Arrange(new Rect(finalSize));
        else _dialogs.Arrange(default);
        var toast = _toasts.DesiredSize;
        var width = Math.Min(toast.Width, Math.Max(0, finalSize.Width));
        var height = Math.Min(toast.Height, Math.Max(0, finalSize.Height));
        _toasts.Arrange(dialog
            ? new Rect(Math.Max(0, finalSize.Width - width - 16), 16, width, height)
            : new Rect(0, 0, width, height));
        return finalSize;
    }

    private void OnWindowActivated(object? sender, EventArgs e) => Service?.ActivateHost(HostId);

    private void OnMotionChanged(object? sender, EventArgs e) =>
        _dialogs.IsAnimationDisabled = !FeedbackMotion.AnimationsEnabled;

    private void OnServiceChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnServiceChanged(sender, e));
            return;
        }

        var service = Service;
        if (service is null) return;
        var showToasts = service.ActiveHostId == HostId;
        if (showToasts) _toasts.Show(service);
        else _toasts.ClearToasts();
        var dialogOpen = _overlayOpen && service.ActiveDialog?.HostId == HostId;
        _dialogs.IsHitTestVisible = dialogOpen;
        ApplyHitTarget(dialogOpen);
        _ = PresentLoop();
    }

    private async Task PresentLoop()
    {
        if (_presenting || Service is null || !_attached) return;
        _presenting = true;
        var generation = _generation;
        try
        {
            while (generation == _generation
                   && Service?.ActiveDialog is { } dialog
                   && dialog.HostId == HostId
                   && _attached)
            {
                ApplyMask();
                _dialogs.IsAnimationDisabled = !FeedbackMotion.AnimationsEnabled;
                var session = new DialogSession();
                var card = new AdDialogCard(dialog);
                var id = dialog.Id;
                card.Accepted += (_, _) =>
                {
                    if (Service?.ActiveDialog?.Id == id) Service.AcceptActiveDialog();
                    session.Close(true);
                };
                card.Cancelled += (_, _) =>
                {
                    if (Service?.ActiveDialog?.Id == id) Service.CancelActiveDialog();
                    session.Close(false);
                };
                _overlayOpen = true;
                _dialogs.IsHitTestVisible = true;
                ApplyHitTarget(dialogOpen: true);
                InvalidateMeasure();
                if (TopLevel.GetTopLevel(this) is Layoutable root) root.UpdateLayout();
                Task<bool> shown;
                try
                {
                    shown = OverlayDialog.ShowCustomAsync<bool>(card, session, HostId, new OverlayDialogOptions
                    {
                        CanLightDismiss = false,
                        CanDragMove = false,
                        CanResize = false,
                        IsCloseButtonVisible = false,
                        Buttons = DialogButton.None,
                        Title = ""
                    });
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Trace.TraceError("反馈对话框未能打开: " + exception);
                    AbandonDialog(id);
                    continue;
                }

                if (shown.IsFaulted || shown.IsCanceled)
                {
                    Trace.TraceError("反馈对话框未能打开: " + shown.Exception);
                    AbandonDialog(id);
                    continue;
                }

                if (shown.IsCompleted && TopLevel.GetTopLevel(card) is null)
                {
                    _overlayOpen = false;
                    ReleaseDialogHitTest();
                    Dispatcher.UIThread.Post(() => _ = PresentLoop(), DispatcherPriority.Background);
                    return;
                }

                _overlayOpen = true;
                _dialogs.IsHitTestVisible = true;
                ApplyHitTarget(dialogOpen: true);
                try
                {
                    await shown.ConfigureAwait(true);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Trace.TraceError("反馈对话框未能打开: " + exception);
                    AbandonDialog(id);
                    continue;
                }
                finally
                {
                    _overlayOpen = false;
                    ReleaseDialogHitTest();
                }

                if (Service?.ActiveDialog?.Id == id) Service.CancelActiveDialog();
            }
        }
        finally
        {
            _presenting = false;
            if (Service?.ActiveDialog?.HostId != HostId) ReleaseDialogHitTest();
        }
    }

    private void AbandonDialog(Guid id)
    {
        _overlayOpen = false;
        ReleaseDialogHitTest();
        if (Service?.ActiveDialog?.Id == id) Service.CancelActiveDialog();
    }

    private void ReleaseDialogHitTest()
    {
        _dialogs.IsHitTestVisible = false;
        ApplyHitTarget(dialogOpen: false);
    }

    private void ApplyHitTarget(bool dialogOpen)
    {
        if (dialogOpen)
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            IsHitTestVisible = true;
            return;
        }

        // Idle and toast states must not cover the page. Toast buttons stay inside the corner bounds.
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        IsHitTestVisible = Service?.ActiveHostId == HostId && _toasts.IsVisible;
    }

    private void ApplyMask()
    {
        if (this.TryFindResource("AppScrimBrush", ActualThemeVariant, out var value) && value is IBrush brush)
            _dialogs.OverlayMaskBrush = brush;
    }

    private sealed class DialogSession : IDialogContext
    {
        public event EventHandler<object?>? RequestClose;

        public void Close() => Close(false);

        public void Close(bool accepted) => RequestClose?.Invoke(this, accepted);
    }
}
