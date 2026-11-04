namespace AsterDock.UI.Services;

public static class SystemNotificationBackends
{
    public static ISystemNotificationBackend Create(Func<string?>? iconPath = null)
    {
        if (OperatingSystem.IsLinux()) return new FreedesktopNotificationBackend(iconPath: iconPath);
        if (OperatingSystem.IsWindows()) return new WindowsToastBackend(iconPath);
        if (OperatingSystem.IsMacOS()) return new MacNotificationBackend();
        return new UnavailableNotificationBackend();
    }

    private sealed class UnavailableNotificationBackend : ISystemNotificationBackend
    {
        public bool IsAvailable => false;
        public event EventHandler<SystemNotificationActivation>? Activated { add { } remove { } }
        public bool TryPublish(SystemNotificationRequest request) => false;
    }
}
