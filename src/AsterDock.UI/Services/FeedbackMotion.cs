using AsterDock.UI.Motion;

namespace AsterDock.UI.Services;

public static class FeedbackMotion
{
    /// <summary>Gallery and tests set this so the first rendered frame is fully visible.</summary>
    public static bool Snap { get; set; }

    public static bool AnimationsEnabled => !Snap && !MotionPreferences.ReduceMotion;

    public static TimeSpan Duration => AnimationsEnabled ? TimeSpan.FromMilliseconds(140) : TimeSpan.Zero;
}
