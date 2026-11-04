using AsterDock.UI.Motion;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace AsterDock.UI.Controls;

public class AdSkeleton : Control, ILoadingClockClient
{
    private const double LoopMs = 1400;

    public static readonly StyledProperty<AdSkeletonKind> KindProperty =
        AvaloniaProperty.Register<AdSkeleton, AdSkeletonKind>(nameof(Kind), AdSkeletonKind.Rectangle);

    public static readonly StyledProperty<double> ShapeCornerRadiusProperty =
        AvaloniaProperty.Register<AdSkeleton, double>(nameof(ShapeCornerRadius), 12);

    private IBrush? _base;
    private LinearGradientBrush? _shine;
    private bool _dark;
    private bool _subscribed;

    public AdSkeleton()
    {
        Focusable = false;
    }

    public AdSkeletonKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double ShapeCornerRadius
    {
        get => GetValue(ShapeCornerRadiusProperty);
        set => SetValue(ShapeCornerRadiusProperty, value);
    }

    protected override Size MeasureOverride(Size available)
    {
        var width = Width;
        var height = Height;
        if (double.IsNaN(width))
        {
            width = Kind switch
            {
                AdSkeletonKind.Circle => 40,
                AdSkeletonKind.Line => double.IsFinite(available.Width) ? available.Width : 180,
                _ => double.IsFinite(available.Width) ? available.Width : 220
            };
        }

        if (double.IsNaN(height))
        {
            height = Kind switch
            {
                AdSkeletonKind.Circle => 40,
                AdSkeletonKind.Line => 12,
                _ => 96
            };
        }

        return new Size(Math.Max(0, width), Math.Max(0, height));
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        EnsureRunning();
        EnsureBrushes();
        if (_base is null) return;

        var radius = Kind switch
        {
            AdSkeletonKind.Circle => Math.Min(size.Width, size.Height) / 2,
            AdSkeletonKind.Line => size.Height / 2,
            _ => Math.Min(Math.Max(0, ShapeCornerRadius), Math.Min(size.Width, size.Height) / 2)
        };
        var rect = new Rect(size);
        using (context.PushClip(new RoundedRect(rect, radius)))
        {
            context.DrawRectangle(_base, null, rect);
            if (MotionPreferences.ReduceMotion || _shine is null) return;
            var phase = (LoadingClock.Now.TotalMilliseconds % LoopMs) / LoopMs;
            if (phase < 0) phase += 1;
            var start = (phase * 1.8) - 0.9;
            _shine.StartPoint = new RelativePoint(start, start, RelativeUnit.Relative);
            _shine.EndPoint = new RelativePoint(start + 0.7, start + 0.7, RelativeUnit.Relative);
            context.DrawRectangle(_shine, null, rect);
        }
    }

    bool ILoadingClockClient.OnClock(TimeSpan now)
    {
        if (VisualRoot is null || !IsEffectivelyVisible || MotionPreferences.ReduceMotion)
        {
            _subscribed = false;
            return false;
        }

        InvalidateVisual();
        return true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MotionPreferences.ReduceMotionChanged += OnReduceMotionChanged;
        ActualThemeVariantChanged += OnThemeChanged;
        _base = null;
        EnsureRunning();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        MotionPreferences.ReduceMotionChanged -= OnReduceMotionChanged;
        ActualThemeVariantChanged -= OnThemeChanged;
        LoadingClock.Unsubscribe(this);
        _subscribed = false;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty || change.Property == ShapeCornerRadiusProperty
            || change.Property == WidthProperty || change.Property == HeightProperty || change.Property == IsVisibleProperty)
        {
            InvalidateMeasure();
            InvalidateVisual();
        }

        if (change.Property == IsVisibleProperty) UpdateClock();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _base = null;
        InvalidateVisual();
    }

    private void OnReduceMotionChanged(object? sender, EventArgs e)
    {
        UpdateClock();
        InvalidateVisual();
    }

    private void EnsureRunning()
    {
        if (_subscribed || MotionPreferences.ReduceMotion || VisualRoot is null || !IsEffectivelyVisible) return;
        _subscribed = true;
        LoadingClock.Subscribe(this);
    }

    private void UpdateClock()
    {
        if (!MotionPreferences.ReduceMotion && VisualRoot is not null && IsEffectivelyVisible) EnsureRunning();
        else
        {
            LoadingClock.Unsubscribe(this);
            _subscribed = false;
        }
    }

    private void EnsureBrushes()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        if (_base is not null && _dark == dark && _shine is not null) return;
        _dark = dark;
        if (TryGetResource("AppSkeletonBrush", ActualThemeVariant, out var value) && value is IBrush brush)
            _base = brush;
        else
            _base = new SolidColorBrush(dark ? Color.Parse("#2A313B") : Color.Parse("#E7ECF3"));

        Color highlight;
        if (TryGetResource("AppSkeletonHighlightBrush", ActualThemeVariant, out var shineValue) && shineValue is ISolidColorBrush shineBrush)
            highlight = Color.FromArgb(dark ? (byte)150 : (byte)230, shineBrush.Color.R, shineBrush.Color.G, shineBrush.Color.B);
        else
            highlight = dark ? Color.FromArgb(150, 58, 66, 80) : Color.FromArgb(230, 248, 250, 252);
        _shine = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(Colors.Transparent, 0.34),
                new GradientStop(highlight, 0.5),
                new GradientStop(Colors.Transparent, 0.66),
                new GradientStop(Colors.Transparent, 1)
            }
        };
    }
}
