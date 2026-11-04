namespace AsterDock.UI.Motion;

/// <summary>Material emphasized curve, cubic-bezier(0.2, 0, 0, 1).</summary>
internal static class EmphasizedEasing
{
    public static double Ease(double progress)
    {
        if (progress <= 0) return 0;
        if (progress >= 1) return 1;
        var t = progress;
        for (var i = 0; i < 8; i++)
        {
            var x = Sample(t, 0.2, 0);
            var dx = Deriv(t, 0.2, 0);
            if (Math.Abs(dx) < 1e-6) break;
            t = Math.Clamp(t - (x - progress) / dx, 0, 1);
        }

        return Sample(t, 0, 1);
    }

    private static double Sample(double t, double c1, double c2)
    {
        var u = 1 - t;
        return (3 * u * u * t * c1) + (3 * u * t * t * c2) + (t * t * t);
    }

    private static double Deriv(double t, double c1, double c2)
    {
        var u = 1 - t;
        return (3 * u * u * c1) + (6 * u * t * (c2 - c1)) + (3 * t * t * (1 - c2));
    }
}
