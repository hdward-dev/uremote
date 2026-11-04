namespace AsterDock.UI.Controls;

internal static class LoadingShapes
{
    public const int Samples = 192;
    public const double MorphDurationMs = 650;
    public const double RotationPeriodSeconds = 2;
    public const double CookieAmplitude = 0.07;

    private static readonly float[] CookieRadii = Sample(Cookie);
    private static readonly float[] CloverRadii = Sample(Clover);
    private static readonly float[] GemRadii = Sample(PillGem);
    private static readonly float[] CircleRadii = Sample(_ => 1);

    public static IReadOnlyList<float[]> Frames { get; } = [CookieRadii, CloverRadii, GemRadii, CircleRadii];

    public static void Interpolate(TimeSpan elapsed, bool reduceMotion, float[] destination)
    {
        if (reduceMotion)
        {
            Array.Copy(CircleRadii, destination, Samples);
            return;
        }

        var cycle = Frames.Count * MorphDurationMs;
        var position = elapsed.TotalMilliseconds % cycle;
        if (position < 0) position += cycle;
        var index = (int)(position / MorphDurationMs);
        var local = (position - (index * MorphDurationMs)) / MorphDurationMs;
        var eased = Motion.EmphasizedEasing.Ease(local);
        var from = Frames[index];
        var to = Frames[(index + 1) % Frames.Count];
        var blend = (float)eased;
        for (var i = 0; i < Samples; i++)
            destination[i] = from[i] + ((to[i] - from[i]) * blend);
    }

    public static double Rotation(TimeSpan elapsed, bool reduceMotion)
        => reduceMotion ? 0 : elapsed.TotalSeconds / RotationPeriodSeconds * Math.Tau;

    public static double PulseOpacity(TimeSpan elapsed)
    {
        var wave = 0.5 - (0.5 * Math.Cos(elapsed.TotalSeconds / 1.6 * Math.Tau));
        return 0.58 + (0.42 * wave);
    }

    private static float[] Sample(Func<double, double> radius)
    {
        var values = new float[Samples];
        var max = 0d;
        for (var i = 0; i < Samples; i++)
        {
            var theta = i * Math.Tau / Samples;
            values[i] = (float)Math.Max(0.05, radius(theta));
            if (values[i] > max) max = values[i];
        }

        if (max > 0)
        {
            for (var i = 0; i < Samples; i++) values[i] = (float)(values[i] / max);
        }

        return values;
    }

    private static double Cookie(double theta) => 1 + (CookieAmplitude * Math.Cos(12 * theta));

    private static double Clover(double theta)
    {
        var lobe = 0.5 + (0.5 * Math.Cos(4 * theta));
        return 0.74 + (0.26 * Math.Pow(lobe, 1.45));
    }

    private static double PillGem(double theta)
    {
        var lobe = 0.5 + (0.5 * Math.Cos((5 * theta) + (Math.PI / 2)));
        return 0.82 + (0.18 * Math.Pow(lobe, 1.5));
    }
}
