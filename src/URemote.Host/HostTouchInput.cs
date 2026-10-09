using URemote.Core;
using URemote.Linux;

namespace URemote.Host;

internal static class HostTouchInput
{
    internal static async Task<IDesktopTouch?> OpenAsync(bool enableInput, Action<string> report, CancellationToken ct)
    {
        // Opt in while validating compositor compatibility. Never fall back to
        // private KDE calls or advertise touch when authorization/setup failed.
        if (!enableInput || Environment.GetEnvironmentVariable("UREMOTE_NATIVE_TOUCH") != "1") return null;
        try
        {
            var touch = await DesktopBackend.CreateTouchAsync(ct);
            report(touch?.Available == true ? "touch-backend=eis" : "touch-backend=unavailable");
            return touch;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is IOException or NotSupportedException or InvalidOperationException or OperationCanceledException or DllNotFoundException or EntryPointNotFoundException or FormatException)
        { report("touch-backend-unavailable=" + e.GetType().Name); return null; }
    }
    internal static HostTouchEvent? Map(HostTouchEvent input, uint width, uint height, int encodedWidth, int encodedHeight)
    {
        if (input.Phase is 0 or 3 or 4) return input;
        var scale = Math.Min((double)encodedWidth / width, (double)encodedHeight / height);
        var w = Math.Floor(width * scale / 2) * 2; var h = Math.Floor(height * scale / 2) * 2;
        if (w <= 0 || h <= 0) return null;
        var points = new List<HostTouchPoint>();
        foreach (var point in input.Points)
        {
            var x = (point.X * encodedWidth - (encodedWidth - w) / 2) / w;
            var y = (point.Y * encodedHeight - (encodedHeight - h) / 2) / h;
            if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x > 1 || y < 0 || y > 1) return null;
            points.Add(point with { X = x, Y = y });
        }
        return input with { Points = points };
    }
}
