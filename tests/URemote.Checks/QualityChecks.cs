using System.Diagnostics;
using URemote.Media;
using SIPSorcery.Net;

internal static class QualityChecks
{
    public static async Task RunAsync(string ffmpeg, int fps = 60, int width = 1920, int height = 1080, int frameCount = 720, bool requireNvenc = false, int outputWidth = 0, int outputHeight = 0, int chromaFormat = 1)
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
        if (outputWidth <= 0) outputWidth = width;
        if (outputHeight <= 0) outputHeight = height;
        await using var encoder = await StreamingH264Encoder.CreateAsync(ffmpeg, width, height, false, 0, fps, outputWidth, outputHeight, 5000000, stopToken: timeout.Token, chromaFormat: chromaFormat);
        if (requireNvenc && encoder.Codec != "h264_nvenc") throw new Exception("NVENC was required but hardware selection fell back to software.");
        var producer = Task.Run(async () =>
        {
            try { for (var n = 0; n < frameCount; n++) await encoder.WriteAsync(pixels, width * 4, timeout.Token); }
            finally { encoder.CompleteInput(); Array.Clear(pixels); }
        });
        var decodeStart = new ProcessStartInfo(ffmpeg) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-r", fps.ToString(), "-f", "h264", "-i", "pipe:0", "-progress", "pipe:1", "-f", "null", "-" }) decodeStart.ArgumentList.Add(arg);
        using var decoder = Process.Start(decodeStart)!;
        var decodeErrors = decoder.StandardError.ReadToEndAsync();
        var decodeProgress = decoder.StandardOutput.ReadToEndAsync(timeout.Token);
        int frames = 0, idrs = 0;
        using var encodedSample = new MemoryStream();
        await foreach (var unit in encoder.ReadAsync(timeout.Token))
        {
            var key = H264Packetiser.ParseNals(unit).Any(n => n.NAL.Length > 0 && (n.NAL[0] & 31) == 5);
            if (frames == 0 && !key) throw new Exception("New session must start with an IDR");
            if (frames == 0) encodedSample.Write(unit);
            if (key) idrs++;
            await decoder.StandardInput.BaseStream.WriteAsync(unit, timeout.Token);
            Array.Clear(unit); frames++;
        }
        await producer;
        decoder.StandardInput.Close(); await decoder.WaitForExitAsync(timeout.Token);
        if (frames != frameCount || idrs != 1) throw new Exception($"Expected {frameCount} frames and one starting IDR; got {frames}/{idrs}");
        var diagnostic = await decodeErrors;
        if (decoder.ExitCode != 0 || !string.IsNullOrWhiteSpace(diagnostic)) throw new Exception("Decoder rejected synthetic desktop stream: " + diagnostic);
        var decodedFrames = (await decodeProgress).Split('\n').LastOrDefault(line => line.StartsWith("frame=", StringComparison.Ordinal));
        if (decodedFrames is null || !int.TryParse(decodedFrames[6..].Trim(), out var decoded) || decoded != frameCount)
            throw new Exception($"Decoder did not output all {frameCount} frames: {decodedFrames}");
        var probeStart = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe"))
            { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-f", "h264", "-i", "pipe:0", "-show_entries", "stream=pix_fmt,profile,width,height", "-of", "json" }) probeStart.ArgumentList.Add(arg);
        using var probe = Process.Start(probeStart)!;
        var probeOutput = probe.StandardOutput.ReadToEndAsync(timeout.Token);
        var probeErrors = probe.StandardError.ReadToEndAsync(timeout.Token);
        await probe.StandardInput.BaseStream.WriteAsync(encodedSample.ToArray(), timeout.Token);
        probe.StandardInput.Close(); await probe.WaitForExitAsync(timeout.Token);
        using var metadata = System.Text.Json.JsonDocument.Parse(await probeOutput);
        var stream = metadata.RootElement.GetProperty("streams")[0];
        if (probe.ExitCode != 0 || !string.IsNullOrWhiteSpace(await probeErrors)
            || stream.GetProperty("pix_fmt").GetString() != (chromaFormat == 3 ? "yuv444p" : "yuv420p")
            || stream.GetProperty("width").GetInt32() != outputWidth || stream.GetProperty("height").GetInt32() != outputHeight)
            throw new Exception("Actual encoded SPS does not match the advertised chroma and size.");
        Console.WriteLine($"PASS: production {encoder.Codec} encoder configured at {width}x{height}->{outputWidth}x{outputHeight}/{fps} FPS, chroma={chromaFormat}, produces {frameCount} decodable frames with verified SPS and one starting IDR");
    }
}
