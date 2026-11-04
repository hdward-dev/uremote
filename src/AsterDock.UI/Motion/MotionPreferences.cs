namespace AsterDock.UI.Motion;

public static class MotionPreferences
{
    private static bool _reduceMotion;

    public static bool ReduceMotion
    {
        get => _reduceMotion;
        set
        {
            if (_reduceMotion == value) return;
            _reduceMotion = value;
            ReduceMotionChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static event EventHandler? ReduceMotionChanged;
}
