using System.Collections.Concurrent;
using System.Diagnostics;
using URemote.Media;

internal static class EncoderLatencyChecks
{
    internal static async Task RunAsync(string ffmpeg, bool throughput = false)
    {
        foreach (var chroma in new[] { 1, 3 })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            const int width = 3840, height = 2160, count = 150, bitrate = 8000000;
            var fps = throughput ? 60 : 30;
            var pixels = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    pixels[i] = (byte)(x / 4); pixels[i + 1] = (byte)(y / 4); pixels[i + 2] = (byte)(x ^ y); pixels[i + 3] = 255;
                }
            await using var encoder = await StreamingH264Encoder.CreateAsync(ffmpeg, width, height, false, 0,
                fps, width, height, bitrate, stopToken: timeout.Token, chromaFormat: chroma);
            var inputs = new ConcurrentQueue<long>();
            var ages = new List<double>();
            var pacer = new VideoPacketPacer(Stopwatch.Frequency);
            long firstOutput = 0;
            var written = 0; var consumed = 0; var maxPending = 0;
            var producer = Task.Run(async () =>
            {
                try
                {
                    for (var n = 0; n < count; n++)
                    {
                        await encoder.WaitForCapacityAsync(timeout.Token);
                        pixels[n * 4] = (byte)n;
                        inputs.Enqueue(Stopwatch.GetTimestamp());
                        await encoder.WriteAsync(pixels, width * 4, timeout.Token);
                        var pending = Interlocked.Increment(ref written) - Volatile.Read(ref consumed);
                        maxPending = Math.Max(maxPending, pending);
                        if (!throughput) await Task.Delay(TimeSpan.FromSeconds(1d / fps), timeout.Token);
                    }
                }
                finally { encoder.CompleteInput(); Array.Clear(pixels); }
            });
            await foreach (var unit in encoder.ReadAsync(timeout.Token))
            {
                try
                {
                    if (!inputs.TryDequeue(out var started)) throw new Exception("Encoded frame has no source.");
                    ages.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    if (firstOutput == 0) firstOutput = Stopwatch.GetTimestamp();
                    // Include a short real-world send stall. The pipeline must not
                    // retain an unbounded queue of old raw or encoded desktops.
                    if (!throughput)
                    {
                        if (consumed == 45) await Task.Delay(350, timeout.Token);
                        foreach (var packet in H264RtpPayloads.Create(unit))
                        {
                            try
                            {
                                var wait = pacer.Reserve(packet.Bytes.Length + 64, bitrate, Stopwatch.GetTimestamp(), onLink: true);
                                if (wait.TotalMilliseconds >= 1) await Task.Delay(wait, timeout.Token);
                            }
                            finally { Array.Clear(packet.Bytes); }
                        }
                    }
                    Interlocked.Increment(ref consumed);
                }
                finally { Array.Clear(unit); }
            }
            await producer;
            if (ages.Count != count) throw new Exception("Latency check lost frames.");
            if (maxPending > encoder.MaxInFlightFrames) throw new Exception("Encoder exceeded the configured in-flight frame limit.");
            var sorted = ages.Order().ToArray();
            Console.WriteLine($"LATENCY chroma={chroma} frames={ages.Count} pending-max={maxPending} pipe={encoder.InputPipeBytes} first-ms={ages[0]:F1} p50-ms={sorted[sorted.Length / 2]:F1} p95-ms={sorted[(int)(sorted.Length * .95)]:F1} max-ms={sorted[^1]:F1} steady-fps={(count - 1) / Stopwatch.GetElapsedTime(firstOutput).TotalSeconds:F1}");
        }
    }
}
