using URemote.Media;

static class DecoderPipeChecks
{
    public static async Task RunAsync()
    {
        var executable = Environment.GetEnvironmentVariable("UREMOTE_FFMPEG")
            ?? throw new Exception("Set UREMOTE_FFMPEG to the FFmpeg executable.");
        var pixels = new byte[64 * 64 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = 220; pixels[i + 3] = 255; }
        var h264 = await new FfmpegH264Encoder(executable).EncodeAsync(pixels, 64, 64, 256, false, 1);
        await using var decoder = new ControllerVideoDecoder(executable);
        var frames = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        decoder.Failed += () => ready.TrySetException(new Exception("Pipe decoder failed."));
        decoder.Frame += frame =>
        {
            if (frame.Width != 1280 || frame.Height != 720 || frame.Pixels.Length != 1280 * 720 * 4)
                ready.TrySetException(new Exception("Invalid RGBA frame."));
            else if (Interlocked.Increment(ref frames) >= 3) ready.TrySetResult();
        };
        // Deliberately split NAL headers and keep stdin open: live remote video
        // must render before EOF, including multiple consecutive frames.
        for (var n = 0; n < 30 && !ready.Task.IsCompleted; n++)
        {
            foreach (var part in new[] { h264[..2], h264[2..7], h264[7..] }) decoder.Push(part);
            await Task.Delay(50);
        }
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine("PASS: fragmented H264 pipe renders multiple RGBA frames without EOF");
    }
}
