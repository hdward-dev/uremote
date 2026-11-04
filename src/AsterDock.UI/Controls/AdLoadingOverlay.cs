using AsterDock.UI.Motion;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;

namespace AsterDock.UI.Controls;

public class AdLoadingOverlay : ContentControl, ILoadingClockClient
{
    private const double FadeMs = 180;
    private const double CheapBlurExtent = 720;

    public static readonly StyledProperty<bool> IsLoadingProperty =
        AvaloniaProperty.Register<AdLoadingOverlay, bool>(nameof(IsLoading));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<AdLoadingOverlay, string?>(nameof(Text));

    public static readonly StyledProperty<int> DelayMsProperty =
        AvaloniaProperty.Register<AdLoadingOverlay, int>(nameof(DelayMs), 150);

    public static readonly StyledProperty<int> MinimumVisibleMsProperty =
        AvaloniaProperty.Register<AdLoadingOverlay, int>(nameof(MinimumVisibleMs), 300);

    public static readonly StyledProperty<bool> FollowContentCornerRadiusProperty =
        AvaloniaProperty.Register<AdLoadingOverlay, bool>(nameof(FollowContentCornerRadius), true);

    public static readonly StyledProperty<bool> IsCancellableProperty =
        AvaloniaProperty.Register<AdLoadingOverlay, bool>(nameof(IsCancellable));

    public static readonly StyledProperty<AdLoadingSize> IndicatorSizeProperty =
        AvaloniaProperty.Register<AdLoadingOverlay, AdLoadingSize>(nameof(IndicatorSize), AdLoadingSize.Medium);

    public static readonly StyledProperty<bool> UseBlurProperty =
        AvaloniaProperty.Register<AdLoadingOverlay, bool>(nameof(UseBlur), true);

    private readonly LoadingVisibilityScheduler _scheduler = new();
    private DispatcherTimer? _deadline;
    private Border? _watched;
    private Border? _scrim;
    private Border? _card;
    private ContentPresenter? _content;
    private ScaleTransform? _scale;
    private AdLoadingIndicator? _indicator;
    private Button? _cancel;
    private BlurEffect? _blur;
    private FadeKind _fade = FadeKind.None;
    private TimeSpan _fadeStart;
    private bool _clock;

    public event EventHandler? CancelRequested;

    public bool IsLoading
    {
        get => GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public int DelayMs
    {
        get => GetValue(DelayMsProperty);
        set => SetValue(DelayMsProperty, value);
    }

    public int MinimumVisibleMs
    {
        get => GetValue(MinimumVisibleMsProperty);
        set => SetValue(MinimumVisibleMsProperty, value);
    }

    public bool FollowContentCornerRadius
    {
        get => GetValue(FollowContentCornerRadiusProperty);
        set => SetValue(FollowContentCornerRadiusProperty, value);
    }

    public bool IsCancellable
    {
        get => GetValue(IsCancellableProperty);
        set => SetValue(IsCancellableProperty, value);
    }

    public AdLoadingSize IndicatorSize
    {
        get => GetValue(IndicatorSizeProperty);
        set => SetValue(IndicatorSizeProperty, value);
    }

    public bool UseBlur
    {
        get => GetValue(UseBlurProperty);
        set => SetValue(UseBlurProperty, value);
    }

    internal bool IsPresented => _scheduler.IsPresented;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_cancel is not null) _cancel.Click -= OnCancelClick;
        _scrim = e.NameScope.Find<Border>("PART_Scrim");
        _card = e.NameScope.Find<Border>("PART_Card");
        _content = e.NameScope.Find<ContentPresenter>("PART_Content");
        _indicator = e.NameScope.Find<AdLoadingIndicator>("PART_Indicator");
        _cancel = e.NameScope.Find<Button>("PART_Cancel");
        var scaled = (Control?)_card ?? e.NameScope.Find<StackPanel>("PART_Chrome");
        if (scaled is not null)
        {
            scaled.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
            _scale = scaled.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
            scaled.RenderTransform = _scale;
        }

