namespace AsterDock.Contracts;

public sealed record ApplicationSummary(
    string Id,
    string Name,
    string Description,
    string Version,
    string Icon,
    string Category);

public sealed record RecentApplication(
    ApplicationSummary Application,
    DateTimeOffset LastOpenedAt);

public interface IApplicationShell
{
    IReadOnlyList<ApplicationSummary> Applications { get; }
    IReadOnlyList<RecentApplication> RecentApplications { get; }
    event EventHandler? StateChanged;

    void OpenApplication(string applicationId);
    void ShowSettings();
    void ShowApplicationSwitcher();
    bool TryExecuteApplicationAction(string applicationId, string actionId);
}
