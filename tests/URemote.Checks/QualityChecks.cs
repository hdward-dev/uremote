using System.Diagnostics;
using URemote.Media;
using SIPSorcery.Net;

internal static class QualityChecks
{
    public static async Task RunAsync(string ffmpeg, int fps = 60, int width = 1920, int height = 1080, int frameCount = 720)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var start = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate={fps}", "-frames:v", "1", "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1" }) start.ArgumentList.Add(arg);
        using var source = Process.Start(start)!;
        var errors = source.StandardError.ReadToEndAsync();
        using var raw = new MemoryStream();
        await source.StandardOutput.BaseStream.CopyToAsync(raw, timeout.Token);
        await source.WaitForExitAsync(timeout.Token); await errors;
        if (source.ExitCode != 0 || raw.Length != width * height * 4) throw new Exception("Synthetic source failed");
        var pixels = raw.ToArray();
        await using var encoder = new StreamingH264Encoder(ffmpeg, width, height, false, 0, fps, width, height, 5000000);
        var producer = Task.Run(async () =>
        {
            try { for (var n = 0; n < frameCount; n++) await encoder.WriteAsync(pixels, width * 4, timeout.Token); }
            finally { encoder.CompleteInput(); Array.Clear(pixels); }
        });
        var decodeStart = new ProcessStartInfo(ffmpeg) { RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-r", fps.ToString(), "-f", "h264", "-i", "pipe:0", "-f", "null", "-" }) decodeStart.ArgumentList.Add(arg);
        using var decoder = Process.Start(decodeStart)!;
        var decodeErrors = decoder.StandardError.ReadToEndAsync();
        int frames = 0, idrs = 0;
        await foreach (var unit in encoder.ReadAsync(timeout.Token))
        {
            var key = H264Packetiser.ParseNals(unit).Any(n => n.NAL.Length > 0 && (n.NAL[0] & 31) == 5);
            if (frames == 0 && !key) throw new Exception("New session must start with an IDR");
            if (key) idrs++;
            await decoder.StandardInput.BaseStream.WriteAsync(unit, timeout.Token);
            Array.Clear(unit); frames++;
        }
        await producer;
        decoder.StandardInput.Close(); await decoder.WaitForExitAsync(timeout.Token);
        if (frames != frameCount || idrs != 1) throw new Exception($"Expected {frameCount} frames and one starting IDR; got {frames}/{idrs}");
        var diagnostic = await decodeErrors;
        if (decoder.ExitCode != 0 || !string.IsNullOrWhiteSpace(diagnostic)) throw new Exception("Decoder rejected synthetic desktop stream: " + diagnostic);
        Console.WriteLine($"PASS: production encoder configured at {width}x{height}/{fps} FPS produces {frameCount} decodable frames with a starting IDR and no periodic quality resets");
    }
}
