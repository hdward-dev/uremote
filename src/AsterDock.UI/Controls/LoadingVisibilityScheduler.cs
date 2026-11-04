namespace AsterDock.UI.Controls;

internal sealed class LoadingVisibilityScheduler
{
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(150);
    public TimeSpan MinimumVisible { get; set; } = TimeSpan.FromMilliseconds(300);
    public bool IsRequested { get; private set; }
    public bool IsPresented { get; private set; }
    public DateTimeOffset? NextTransitionAt { get; private set; }

    private DateTimeOffset _presentedAt;

    public bool SetRequested(bool requested, DateTimeOffset now)
    {
        IsRequested = requested;
        if (requested)
        {
            if (IsPresented)
            {
                NextTransitionAt = null;
                return false;
            }

            if (Delay <= TimeSpan.Zero)
            {
                Present(now);
                return true;
            }

            NextTransitionAt = now + Delay;
            return false;
        }

        if (!IsPresented)
        {
            NextTransitionAt = null;
            return false;
        }

        var earliest = _presentedAt + MinimumVisible;
        if (now >= earliest || MinimumVisible <= TimeSpan.Zero)
        {
            IsPresented = false;
            NextTransitionAt = null;
            return true;
        }

        NextTransitionAt = earliest;
        return false;
    }

    public bool Advance(DateTimeOffset now)
    {
        if (NextTransitionAt is not { } due || now < due) return false;
        if (IsRequested && !IsPresented)
        {
            Present(now);
            return true;
        }

        if (!IsRequested && IsPresented)
        {
            IsPresented = false;
            NextTransitionAt = null;
            return true;
        }

        NextTransitionAt = null;
        return false;
    }

    private void Present(DateTimeOffset now)
    {
        IsPresented = true;
        _presentedAt = now;
        NextTransitionAt = null;
    }
}
