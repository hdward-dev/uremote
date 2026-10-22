using Avalonia;
using Avalonia.Controls;

namespace URemote.Module;

/// <summary>
/// Wraps device cards into rows that fill the viewport.
/// A Grid with star columns inside the page StackPanel measures at width 0, so each card is given a finite width instead.
/// </summary>
public sealed class DeviceCardWrap : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<DeviceCardWrap, double>(nameof(MinItemWidth), 380);

    public static readonly StyledProperty<double> MaxItemWidthProperty =
        AvaloniaProperty.Register<DeviceCardWrap, double>(nameof(MaxItemWidth), 640);

    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<DeviceCardWrap, double>(nameof(ItemSpacing), 16);

    public static readonly StyledProperty<double> LineSpacingProperty =
        AvaloniaProperty.Register<DeviceCardWrap, double>(nameof(LineSpacing), 16);

    public static readonly StyledProperty<bool> SingleColumnProperty =
        AvaloniaProperty.Register<DeviceCardWrap, bool>(nameof(SingleColumn));

    static DeviceCardWrap()
    {
        AffectsMeasure<DeviceCardWrap>(MinItemWidthProperty, MaxItemWidthProperty, ItemSpacingProperty, LineSpacingProperty, SingleColumnProperty);
    }

    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double MaxItemWidth { get => GetValue(MaxItemWidthProperty); set => SetValue(MaxItemWidthProperty, value); }
    public double ItemSpacing { get => GetValue(ItemSpacingProperty); set => SetValue(ItemSpacingProperty, value); }
    public double LineSpacing { get => GetValue(LineSpacingProperty); set => SetValue(LineSpacingProperty, value); }
    public bool SingleColumn { get => GetValue(SingleColumnProperty); set => SetValue(SingleColumnProperty, value); }

    /// <summary>
    /// Column count and card width for a viewport. Grid mode fills each row between <paramref name="min"/> and <paramref name="max"/>.
    /// A single column (list mode, or an empty catalog message) uses the full available width.
    /// </summary>
    public static (int Columns, double ItemWidth) Resolve(double available, double min, double max, double spacing, bool grid)
    {
        spacing = Math.Max(0, spacing);
        min = Math.Max(1, min);
        max = Math.Max(min, max);
        if (!grid || !double.IsFinite(available) || available <= 1)
        {
            var fallback = !double.IsFinite(available) || available <= 1 ? (grid ? min : max) : available;
            return (1, Math.Max(1, grid ? Math.Min(fallback, max) : fallback));
        }

        var columns = Math.Max(1, (int)Math.Floor((available + spacing) / (min + spacing)));
        double Fill(int count) => (available - (count - 1) * spacing) / count;
        if (Fill(columns) > max)
        {
            var fitted = Math.Max(1, (int)Math.Ceiling((available + spacing) / (max + spacing)));
            if (fitted > 1 && Fill(fitted) >= min - 0.01)
                columns = fitted;
        }

        var width = Fill(columns);
        if (width > max) width = max;
        if (width > available) width = available;
        return (columns, Math.Max(1, width));
    }

    protected override Size MeasureOverride(Size available)
    {
        var (columns, itemWidth) = ResolveLayout(available.Width);
        var lines = BuildLines(itemWidth, columns);
        return new Size(DesiredWidth(available.Width, itemWidth), HeightOf(lines));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, itemWidth) = ResolveLayout(finalSize.Width);
        var lines = BuildLines(itemWidth, columns);
        var spacing = Math.Max(0, ItemSpacing);
        var lineSpacing = Math.Max(0, LineSpacing);
        double y = 0;
        for (var row = 0; row < lines.Count; row++)
        {
            if (row > 0) y += lineSpacing;
            double x = 0;
            foreach (var child in lines[row].Children)
            {
                child.Arrange(new Rect(x, y, itemWidth, lines[row].Height));
                x += itemWidth + spacing;
            }
            y += lines[row].Height;
        }
        return finalSize;
    }

    private (int Columns, double ItemWidth) ResolveLayout(double available)
    {
        var grid = !SingleColumn && Children.Any(child => child.IsVisible && child is DeviceCard);
        return Resolve(available, MinItemWidth, MaxItemWidth, ItemSpacing, grid);
    }

    private List<Line> BuildLines(double itemWidth, int columns)
    {
        var lines = new List<Line>();
        var row = new List<Control>();
        double rowHeight = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            if (double.IsNaN(child.Width) || Math.Abs(child.Width - itemWidth) > 0.5)
                child.Width = itemWidth;
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            row.Add(child);
            if (row.Count == Math.Max(1, columns))
            {
                lines.Add(new Line(row.ToArray(), rowHeight));
                row.Clear();
                rowHeight = 0;
            }
        }
        if (row.Count > 0) lines.Add(new Line(row.ToArray(), rowHeight));
        return lines;
    }

    private double HeightOf(List<Line> lines)
    {
        if (lines.Count == 0) return 0;
        var lineSpacing = Math.Max(0, LineSpacing);
        return lines.Sum(line => line.Height) + lineSpacing * (lines.Count - 1);
    }

    private static double DesiredWidth(double available, double itemWidth) =>
        double.IsFinite(available) && available > 1 ? available : itemWidth;

    private readonly record struct Line(Control[] Children, double Height);
}
