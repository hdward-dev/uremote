using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using URemote.Linux;

namespace URemote.Module;

public sealed partial class URemoteView
{
    private Control BuildAccountPage()
    {
        ApplyTheme(accountHeading, TextBlock.ForegroundProperty, "AppStrongTextBrush");
        ApplyTheme(accountIdentity, SelectableTextBlock.ForegroundProperty, "AppStrongTextBrush");
        ApplyTheme(accountDescription, TextBlock.ForegroundProperty, "AppMutedBrush");
        ApplyTheme(logoutHint, TextBlock.ForegroundProperty, "AppMutedBrush");
        accountIdentity.FontSize = 20; accountIdentity.FontWeight = FontWeight.SemiBold;
        accountState.FontSize = 12;
        logout.VerticalAlignment = VerticalAlignment.Top;
        logout.Padding = new Thickness(14, 8);

        var person = new PathIcon { Width = 22, Height = 22,
            Data = Geometry.Parse("M12,2 A5,5 0 1 1 12,12 A5,5 0 1 1 12,2 M3,22 C3,12 21,12 21,22 Z") };
        ApplyTheme(person, PathIcon.ForegroundProperty, "AppAccentBrush");
        var avatar = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(24), Child = person,
            VerticalAlignment = VerticalAlignment.Top };
        ApplyTheme(avatar, Border.BackgroundProperty, "AppAccentSurfaceBrush");
        var heading = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 14 };
        heading.Children.Add(avatar);
        var labels = new StackPanel { Spacing = 5, Children = { accountHeading, accountIdentity, accountState } };
        Grid.SetColumn(labels, 1); heading.Children.Add(labels);
        Grid.SetColumn(logout, 2); heading.Children.Add(logout);

        var accountCard = Card(new StackPanel { Spacing = 14, Children = {
            heading, accountDescription, loginPanel, logoutHint } }, 20);
        var capabilityRows = new StackPanel { Spacing = 12, Children = {
            Text("功能状态", 16),
            AccountInfoRow("桌面环境", DesktopEnvironmentInfo.Current.DisplayName),
            new Separator(),
            AccountInfoRow("远程控制", "画面共享、键鼠控制、远程终端与文件传输"),
            AccountInfoRow("兼容性验证", "系统声音、剪贴板及不同桌面环境仍在持续验证") } };
        var advanced = new Expander { Header = "高级设置", HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 14, Margin = new Thickness(0, 14, 0, 0), Children = { advancedSettings } } };
        var content = new StackPanel { Spacing = 16, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { new StackPanel { Spacing = 6, Children = {
                Text("账号与设置", 26), Text("管理登录账号，查看运行环境与应用配置。", 13, true) } },
                accountCard, Card(capabilityRows, 20), Card(advanced, 16) } };
        content.SizeChanged += (_, e) =>
        {
            var compact = e.NewSize.Width < 440;
            heading.ColumnDefinitions = new(compact ? "Auto,*" : "Auto,*,Auto");
            heading.RowDefinitions = new(compact ? "Auto,Auto" : "Auto");
            Grid.SetColumn(logout, compact ? 1 : 2); Grid.SetRow(logout, compact ? 1 : 0);
            logout.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            logout.Margin = new Thickness(0, compact ? 12 : 0, 0, 0);
        };
        return content;
    }

    private static Control AccountInfoRow(string title, string description)
    {
        var row = new StackPanel { Spacing = 5 };
        row.Children.Add(Text(title, 13)); row.Children.Add(Text(description, 12, true));
        return row;
    }
}
