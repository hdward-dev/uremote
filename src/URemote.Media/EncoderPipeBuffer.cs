using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace URemote.Media;

internal static class EncoderPipeBuffer
{
    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int operation, int value);

    internal static int Grow(Stream stream, int bytes)
    {
        if (!OperatingSystem.IsLinux() || stream is not PipeStream pipe) return 0;
        var handle = pipe.SafePipeHandle;
        var held = false;
        try
        {
            handle.DangerousAddRef(ref held);
            var fd = handle.DangerousGetHandle().ToInt32();
            // Tune only our own pipe. Kernel/user limits may refuse enlargement;
            // retain the existing capacity and keep the encoder working then.
            _ = fcntl(fd, 1031 /* F_SETPIPE_SZ */, bytes);
            return Math.Max(0, fcntl(fd, 1032 /* F_GETPIPE_SZ */, 0));
        }
        finally { if (held) handle.DangerousRelease(); }
    }
}
