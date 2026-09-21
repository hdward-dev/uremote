using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace URemote.Linux;

internal static class UnixFileDescriptor
{
    [StructLayout(LayoutKind.Sequential)] private struct IoVector { public IntPtr Base; public nuint Length; }
    [StructLayout(LayoutKind.Sequential)] private struct MessageHeader
    {
        public IntPtr Name;
        public uint NameLength;
        public IntPtr IoVectors;
        public nuint IoVectorCount;
        public IntPtr Control;
        public nuint ControlLength;
        public int Flags;
    }
    [DllImport("libc", SetLastError = true)] private static extern nint sendmsg(int socket, ref MessageHeader message, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int memfd_create([MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint flags);

    public static SafeFileHandle CreateMemoryFile()
    {
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8) throw new PlatformNotSupportedException("Linux 64-bit required.");
        var fd = memfd_create("u-remote-frame", 1); // MFD_CLOEXEC
        if (fd < 0) throw new IOException("Unable to create screen buffer.");
        return new(new IntPtr(fd), ownsHandle: true);
    }

    public static (int Count, int Error) Send(int socket, int descriptor, byte[] bytes)
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Linux 64-bit descriptor ABI required.");
        var data = Marshal.AllocHGlobal(bytes.Length);
        var vector = Marshal.AllocHGlobal(Marshal.SizeOf<IoVector>());
        var control = Marshal.AllocHGlobal(24);
        try
        {
            Marshal.Copy(bytes, 0, data, bytes.Length);
            Marshal.StructureToPtr(new IoVector { Base = data, Length = (nuint)bytes.Length }, vector, false);
            // cmsghdr: length=20, SOL_SOCKET=1, SCM_RIGHTS=1, one int FD, 8-byte aligned storage.
            Marshal.Copy(new byte[24], 0, control, 24);
            Marshal.WriteInt64(control, 20);
            Marshal.WriteInt32(control, 8, 1);
            Marshal.WriteInt32(control, 12, 1);
            Marshal.WriteInt32(control, 16, descriptor);
            var header = new MessageHeader { IoVectors = vector, IoVectorCount = 1, Control = control, ControlLength = 24 };
            var result = sendmsg(socket, ref header, 0x4000); // MSG_NOSIGNAL
            return ((int)result, result < 0 ? Marshal.GetLastPInvokeError() : 0);
        }
        finally { Marshal.FreeHGlobal(control); Marshal.FreeHGlobal(vector); Marshal.FreeHGlobal(data); }
    }
}