        if (_cancel is not null) _cancel.Click += OnCancelClick;
        SyncChrome();
        WatchContentCorner();
        ApplyPresented(_scheduler.IsPresented, animate: false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsLoadingProperty || change.Property == DelayMsProperty || change.Property == MinimumVisibleMsProperty)
            OnLoadingChanged();
        if (change.Property == ContentProperty || change.Property == FollowContentCornerRadiusProperty)
            WatchContentCorner();
        if (change.Property == TextProperty || change.Property == IndicatorSizeProperty || change.Property == IsCancellableProperty)
            SyncChrome();
        if (change.Property == CornerRadiusProperty && _scrim is not null)
            _scrim.CornerRadius = CornerRadius;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _deadline?.Stop();
        StopClock();
        if (_watched is not null) _watched.PropertyChanged -= OnWatchedPropertyChanged;
        _watched = null;
        base.OnDetachedFromVisualTree(e);
    }

    bool ILoadingClockClient.OnClock(TimeSpan now)
    {
        if (_fade == FadeKind.None || _scrim is null)
        {
            _clock = false;
            return false;
        }

        ApplyFade(now);
        return _fade != FadeKind.None;
    }

    private void OnLoadingChanged()
    {
        _scheduler.Delay = TimeSpan.FromMilliseconds(Math.Max(0, DelayMs));
        _scheduler.MinimumVisible = TimeSpan.FromMilliseconds(Math.Max(0, MinimumVisibleMs));
        var changed = _scheduler.SetRequested(IsLoading, DateTimeOffset.UtcNow);
        if (changed) ApplyPresented(_scheduler.IsPresented, animate: true);
        ArmDeadline();
    }

    private void ArmDeadline()
    {
        _deadline?.Stop();
        if (_scheduler.NextTransitionAt is not { } due) return;
        var delay = due - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        _deadline ??= new DispatcherTimer();
        _deadline.Interval = delay <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : delay;
        _deadline.Tick -= OnDeadline;
        _deadline.Tick += OnDeadline;
        _deadline.Start();
    }

    private void OnDeadline(object? sender, EventArgs e)
    {
        _deadline?.Stop();
        var now = DateTimeOffset.UtcNow;
        if (_scheduler.Advance(now)) ApplyPresented(_scheduler.IsPresented, animate: true);
        ArmDeadline();
    }

    private void ApplyPresented(bool presented, bool animate)
    {
        if (_scrim is null) return;
        _scrim.CornerRadius = CornerRadius;
        if (presented)
        {
            _scrim.IsVisible = true;
            IsHitTestVisible = true;
            ApplyBlur(true);
            if (!animate || MotionPreferences.ReduceMotion)
            {
                _fade = FadeKind.None;
                StopClock();
                _scrim.Opacity = 1;
                SetScale(1);
                return;
            }

            _fade = FadeKind.In;
            _fadeStart = LoadingClock.Now;
            _scrim.Opacity = 0;
            SetScale(0.96);
            StartClock();
            ApplyFade(LoadingClock.Now);
            return;
        }

        IsHitTestVisible = false;
        if (!animate || MotionPreferences.ReduceMotion || !_scrim.IsVisible)
        {
            HideImmediate();
            return;
        }

        _fade = FadeKind.Out;
        _fadeStart = LoadingClock.Now;
        StartClock();
    }

    private void ApplyFade(TimeSpan now)
    {
        if (_scrim is null || _fade == FadeKind.None) return;
        var t = Math.Clamp((now - _fadeStart).TotalMilliseconds / FadeMs, 0, 1);
        var eased = EmphasizedEasing.Ease(t);
        if (_fade == FadeKind.In)
        {
            _scrim.Opacity = eased;
            SetScale(0.96 + (0.04 * eased));
            if (t >= 1)
            {
                _fade = FadeKind.None;
                StopClock();
            }

            return;
        }

        _scrim.Opacity = 1 - eased;
        SetScale(1 - (0.02 * eased));
        if (t >= 1) HideImmediate();
    }

    private void HideImmediate()
    {
        _fade = FadeKind.None;
        StopClock();
        ApplyBlur(false);
        if (_scrim is not null)
        {
            _scrim.Opacity = 0;
            _scrim.IsVisible = false;
        }

        SetScale(1);
        IsHitTestVisible = false;
    }

    private void ApplyBlur(bool enabled)
    {
        if (_content is null) return;
        var cheap = enabled && UseBlur && !MotionPreferences.ReduceMotion
            && Bounds.Width > 0 && Bounds.Height > 0
            && Bounds.Width <= CheapBlurExtent && Bounds.Height <= CheapBlurExtent;
        if (!cheap)
        {
            _content.Effect = null;
            return;
        }

        _blur ??= new BlurEffect { Radius = 3 };
        _content.Effect = _blur;
    }

    private void SyncChrome()
    {
        if (_indicator is not null)
        {
            _indicator.Size = IndicatorSize;
            _indicator.Text = Text;
            _indicator.Variant = AdLoadingVariant.Contained;
            _indicator.FontSize = 14;
        }

        if (_cancel is not null) _cancel.IsVisible = IsCancellable;
        if (_scrim is not null) _scrim.CornerRadius = CornerRadius;
    }

    private void SetScale(double value)
    {
        if (_scale is null) return;
        _scale.ScaleX = value;
        _scale.ScaleY = value;
    }

    private void StartClock()
    {
        if (_clock) return;
        _clock = true;
        LoadingClock.Subscribe(this);
    }

    private void StopClock()
    {
        if (!_clock) return;
        _clock = false;
        LoadingClock.Unsubscribe(this);
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => CancelRequested?.Invoke(this, EventArgs.Empty);

    private void WatchContentCorner()
    {
        if (_watched is not null) _watched.PropertyChanged -= OnWatchedPropertyChanged;
        _watched = FollowContentCornerRadius && Content is Border border ? border : null;
        if (_watched is null) return;
        SetCurrentValue(CornerRadiusProperty, _watched.CornerRadius);
        _watched.PropertyChanged += OnWatchedPropertyChanged;
    }

    private void OnWatchedPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Border.CornerRadiusProperty && sender is Border border && FollowContentCornerRadius)
            SetCurrentValue(CornerRadiusProperty, border.CornerRadius);
    }

    private enum FadeKind
    {
        None,
        In,
        Out
    }
}
