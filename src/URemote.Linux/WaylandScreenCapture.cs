using System.Buffers.Binary;

namespace URemote.Linux;

public sealed record CapturedScreen(uint OutputGlobalName, uint Width, uint Height, uint Stride,
    uint ShmFormat, bool YInverted, byte[] Pixels)
{
    public override string ToString() => $"CapturedScreen({Width}x{Height}, pixel contents omitted)";
}

public static class WaylandScreenCapture
{
    // One-shot CPU/shared-memory capture. Not a real-time encoder or PipeWire streaming session.
    public static async Task<CapturedScreen> CaptureAsync(uint outputGlobalName, bool includeCursor = true,
        CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var connection = await WaylandConnection.OpenAsync(token);
        var captureGlobal = connection.Globals.FirstOrDefault(g => g.Interface == "zwlr_screencopy_manager_v1")
            ?? throw new NotSupportedException("Compositor does not expose screencopy.");
        var outputGlobal = connection.Globals.FirstOrDefault(g => g.Interface == "wl_output" && g.Name == outputGlobalName)
            ?? throw new ArgumentException("Selected output is unavailable.");
        var shmGlobal = connection.Globals.FirstOrDefault(g => g.Interface == "wl_shm")
            ?? throw new NotSupportedException("Compositor does not expose shared memory.");
        // v1 guarantees a shared-memory buffer event, and needs no buffer_done negotiation.
        var manager = await connection.BindAsync(captureGlobal, 1, token);
        var output = await connection.BindAsync(outputGlobal, 1, token);
        var shm = await connection.BindAsync(shmGlobal, 1, token);
        var frame = connection.AllocateId();
        await connection.SendAsync(manager, 0, WaylandConnection.Words(frame, includeCursor ? 1u : 0u, output), token);
        uint width, height, stride, format;
        while (true)
        {
            var e = await connection.ReadEventAsync(token);
            if (e.Target != frame) continue;
            if (e.Opcode == 3) throw new IOException("Compositor rejected screen capture.");
            if (e.Opcode != 0) continue;
            if (e.Data.Length != 16) throw new FormatException("Invalid screen buffer event.");
            format = Word(e.Data, 0); width = Word(e.Data, 4); height = Word(e.Data, 8); stride = Word(e.Data, 12);
            break;
        }
        var size = ValidateBuffer(width, height, stride);
        using var memory = UnixFileDescriptor.CreateMemoryFile();
        RandomAccess.SetLength(memory, size);
        var pool = connection.AllocateId();
        // The FD is ancillary data, not an integer in the Wayland message body.
        await connection.SendFileDescriptorAsync(shm, 0, WaylandConnection.Words(pool, (uint)size), memory, token);
        var buffer = connection.AllocateId();
        await connection.SendAsync(pool, 0, WaylandConnection.Words(buffer, 0, width, height, stride, format), token);
        await connection.SendAsync(frame, 0, WaylandConnection.Words(buffer), token);
        var inverted = false;
        while (true)
        {
            var e = await connection.ReadEventAsync(token);
            if (e.Target != frame) continue;
            if (e.Opcode == 3) throw new IOException("Compositor failed to copy the screen.");
            if (e.Opcode == 1)
            {
                if (e.Data.Length != 4) throw new FormatException("Invalid screen flags.");
                inverted = (Word(e.Data, 0) & 1) != 0;
            }
            if (e.Opcode == 2) break;
        }
        var pixels = new byte[size];
        var position = 0;
        while (position < size)
        {
            var read = await RandomAccess.ReadAsync(memory, pixels.AsMemory(position), position, token);
            if (read == 0) throw new IOException("Incomplete shared-memory screen buffer.");
            position += read;
        }
        await connection.SendAsync(frame, 1, [], token);
        await connection.SendAsync(buffer, 0, [], token);
        await connection.SendAsync(pool, 1, [], token);
        await connection.SendAsync(manager, 2, [], token);
        await connection.RoundtripAsync(token);
        return new(outputGlobalName, width, height, stride, format, inverted, pixels);
    }

    public static int ValidateBuffer(uint width, uint height, uint stride)
    {
        var size = (ulong)height * stride;
        if (width is 0 or > 16384 || height is 0 or > 16384 || stride < width || size is 0 or > 268435456)
            throw new FormatException("Screen buffer dimensions exceed limits.");
        return (int)size;
    }
    private static uint Word(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
}
