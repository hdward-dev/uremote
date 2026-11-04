using AsterDock.UI.Motion;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace AsterDock.UI.Controls;

public class AdLoadingIndicator : TemplatedControl, ILoadingClockClient
{
    private static readonly Color LightAccent = Color.Parse("#1267E8");
    private static readonly Color DarkAccent = Color.Parse("#6EA8FF");
    private static readonly IBrush LightAccentBrush = new SolidColorBrush(LightAccent);
    private static readonly IBrush DarkAccentBrush = new SolidColorBrush(DarkAccent);
    private static readonly IBrush LightContainerBrush = new SolidColorBrush(Color.FromArgb(31, LightAccent.R, LightAccent.G, LightAccent.B));
    private static readonly IBrush DarkContainerBrush = new SolidColorBrush(Color.FromArgb(51, DarkAccent.R, DarkAccent.G, DarkAccent.B));

    public static readonly StyledProperty<AdLoadingSize> SizeProperty =
        AvaloniaProperty.Register<AdLoadingIndicator, AdLoadingSize>(nameof(Size), AdLoadingSize.Medium);

    public static readonly StyledProperty<double> DiameterProperty =
        AvaloniaProperty.Register<AdLoadingIndicator, double>(nameof(Diameter), double.NaN);

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<AdLoadingIndicator, string?>(nameof(Text));

    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<AdLoadingIndicator, double>(nameof(Progress), double.NaN, coerce: CoerceProgress);

    public static readonly StyledProperty<AdLoadingVariant> VariantProperty =
        AvaloniaProperty.Register<AdLoadingIndicator, AdLoadingVariant>(nameof(Variant), AdLoadingVariant.Contained);

    public static readonly StyledProperty<bool> ShowContainerProperty =
        AvaloniaProperty.Register<AdLoadingIndicator, bool>(nameof(ShowContainer), true);

    public static readonly StyledProperty<IBrush?> ShapeBrushProperty =
        AvaloniaProperty.Register<AdLoadingIndicator, IBrush?>(nameof(ShapeBrush));

    public static readonly StyledProperty<IBrush?> ContainerBrushProperty =
        AvaloniaProperty.Register<AdLoadingIndicator, IBrush?>(nameof(ContainerBrush));

    private readonly float[] _radii = new float[LoadingShapes.Samples];
    private readonly Point[] _points = new Point[LoadingShapes.Samples];
    private readonly StreamGeometry _shape = new();
    private readonly StreamGeometry _arc = new();
    private LoadingMark? _mark;
    private TextBlock? _label;
    private Pen? _trackPen;
    private Pen? _progressPen;
    private Color _penColor;
    private double _penThickness = -1;
    private int _variantSync;
    private bool _subscribed;

    public AdLoadingIndicator()
    {
        FontSize = 14;
        Focusable = false;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Template = new FuncControlTemplate<AdLoadingIndicator>((host, scope) => host.CreateTemplate(scope));
    }

