using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;

namespace URemote.Module;

public partial class DeviceCard : Border
{
    public DeviceCard() => InitializeComponent();

    private DeviceCardModel? Model => DataContext as DeviceCardModel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyChrome();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (VisualRoot is not null) ApplyChrome();
    }

    private void ApplyChrome()
    {
        Background = Brush("AppSurfaceBrush", Colors.White);
        BorderBrush = Model?.IsSelected == true
            ? Brush("AppAccentBrush", Color.FromRgb(0x12, 0x67, 0xE8))
            : Brush("AppBorderBrush", Color.FromRgb(0xE2, 0xE7, 0xEF));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(12);
    }

    private IBrush Brush(string key, Color fallback) =>
        TryGetResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : new SolidColorBrush(fallback);

    private void Preview_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (Model is not { ListView: false } || sender is not Grid visual) return;
        var height = Math.Clamp(e.NewSize.Width * 9 / 16, 130, 220);
        if (Math.Abs(visual.Height - height) > 1) visual.Height = height;
    }

    private void Actions_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (Model is { } model) model.ShowLabels = e.NewSize.Width >= 430;
    }

    private void Remote_Click(object? sender, RoutedEventArgs e) => Model?.RequestRemote?.Invoke();
    private void Files_Click(object? sender, RoutedEventArgs e) => Model?.RequestFiles?.Invoke();
    private void Terminal_Click(object? sender, RoutedEventArgs e) => Model?.RequestTerminal?.Invoke();
    private void Favorite_Click(object? sender, RoutedEventArgs e) => Model?.RequestFavorite?.Invoke();
    private void Details_Click(object? sender, RoutedEventArgs e) => Model?.RequestDetails?.Invoke();
}
