using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace URemote.Linux;

public sealed record WaylandGlobal(uint Name, string Interface, uint Version);
internal sealed class WaylandConnection : IDisposable
{
    private readonly Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private uint nextId = 3;
    private readonly List<WaylandGlobal> globals = [];
    public IReadOnlyList<WaylandGlobal> Globals => globals.ToArray();
    public uint AllocateId() => nextId++;

    public static async Task<WaylandConnection> OpenAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Linux little-endian Wayland required.");
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? throw new InvalidOperationException("No desktop runtime directory.");
        var display = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "wayland-0";
        var path = Path.IsPathRooted(display) ? display : Path.Combine(runtime, display);
        var connection = new WaylandConnection();
        try
        {
            await connection.socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
            await connection.SendAsync(1, 1, Words(2), ct); // wl_display.get_registry
            await connection.RoundtripAsync(ct);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public async Task<uint> BindAsync(WaylandGlobal global, uint version, CancellationToken ct)
    {
        if (version == 0 || version > global.Version) throw new ArgumentOutOfRangeException(nameof(version));
        var id = AllocateId();
        var name = Encoding.UTF8.GetBytes(global.Interface + '\0');
        var padded = (name.Length + 3) & ~3;
        var payload = new byte[16 + padded];
        Words(global.Name, (uint)name.Length).CopyTo(payload, 0);
        name.CopyTo(payload, 8);
        Words(version, id).CopyTo(payload, 8 + padded);
        await SendAsync(2, 0, payload, ct);
        return id;
    }

    public async Task SendAsync(uint target, ushort opcode, byte[] payload, CancellationToken ct)
    {
        if (payload.Length > 65524 || payload.Length % 4 != 0) throw new ArgumentException("Invalid Wayland payload size.");
        var message = new byte[payload.Length + 8];
        Words(target, ((uint)message.Length << 16) | opcode).CopyTo(message, 0);
        payload.CopyTo(message, 8);
        var sent = 0;
        while (sent < message.Length)
        {
            var count = await socket.SendAsync(message.AsMemory(sent), SocketFlags.None, ct);
            if (count == 0) throw new IOException("Wayland connection closed.");
            sent += count;
        }
    }

    public async Task RoundtripAsync(CancellationToken ct)
    {
        var callback = AllocateId();
        await SendAsync(1, 0, Words(callback), ct);
        while (true)
        {
            var (target, opcode, data) = await ReadEventAsync(ct);
            if (target == callback && opcode == 0) return;
            if (target == 2 && opcode == 0)
            {
                if (data.Length < 12) throw new FormatException("Invalid registry event.");
                var name = BinaryPrimitives.ReadUInt32LittleEndian(data);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
                if (length is < 1 or > 1024) throw new FormatException("Invalid registry interface name.");
                var end = 8 + ((int)length + 3 & ~3);
                if (end + 4 != data.Length || data[8 + length - 1] != 0) throw new FormatException("Truncated registry event.");
                var text = new UTF8Encoding(false, true).GetString(data, 8, (int)length - 1);
                globals.Add(new(name, text, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(end))));
            }
            else if (target == 2 && opcode == 1 && data.Length == 4)
                globals.RemoveAll(g => g.Name == BinaryPrimitives.ReadUInt32LittleEndian(data));
            // Other events (output metadata, delete_id) carry no file descriptors on these bound interfaces.
        }
    }

    public async Task<(uint Target, uint Opcode, byte[] Data)> ReadEventAsync(CancellationToken ct)
    {
        var header = new byte[8];
        await ReadExactlyAsync(header, ct);
        var target = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var word = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var size = (int)(word >> 16);
        var opcode = word & 65535;
        if (size < 8 || size % 4 != 0) throw new FormatException("Invalid Wayland event size.");
        var data = new byte[size - 8];
        await ReadExactlyAsync(data, ct);
        if (target == 1 && opcode == 0) throw new IOException("Compositor rejected a Wayland protocol request.");
        return (target, opcode, data);
    }

    public async Task SendFileDescriptorAsync(uint target, ushort opcode, byte[] payload,
        Microsoft.Win32.SafeHandles.SafeFileHandle file, CancellationToken ct)
    {
        var message = new byte[payload.Length + 8];
        Words(target, ((uint)message.Length << 16) | opcode).CopyTo(message, 0);
        payload.CopyTo(message, 8);
        var added = false;
        file.DangerousAddRef(ref added);
        try
        {
            int sent;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var result = UnixFileDescriptor.Send(socket.Handle.ToInt32(), file.DangerousGetHandle().ToInt32(), message);
                sent = result.Count;
                if (sent >= 0) break;
                if (result.Error is not (4 or 11)) throw new IOException("Wayland descriptor transfer failed.");
                await Task.Delay(1, ct);
            }
            if (sent == 0) throw new IOException("Wayland descriptor transfer closed.");
            while (sent < message.Length)
            {
                var count = await socket.SendAsync(message.AsMemory(sent), SocketFlags.None, ct);
                if (count == 0) throw new IOException("Wayland connection closed.");
                sent += count;
            }
        }
        finally { if (added) file.DangerousRelease(); }
    }

    private async Task ReadExactlyAsync(byte[] bytes, CancellationToken ct)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await socket.ReceiveAsync(bytes.AsMemory(offset), SocketFlags.None, ct);
            if (read == 0) throw new IOException("Wayland connection closed.");
            offset += read;
        }
    }
    public static byte[] Words(params uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }
    public void Dispose() => socket.Dispose();
}

public static class WaylandCapabilities
{
    public static async Task<IReadOnlyList<WaylandGlobal>> DiscoverAsync(CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var connection = await WaylandConnection.OpenAsync(deadline.Token);
        return connection.Globals.Where(g => g.Interface is "zwlr_virtual_pointer_manager_v1"
            or "zwp_virtual_keyboard_manager_v1" or "zwlr_screencopy_manager_v1" or "wl_output" or "wl_seat").ToArray();
    }
}
