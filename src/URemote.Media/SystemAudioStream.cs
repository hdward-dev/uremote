using System.Diagnostics;
using System.Buffers.Binary;
using Concentus;
using Concentus.Enums;
namespace URemote.Media;

public static class SystemAudioStream
{
    // Capture only the default output monitor, never the default microphone.
    public static async Task RunAsync(HostMediaPeer peer, string pwCat, Action<string> report, CancellationToken ct)
    {
        var start = new ProcessStartInfo(pwCat) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--record", "--raw", "--rate", "48000", "--channels", "2", "--format", "s16", "--latency", "20ms",
            "--properties", "{ stream.capture.sink=true node.name=uremote-system-audio media.name=U-Remote-System-Audio }", "-" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("System audio capture unavailable.");
        var errors = process.StandardError.ReadToEndAsync();
        using var cancel = ct.Register(() => { try { process.Kill(true); } catch (InvalidOperationException) { } });
        using var opus = OpusCodecFactory.CreateEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
        opus.Bitrate = 96000; opus.Complexity = 5;
        var pcm = new byte[960 * 2 * 2]; var samples = new short[1920]; var packet = new byte[4000];
        var count = 0;
        try
        {
            while (true)
            {
                await process.StandardOutput.BaseStream.ReadExactlyAsync(pcm, ct);
                for (int i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2));
                var length = opus.Encode(samples, 960, packet, packet.Length);
                var encoded = packet.AsSpan(0, length).ToArray();
                try { peer.SendOpus(encoded); } finally { Array.Clear(encoded); }
                if (++count == 1) report("audio-streaming");
            }
        }
        finally
        {
            Array.Clear(pcm); Array.Clear(samples); Array.Clear(packet);
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(); await errors;
        }
    }
}
