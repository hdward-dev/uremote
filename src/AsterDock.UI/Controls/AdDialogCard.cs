using AsterDock.UI.Services;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ursa.Helpers;

namespace AsterDock.UI.Controls;

public class AdDialogCard : Border
{
    private readonly Button _primary;
    private readonly Button? _secondary;
    private readonly Button? _detailsToggle;
    private readonly SelectableTextBlock? _details;
    private readonly Button? _copy;
    private readonly Border _icon;
    private readonly ShapePath _iconPath;
    private readonly TextBlock _kind;
    private readonly TextBlock _title;
    private readonly TextBlock _message;
    private DispatcherTimer? _motion;
    private bool _copied;
    private readonly DialogTone _tone;

    public AdDialogCard(FeedbackDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        Dialog = dialog;
        Focusable = true;
        IsTabStop = false;
        Width = 440;
        MaxWidth = 480;
        Padding = new Thickness(22);
        CornerRadius = new CornerRadius(16);
        BorderThickness = new Thickness(1);
        Background = Brush("AppSurfaceBrush");
        BorderBrush = Brush("AppBorderBrush");
        BoxShadow = BoxShadows.Parse("0 12 28 0 #33101820");
        AutomationProperties.SetName(this, UserFeedbackService.ToneLabel(dialog.Tone) + " " + dialog.Title);

        _tone = dialog.Danger ? DialogTone.Error : dialog.Tone;
        _iconPath = new ShapePath
        {
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Data = Geometry.Parse(IconData(_tone)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _icon = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Child = _iconPath
        };
        _kind = new TextBlock { Text = UserFeedbackService.ToneLabel(dialog.Tone), FontSize = 12 };
        _title = new TextBlock
        {
            Text = dialog.Title,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };
        _message = new TextBlock
        {
            Name = "DialogMessage",
            Text = dialog.Message,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0)
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                _icon,
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 2,
                    Children = { _kind, _title }
                }
            }
        };

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(_message);

        if (dialog.Details is not null)
        {
            _detailsToggle = new Button
            {
                Name = "DialogDetailsToggle",
                Content = "错误详情",
                Classes = { "ad-dialog-quiet" },
                HorizontalAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(_detailsToggle, "错误详情");
            _copy = new Button
            {
                Name = "DialogCopy",
                Content = "复制",
                Classes = { "ad-dialog-quiet" }
            };
            AutomationProperties.SetName(_copy, "复制错误详情");
            _details = new SelectableTextBlock
            {
                Name = "DialogDetails",
                Text = dialog.Details,
                IsVisible = false,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray
            };
            var detailsScroll = new ScrollViewer
            {
                Name = "DialogDetailsScroll",
                MaxHeight = 160,
                IsVisible = false,
                Margin = new Thickness(0, 8, 0, 0),
                Content = _details
            };
            _detailsToggle.Click += (_, _) =>
            {
                var open = !detailsScroll.IsVisible;
                detailsScroll.IsVisible = open;
                _details.IsVisible = open;
                _detailsToggle.Content = open ? "收起详情" : "错误详情";
                AutomationProperties.SetName(_detailsToggle, open ? "收起错误详情" : "错误详情");
            };
            _copy.Click += (_, _) => _ = CopyAsync(dialog.Details);
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 12, 0, 0),
                Children = { _detailsToggle, _copy }
            };
            body.Children.Add(row);
            body.Children.Add(detailsScroll);
        }

        _primary = new Button
        {
            Name = "DialogPrimary",
            Content = dialog.PrimaryText,
            Classes = { dialog.Danger ? "ad-dialog-danger" : "ad-dialog-primary" },
            MinWidth = 88
        };
        AutomationProperties.SetName(_primary, dialog.PrimaryText);
        FocusHelper.SetDialogFocusHint(_primary, true);
        _primary.Click += (_, _) => Accepted?.Invoke(this, EventArgs.Empty);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        if (dialog.SecondaryText is not null)
        {
            _secondary = new Button
            {
                Name = "DialogSecondary",
                Content = dialog.SecondaryText,
                Classes = { "ad-dialog-secondary" },
                MinWidth = 88
            };
            AutomationProperties.SetName(_secondary, dialog.SecondaryText);
            _secondary.Click += (_, _) => Cancelled?.Invoke(this, EventArgs.Empty);
            buttons.Children.Add(_secondary);
        }

        buttons.Children.Add(_primary);
        body.Children.Add(buttons);
        Child = body;
        AddHandler(KeyDownEvent, OnPreviewKey, RoutingStrategies.Tunnel);
        ApplyTheme();
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
        Opacity = FeedbackMotion.AnimationsEnabled ? 0 : 1;
    }

    public FeedbackDialog Dialog { get; }

    public event EventHandler? Accepted;

    public event EventHandler? Cancelled;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyTheme();
        Dispatcher.UIThread.Post(() => _primary.Focus(), DispatcherPriority.Input);
        StartMotion();
    }

    private void ApplyTheme()
    {
        Background = Brush("AppSurfaceBrush");
        BorderBrush = Brush("AppBorderBrush");
        _icon.Background = Brush(SurfaceKey(_tone));
        _iconPath.Stroke = Brush(ForegroundKey(_tone));
        _kind.Foreground = Brush(ForegroundKey(_tone));
        _title.Foreground = Brush("AppStrongTextBrush");
        _message.Foreground = Brush("AppStrongTextBrush");
        if (_details is not null) _details.Foreground = Brush("AppMutedBrush");
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _motion?.Stop();
        _motion = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPreviewKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Cancelled?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (e.Key == Key.Enter)
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
            if (ReferenceEquals(focused, _secondary))
            {
                e.Handled = true;
                Cancelled?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (focused is Button && !ReferenceEquals(focused, _primary)) return;
            e.Handled = true;
            Accepted?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (e.Key != Key.Tab) return;
        var items = Focusables();
        if (items.Count == 0) return;
        var current = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        var index = current is null ? -1 : items.IndexOf(current);
        var backward = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var next = index < 0
            ? (backward ? items[^1] : items[0])
            : items[(index + (backward ? -1 : 1) + items.Count) % items.Count];
        next.Focus();
        e.Handled = true;
    }

    private List<Control> Focusables() =>
        this.GetVisualDescendants()
            .OfType<Control>()
            .Where(item => item.Focusable && item.IsVisible && item.IsEffectivelyEnabled && item.IsTabStop)
            .ToList();

    private async Task CopyAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || _copy is null) return;
        try
        {
            await clipboard.SetTextAsync(text);
            if (_copied) return;
            _copied = true;
            _copy.Content = "已复制";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _copy.Content = "复制失败";
        }
    }

    private void StartMotion()
    {
        if (!FeedbackMotion.AnimationsEnabled)
        {
            Opacity = 1;
            return;
        }

        var started = DateTime.UtcNow;
        _motion = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _motion.Tick += (_, _) =>
        {
            var progress = (DateTime.UtcNow - started).TotalMilliseconds / FeedbackMotion.Duration.TotalMilliseconds;
            if (progress >= 1)
            {
                Opacity = 1;
                _motion?.Stop();
                return;
            }

            Opacity = progress;
        };
        _motion.Start();
    }

    private static string ForegroundKey(DialogTone tone) => tone switch
    {
        DialogTone.Success => "AppPositiveBrush",
        DialogTone.Warning => "AppWarningBrush",
        DialogTone.Error => "AppDangerBrush",
        _ => "AppAccentBrush"
    };

    private static string SurfaceKey(DialogTone tone) => tone switch
    {
        DialogTone.Success => "AppPositiveSurfaceBrush",
        DialogTone.Warning => "AppWarningSurfaceBrush",
        DialogTone.Error => "AppDangerSurfaceBrush",
        _ => "AppAccentSurfaceBrush"
    };

    private static string IconData(DialogTone tone) => tone switch
    {
        DialogTone.Success => "M4,12 L9,17 L20,6",
        DialogTone.Warning => "M12,4 L21,20 H3 Z M12,9 V13 M12,16 V16.2",
        DialogTone.Error => "M7,7 L17,17 M17,7 L7,17",
        DialogTone.Confirm => "M12,16 V16.2 M12,7 A3,3 0 0 1 12,13",
        _ => "M12,7 V7.2 M12,11 V17"
    };

    private IBrush Brush(string key)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush) return brush;
        return key switch
        {
            "AppSurfaceBrush" => new SolidColorBrush(Color.Parse("#FFFFFF")),
            "AppBorderBrush" => new SolidColorBrush(Color.Parse("#E2E7EF")),
            "AppMutedBrush" => new SolidColorBrush(Color.Parse("#667085")),
            "AppDangerBrush" => new SolidColorBrush(Color.Parse("#C42B1C")),
            "AppWarningBrush" => new SolidColorBrush(Color.Parse("#B54708")),
            "AppPositiveBrush" => new SolidColorBrush(Color.Parse("#16883F")),
            _ => new SolidColorBrush(Color.Parse("#1267E8"))
        };
    }
}
