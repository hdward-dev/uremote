using AsterDock.Contracts;

namespace AsterDock.UI.Services;

public static class SystemNotificationIdentity
{
    public const string AppName = "星栈";
    public const string BundleId = "dev.hdward.asterdock";
    public const string DesktopEntry = "asterdock";
    public const string WindowsAppUserModelId = "dev.hdward.asterdock";
}

public sealed class SystemNotificationRequest
{
    public required string Token { get; init; }
    public string? ModuleId { get; init; }
    public FeedbackKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string? Details { get; init; }
    public bool OpenErrorDialog { get; init; }
}

public sealed class SystemNotificationActivation : EventArgs
{
    public required string Token { get; init; }
}

/// <summary>
/// Carries the original exception text into the in-app dialog opened from a system notification.
/// <see cref="Exception.ToString"/> is what the dialog shows.
/// </summary>
public sealed class NotificationDetailsException : Exception
{
    private readonly string _details;

    public NotificationDetailsException(string details) : base("系统通知")
    {
        _details = details ?? "";
    }

    public override string ToString() => _details;
}

public interface ISystemNotificationBackend
{
    bool IsAvailable { get; }
    bool TryPublish(SystemNotificationRequest request);
    event EventHandler<SystemNotificationActivation>? Activated;
}

public interface IFeedbackPresence
{
    bool AllowSystemNotifications { get; }
    bool IsWindowForeground(string? moduleId);
    bool IsPageVisible(string? moduleId);
}

public enum FeedbackRoute
{
    InAppImmediate,
    InAppDeferred,
    SystemOnly,
    BothImmediate,
    BothDeferred
}

public static class FeedbackRouter
{
    public static FeedbackRoute Decide(
        FeedbackDelivery delivery,
        bool hasPresence,
        bool windowForeground,
        bool pageVisible,
        bool systemAllowed)
    {
        if (!Enum.IsDefined(delivery)) throw new ArgumentOutOfRangeException(nameof(delivery));
        if (!hasPresence)
        {
            return delivery switch
            {
                FeedbackDelivery.System when systemAllowed => FeedbackRoute.SystemOnly,
                FeedbackDelivery.Both when systemAllowed => FeedbackRoute.BothImmediate,
                _ => FeedbackRoute.InAppImmediate
            };
        }

        var onScreen = windowForeground && pageVisible;
        return delivery switch
        {
            FeedbackDelivery.InApp => FeedbackRoute.InAppImmediate,
            FeedbackDelivery.System => systemAllowed
                ? FeedbackRoute.SystemOnly
                : pageVisible ? FeedbackRoute.InAppImmediate : FeedbackRoute.InAppDeferred,
            FeedbackDelivery.Both => systemAllowed
                ? pageVisible ? FeedbackRoute.BothImmediate : FeedbackRoute.BothDeferred
                : pageVisible ? FeedbackRoute.InAppImmediate : FeedbackRoute.InAppDeferred,
            _ => onScreen
                ? FeedbackRoute.InAppImmediate
                : systemAllowed
                    ? FeedbackRoute.SystemOnly
                    : pageVisible ? FeedbackRoute.InAppImmediate : FeedbackRoute.InAppDeferred
        };
    }
}

public sealed class FreedesktopNotifyMessage
{
    public const string Service = "org.freedesktop.Notifications";
    public const string Path = "/org/freedesktop/Notifications";
    public const string Interface = "org.freedesktop.Notifications";
    public const string Signature = "susssasa{sv}i";

    public string AppName { get; init; } = SystemNotificationIdentity.AppName;
    public uint ReplacesId { get; init; }
    public string AppIcon { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Body { get; init; } = "";
    public IReadOnlyList<string> Actions { get; init; } = ["default", "打开"];
    public byte Urgency { get; init; }
    public string DesktopEntry { get; init; } = SystemNotificationIdentity.DesktopEntry;
    public string? ImagePath { get; init; }
    public int ExpireTimeout { get; init; }

    public static FreedesktopNotifyMessage Create(SystemNotificationRequest request, string? iconPath)
    {
        var title = string.IsNullOrWhiteSpace(request.Title) ? request.Body : request.Title.Trim();
        var body = string.IsNullOrWhiteSpace(request.Title) ? "" : request.Body.Trim();
        return new FreedesktopNotifyMessage
        {
            AppIcon = iconPath ?? "",
            Summary = title,
            Body = body,
            Urgency = UrgencyFor(request.Kind),
            ImagePath = string.IsNullOrWhiteSpace(iconPath) ? null : iconPath,
            ExpireTimeout = ExpireFor(request.Kind)
        };
    }

    public static byte UrgencyFor(FeedbackKind kind) => kind switch
    {
        FeedbackKind.Info => 0,
        FeedbackKind.Error => 2,
        _ => 1
    };

    public static int ExpireFor(FeedbackKind kind) => kind switch
    {
        FeedbackKind.Error => 0,
        FeedbackKind.Warning => 8000,
        FeedbackKind.Success => 4000,
        _ => 5000
    };
}

public static class WindowsToastContent
{
    public static string Create(SystemNotificationRequest request, string? iconPath)
    {
        var title = Xml(string.IsNullOrWhiteSpace(request.Title) ? request.Body : request.Title.Trim());
        var body = Xml(string.IsNullOrWhiteSpace(request.Title) ? "" : request.Body.Trim());
        var duration = request.Kind == FeedbackKind.Error ? " duration=\"long\"" : "";
        var audio = request.Kind switch
        {
            FeedbackKind.Error => "<audio src=\"ms-winsoundevent:Notification.Reminder\"/>",
            FeedbackKind.Info => "<audio silent=\"true\"/>",
            _ => "<audio src=\"ms-winsoundevent:Notification.Default\"/>"
        };
        var image = string.IsNullOrWhiteSpace(iconPath)
            ? ""
            : $"<image placement=\"appLogoOverride\" src=\"{Xml(new Uri(iconPath).AbsoluteUri)}\"/>";
        return $"""
            <toast launch="token={Xml(request.Token)}" activationType="foreground"{duration}>
              <visual>
                <binding template="ToastGeneric">
                  {image}
                  <text>{title}</text>
                  <text>{body}</text>
                </binding>
              </visual>
              {audio}
            </toast>
            """;
    }

    private static string Xml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
