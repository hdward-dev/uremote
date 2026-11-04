using AsterDock.Contracts;
using AsterDock.UI.Services;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AsterDock.UI.Controls;

public class AdToastHost : StackPanel
{
    private UserFeedbackService? _service;

    public AdToastHost()
    {
        Spacing = 8;
        Width = 360;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
    }

    public void Show(UserFeedbackService service)
    {
        _service = service;
        Children.Clear();
        foreach (var toast in service.VisibleToasts)
            Children.Add(Create(toast));
        IsVisible = Children.Count > 0;
        Opacity = FeedbackMotion.AnimationsEnabled ? 0.001 : 1;
        if (FeedbackMotion.AnimationsEnabled && Children.Count > 0)
        {
            var started = DateTime.UtcNow;
            var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                var progress = (DateTime.UtcNow - started).TotalMilliseconds / FeedbackMotion.Duration.TotalMilliseconds;
                Opacity = progress >= 1 ? 1 : Math.Max(0.001, progress);
                if (progress >= 1) timer.Stop();
            };
            timer.Start();
        }
        else
        {
            Opacity = 1;
        }
    }

    public void ClearToasts()
    {
        Children.Clear();
        IsVisible = false;
    }

    private Control Create(FeedbackToast toast)
    {
        var tone = Brush(BarKey(toast.Kind));
        var title = new TextBlock
        {
            Text = toast.Title ?? UserFeedbackService.KindLabel(toast.Kind),
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("AppStrongTextBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        var message = new TextBlock
        {
            Name = "ToastMessage",
            Text = toast.Message,
            FontSize = 13,
            Foreground = Brush("AppStrongTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 28, 0)
        };
        var column = new StackPanel { Children = { title, message } };
        if (toast.RepeatCount > 1)
        {
            column.Children.Add(new TextBlock
            {
                Name = "ToastRepeat",
                Text = "重复 " + toast.RepeatCount + " 次",
                FontSize = 12,
                Foreground = Brush("AppMutedBrush"),
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        if (toast.ActionText is not null)
        {
            var action = new Button
            {
                Name = "ToastAction",
                Content = toast.ActionText,
                Classes = { "ad-dialog-quiet" },
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0)
            };
            AutomationProperties.SetName(action, toast.ActionText);
            var id = toast.Id;
            action.Click += (_, _) => _service?.InvokeToastAction(id);
            column.Children.Add(action);
        }

        var close = new Button
        {
            Name = "ToastClose",
            Content = "×",
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            Classes = { "ad-dialog-quiet" },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };
        AutomationProperties.SetName(close, "关闭通知");
        var closeId = toast.Id;
        close.Click += (_, _) => _service?.DismissToast(closeId);

        var card = new Border
        {
            Name = "ToastItem",
            Tag = toast.Id,
            Background = Brush("AppSurfaceBrush"),
            BorderBrush = Brush("AppBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 10, 8, 12),
            BoxShadow = BoxShadows.Parse("0 8 18 0 #22101820"),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("4,*,Auto"),
                Children =
                {
                    new Border
                    {
                        Width = 4,
                        CornerRadius = new CornerRadius(2),
                        Background = tone,
                        Margin = new Thickness(0, 2, 10, 2)
                    },
                    column,
                    close
                }
            }
        };
        Grid.SetColumn(column, 1);
        Grid.SetColumn(close, 2);
        AutomationProperties.SetName(card, UserFeedbackService.KindLabel(toast.Kind) + " " + toast.Message);
        return card;
    }

    private static string BarKey(FeedbackKind kind) => kind switch
    {
        FeedbackKind.Success => "AppPositiveBrush",
        FeedbackKind.Warning => "AppWarningBrush",
        FeedbackKind.Error => "AppDangerBrush",
        _ => "AppAccentBrush"
    };

    private IBrush Brush(string key)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush) return brush;
        return new SolidColorBrush(Colors.Gray);
    }
}
