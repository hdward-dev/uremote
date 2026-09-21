namespace AsterDock.Contracts;

public sealed record ApplicationQuickAction(string Id, string DisplayName, Action Execute);

public interface IApplicationQuickActionProvider
{
    IReadOnlyList<ApplicationQuickAction> GetQuickActions();
}
