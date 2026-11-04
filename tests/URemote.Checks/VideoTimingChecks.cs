using URemote.Media;
using URemote.Core;

internal static class VideoTimingChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var clock = new VideoRtpClock(100, 0, 90000);
        check(clock.Stamp(0) == 100 && clock.Stamp(1500) == 1600, "60-FPS timestamps follow the 90 kHz media clock");
        check(clock.Stamp(6000) == 6100, "a slow frame advances media time by its actual interval instead of 1/60 second");
        check(clock.Stamp(6000) == 6101 && clock.Stamp(5000) == 6102, "timestamps remain monotonic across duplicate or backwards clock samples");
        clock = new VideoRtpClock(uint.MaxValue - 1000, 0, 90000);
        check(clock.Stamp(0) == uint.MaxValue - 1000 && clock.Stamp(1500) == 499,
            "media timestamp wrap preserves its real-time interval");
        var profiles = new HostVideoSettings([(2560, 1440), (3840, 2160)], frameRateLimits: [60, 60]);
        check(profiles.OutputFps(0) == 60 && profiles.OutputFps(1) == 60, "both screens keep 60 FPS while static");
        profiles.Apply(new(0, 0, 0, 30, 0, 1, 0, 1));
        check(profiles.OutputFps(0) == 30 && profiles.OutputFps(1) == 60, "the controller's lower requested frame rate is still respected");
        profiles = new HostVideoSettings([(3840, 2160), (2560, 1440)], frameRateLimits: [60, 60]);
        profiles.BindCeiling(0, 3840, 2160);
        check(profiles.Apply(new(-1, 0, 0, 60, 2, 0, 0, null, MaxScaleWidth: 1920, MaxScaleHeight: 1080))
            && profiles.Get(0) is { Width: 1920, Height: 1080, Fps: 60 }
            && profiles.Get(1) is { Width: 1920, Height: 1080, Fps: 60 },
            "the Android decoder ceiling limits pixels on both monitors without lowering FPS");
        profiles.AdoptSourceSize(0, 3840, 2160); profiles.BindCeiling(0, 3840, 2160);
        profiles.Apply(new(-1, 0, 0, 60, 4, 0, 0, null));
        check(profiles.Get(0) is { Width: 1920, Height: 1080, Bitrate: 30000000 },
            "new captures, hardware-mode binding and quality updates cannot undo the receiver's scaling limit");
        profiles.Apply(new(-1, 0, 0, 60, 4, 0, 0, null, MaxScaleWidth: 3840, MaxScaleHeight: 2160));
        check(profiles.Get(0) is { Width: 3840, Height: 2160 }, "a higher negotiated decoder ceiling restores native pixels");
        profiles = new HostVideoSettings([(2160, 3840)], frameRateLimits: [60]);
        profiles.Apply(new(-1, 0, 0, 60, 2, 0, 0, null, MaxScaleWidth: 1920, MaxScaleHeight: 1080));
        check(profiles.Get(0) is { Width: 606, Height: 1080 }, "portrait scaling fits the decoder ceiling with even dimensions and preserved aspect ratio");
        check(!profiles.Apply(new(-1, 0, 0, 60, 2, 0, 0, null, MaxScaleWidth: 1)), "invalid decoder scaling limits are rejected");
        var packet = FileTransferProtocol.Blob(21, FileTransferProtocol.Join(FileTransferProtocol.Blob(1, FileTransferProtocol.Int(1, 1)),
            FileTransferProtocol.Blob(2, FileTransferProtocol.Join(FileTransferProtocol.Int(14, 1920), FileTransferProtocol.Int(15, 1080)))));
        check(HostCaptureProtocol.Decode(packet) is { MaxScaleWidth: 1920, MaxScaleHeight: 1080 }, "capture RPC fields 14 and 15 carry the decoder scaling ceiling");
    }
}