    public AdLoadingSize Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double Diameter
    {
        get => GetValue(DiameterProperty);
        set => SetValue(DiameterProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public AdLoadingVariant Variant
    {
        get => GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    public bool ShowContainer
    {
        get => GetValue(ShowContainerProperty);
        set => SetValue(ShowContainerProperty, value);
    }

    public IBrush? ShapeBrush
    {
        get => GetValue(ShapeBrushProperty);
        set => SetValue(ShapeBrushProperty, value);
    }

    public IBrush? ContainerBrush
    {
        get => GetValue(ContainerBrushProperty);
        set => SetValue(ContainerBrushProperty, value);
    }

    public bool IsDeterminate => !double.IsNaN(Progress);

    public bool IsContained => Variant == AdLoadingVariant.Contained && ShowContainer;

    private double MarkDiameter =>
        !double.IsNaN(Diameter) && Diameter > 0
            ? Diameter
            : Size switch
            {
                AdLoadingSize.Small => 20,
                AdLoadingSize.Large => 56,
                _ => 36
            };

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _mark = e.NameScope.Find<LoadingMark>("PART_Glyph");
        _label = e.NameScope.Find<TextBlock>("PART_Label");
        SyncLabel();
        SyncMarkSize();
        _mark?.InvalidateVisual();
    }

    bool ILoadingClockClient.OnClock(TimeSpan now)
    {
        if (VisualRoot is null || !IsEffectivelyVisible || !WantsFrames)
        {
            _subscribed = false;
            return false;
        }

        _mark?.InvalidateVisual();
        return true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MotionPreferences.ReduceMotionChanged += OnReduceMotionChanged;
        ActualThemeVariantChanged += OnThemeChanged;
        EnsureRunning();
        _mark?.InvalidateVisual();
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
        if (change.Property == VariantProperty && _variantSync == 0)
        {
            _variantSync++;
            SetCurrentValue(ShowContainerProperty, Variant == AdLoadingVariant.Contained);
            _variantSync--;
        }
        else if (change.Property == ShowContainerProperty && _variantSync == 0)
        {
            _variantSync++;
            SetCurrentValue(VariantProperty, ShowContainer ? AdLoadingVariant.Contained : AdLoadingVariant.Bare);
            _variantSync--;
        }

        if (change.Property == TextProperty || change.Property == FontSizeProperty || change.Property == ForegroundProperty)
            SyncLabel();

        if (change.Property == SizeProperty || change.Property == DiameterProperty || change.Property == VariantProperty
            || change.Property == ShowContainerProperty || change.Property == TextProperty || change.Property == ProgressProperty
            || change.Property == IsVisibleProperty)
        {
            UpdateClockSubscription();
            SyncMarkSize();
            InvalidateMeasure();
            _mark?.InvalidateVisual();
        }

        if (change.Property == ShapeBrushProperty || change.Property == ContainerBrushProperty)
        {
            _penThickness = -1;
            _mark?.InvalidateVisual();
        }
    }

    private Control CreateTemplate(INameScope scope)
    {
        var mark = new LoadingMark(this) { Name = "PART_Glyph" };
        scope.Register("PART_Glyph", mark);
        var label = new TextBlock
        {
            Name = "PART_Label",
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            FontSize = 14,
            IsVisible = false
        };
        scope.Register("PART_Label", label);
        return new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { mark, label }
        };
    }

    private void DrawMark(DrawingContext context, Size size)
    {
        if (size.Width <= 1 || size.Height <= 1) return;
        EnsureRunning();
        var accent = ResolveAccent();
        var now = LoadingClock.Now;
        var reduce = MotionPreferences.ReduceMotion;
        var center = new Point(size.Width / 2, size.Height / 2);
        var diameter = Math.Min(size.Width, size.Height);
        var opacity = reduce && !IsDeterminate ? LoadingShapes.PulseOpacity(now) : 1;
        using (context.PushOpacity(opacity))
        {
            if (IsContained)
            {
                var container = ContainerBrush ?? (IsDark ? DarkContainerBrush : LightContainerBrush);
                context.DrawEllipse(container, null, center, diameter / 2, diameter / 2);
            }

            var shapeRadius = diameter * (IsContained ? 1d / 3d : 0.46);
            if (IsDeterminate)
            {
                DrawCircle(center, shapeRadius);
                context.DrawGeometry(accent, null, _shape.Clone());
                DrawProgress(context, center, diameter, accent);
            }
            else
            {
                LoadingShapes.Interpolate(now, reduce, _radii);
                DrawPolar(center, shapeRadius, LoadingShapes.Rotation(now, reduce));
                context.DrawGeometry(accent, null, _shape.Clone());
            }
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _penThickness = -1;
        SyncLabel();
        _mark?.InvalidateVisual();
    }

    private void OnReduceMotionChanged(object? sender, EventArgs e)
    {
        UpdateClockSubscription();
        _mark?.InvalidateVisual();
    }

    private bool WantsFrames => !IsDeterminate;

    private bool IsDark => ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;

    private void EnsureRunning()
    {
        if (_subscribed || !WantsFrames || VisualRoot is null || !IsEffectivelyVisible) return;
        _subscribed = true;
        LoadingClock.Subscribe(this);
    }

    private void UpdateClockSubscription()
    {
        if (WantsFrames && VisualRoot is not null && IsEffectivelyVisible) EnsureRunning();
        else
        {
            LoadingClock.Unsubscribe(this);
            _subscribed = false;
        }
    }

    private void SyncMarkSize()
    {
        if (_mark is null) return;
        var diameter = MarkDiameter;
        _mark.Width = diameter;
        _mark.Height = diameter;
    }

    private void SyncLabel()
    {
        if (_label is null) return;
        var text = string.IsNullOrWhiteSpace(Text) ? null : Text;
        _label.Text = text;
        _label.IsVisible = text is not null;
        _label.FontSize = FontSize > 0 ? FontSize : 14;
        _label.Foreground = Foreground ?? ResolveStrongText();
        _label.HorizontalAlignment = HorizontalAlignment.Center;
        _label.TextAlignment = TextAlignment.Center;
    }

    private IBrush ResolveAccent() => ShapeBrush ?? (IsDark ? DarkAccentBrush : LightAccentBrush);

    private IBrush ResolveStrongText()
    {
        if (this.TryGetResource("AppStrongTextBrush", ActualThemeVariant, out var value) && value is IBrush brush)
            return brush;
        return IsDark ? Brushes.White : Brushes.Black;
    }

    private void DrawPolar(Point center, double maxRadius, double rotation)
    {
        for (var i = 0; i < LoadingShapes.Samples; i++)
        {
            var theta = rotation + (i * Math.Tau / LoadingShapes.Samples);
            var radius = _radii[i] * maxRadius;
            _points[i] = new Point(center.X + (Math.Cos(theta) * radius), center.Y + (Math.Sin(theta) * radius));
        }

        FillSmooth(_shape);
    }

    private void DrawCircle(Point center, double radius)
    {
        for (var i = 0; i < LoadingShapes.Samples; i++)
        {
            var theta = i * Math.Tau / LoadingShapes.Samples;
            _points[i] = new Point(center.X + (Math.Cos(theta) * radius), center.Y + (Math.Sin(theta) * radius));
        }

        FillSmooth(_shape);
    }

    private void FillSmooth(StreamGeometry geometry)
    {
        var count = LoadingShapes.Samples;
        using var ctx = geometry.Open();
        ctx.BeginFigure(_points[0], isFilled: true);
        for (var i = 0; i < count; i++)
        {
            var p0 = _points[(i - 1 + count) % count];
            var p1 = _points[i];
            var p2 = _points[(i + 1) % count];
            var p3 = _points[(i + 2) % count];
            var c1 = new Point(p1.X + ((p2.X - p0.X) / 6), p1.Y + ((p2.Y - p0.Y) / 6));
            var c2 = new Point(p2.X - ((p3.X - p1.X) / 6), p2.Y - ((p3.Y - p1.Y) / 6));
            ctx.CubicBezierTo(c1, c2, p2);
        }

        ctx.EndFigure(true);
    }

    private void DrawProgress(DrawingContext context, Point center, double diameter, IBrush accent)
    {
        var color = (accent as ISolidColorBrush)?.Color ?? LightAccent;
        var thickness = Math.Clamp(diameter * 0.065, 2, 3.5);
        if (_trackPen is null || _penColor != color || Math.Abs(_penThickness - thickness) > 0.01)
        {
            _penColor = color;
            _penThickness = thickness;
            var track = new SolidColorBrush(Color.FromArgb(31, color.R, color.G, color.B));
            _trackPen = new Pen(track, thickness, lineCap: PenLineCap.Round);
            _progressPen = new Pen(accent, thickness, lineCap: PenLineCap.Round);
        }

        var radius = (diameter / 2) - (thickness / 2) - 0.75;
        if (radius <= 0 || _trackPen is null || _progressPen is null) return;
        context.DrawEllipse(null, _trackPen, center, radius, radius);
        var progress = Math.Clamp(Progress, 0, 1);
        if (progress <= 0) return;
        if (progress >= 0.999)
        {
            context.DrawEllipse(null, _progressPen, center, radius, radius);
            return;
        }

        var sweep = progress * Math.Tau;
        var start = -Math.PI / 2;
        var steps = Math.Max(16, (int)Math.Ceiling(sweep / Math.Tau * 120));
        using (var ctx = _arc.Open())
        {
            ctx.BeginFigure(PointOn(center, radius, start), isFilled: false);
            for (var i = 1; i <= steps; i++)
                ctx.LineTo(PointOn(center, radius, start + (sweep * i / steps)));
            ctx.EndFigure(false);
        }

        context.DrawGeometry(null, _progressPen, _arc.Clone());
    }

    private static Point PointOn(Point center, double radius, double angle)
        => new(center.X + (Math.Cos(angle) * radius), center.Y + (Math.Sin(angle) * radius));

    private static double CoerceProgress(AvaloniaObject sender, double value)
        => double.IsNaN(value) ? value : Math.Clamp(value, 0, 1);

    private sealed class LoadingMark : Control
    {
        private readonly AdLoadingIndicator _host;

        public LoadingMark(AdLoadingIndicator host)
        {
            _host = host;
            Focusable = false;
            IsHitTestVisible = false;
            HorizontalAlignment = HorizontalAlignment.Center;
        }

        public override void Render(DrawingContext context) => _host.DrawMark(context, Bounds.Size);
    }
}
