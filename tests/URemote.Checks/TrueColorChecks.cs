using URemote.Core;
using URemote.Media;

internal static class TrueColorChecks
{
    internal static void Run(Action<bool, string> check)
    {
        check(FfmpegPacketLength.TryRead("0, 0, 0, 1, 14222, 0xbf181eb7, S=1, Quality stats, 8, 0", out var length) && length == 14222
            && !FfmpegPacketLength.TryRead("#dimensions 0: 3840x2160", out _), "packet lengths preserve FFmpeg framing with optional side data");
        try { FfmpegPacketLength.TryRead("0, 0, 0, 1, 8000001, 0", out _); check(false, "oversized packet is rejected"); }
        catch (FormatException) { check(true, "oversized packet is rejected"); }
        static byte[] Request(params byte[][] fields) => FileTransferProtocol.Blob(21, FileTransferProtocol.Join(
            FileTransferProtocol.Blob(1, FileTransferProtocol.Int(1, 91)), FileTransferProtocol.Blob(2, FileTransferProtocol.Join(fields))));
        var update = HostCaptureProtocol.Decode(Request(FileTransferProtocol.Int(7, 3)))!;
        check(update is { ChromaFormat: 3, RpcId: 91, EnableHdr: false }, "native true-color RPC selects chroma 4:4:4 without enabling HDR");
        check(HostCaptureProtocol.Decode(Request(FileTransferProtocol.Int(11, 1))) is { EnableHdr: true, ChromaFormat: 0 },
            "HDR is decoded independently of true color");
        var unsupported = new HostVideoSettings([(3840, 2160)]);
        var before = unsupported.Get(0);
        check(!unsupported.Apply(update) && unsupported.Get(0) == before, "unprobed encoders reject true color without changing video settings");
        var profiles = new HostVideoSettings([(3840, 2160), (2560, 1440)], supportsTrueColor: true, decoderCapabilities:
            [new(60, 1, 3840, 2160, 1), new(60, 1, 1920, 1080, 3), new(60, 2, 7680, 4320, 3)]);
        check(profiles.Apply(update) && profiles.Get(0) is { ChromaFormat: 3, Width: 1920, Height: 1080 }
            && profiles.Get(1).ChromaFormat == 1, "true color uses the selected display's H264 4:4:4 decoder limit");
        profiles.Apply(new(-1, 0, 0, 60, 4, 0, 0, null));
        profiles.BindCeiling(0, 3840, 2160); profiles.AdoptSourceSize(0, 3840, 2160);
        profiles.ObserveReceiver(100, 0, 1); profiles.ObserveReceiver(2000, 500, 2);
        check(profiles.Get(0) is { ChromaFormat: 3, Width: 1920, Height: 1080, Fps: 60 },
            "quality, capture refresh and congestion updates preserve the chosen chroma and its decoder ceiling");
        check(profiles.Apply(update with { ChromaFormat = 1 }) && profiles.Get(0) is { Width: 3840, Height: 2160, ChromaFormat: 1 },
            "switching back to 4:2:0 restores that decoder's native resolution");
        before = profiles.Get(0);
        check(!profiles.Apply(update with { EnableHdr = true }) && !profiles.Apply(update with { ChromaFormat = 2 })
            && profiles.Get(0) == before, "unsupported HDR and 4:2:2 requests cannot silently change the stream");
        check(profiles.Apply(update with { Screen = -2 }) && profiles.Get(0).ChromaFormat == 3 && profiles.Get(1).ChromaFormat == 3,
            "global true-color settings reach both display streams");
        var sync = profiles.Sync(3);
        var chroma = FileTransferProtocol.Blob(25, FileTransferProtocol.Join(FileTransferProtocol.Int(1, 60), FileTransferProtocol.Int(2, 4),
            FileTransferProtocol.Int(3, 3), FileTransferProtocol.Int(5, 1920), FileTransferProtocol.Int(6, 1080),
            FileTransferProtocol.Int(9, 1920), FileTransferProtocol.Int(10, 1080)));
        check(sync.AsSpan().EndsWith(chroma), "CaptureSettingSync reports the actual chroma, dimensions, quality and frame rate");
        profiles.DisableTrueColor();
        check(profiles.Get(0).ChromaFormat == 1 && profiles.Get(1).ChromaFormat == 1 && !profiles.Apply(update),
            "a true-color encoder failure safely resumes 4:2:0 on every output");
        byte[] echo = [8, 7, 26, 0];
        var reply = HostControlEcho.Reply(echo, 1, 2, true, true, captureSettingVersion: 4)!;
        check(reply.AsSpan().EndsWith(new byte[] { 34, 6, 8, 4, 24, 2, 16, 2 }),
            "echo advertises capture-setting version 4 without duplicate fields or an HDR promise");
    }
}
