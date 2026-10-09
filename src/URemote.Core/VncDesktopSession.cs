using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace URemote.Core;

/// <summary>RFB 3.3/3.7/3.8 client with raw, CopyRect and DesktopSize updates.</summary>
public sealed class VncDesktopSession : ILocalDesktopSession
{
    private readonly Channel<byte[]> input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.Wait });
    private int width, height;
    private byte[] pixels = [];
    private volatile bool ready;
    private bool resizePending;
    public event Action<LocalDesktopFrame>? Frame;
    public event Action<string>? Status;

    public async Task RunAsync(LocalDesktopOptions options, CancellationToken cancellationToken)
    {
        options.Validate();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var client = new TcpClient { NoDelay = true };
        using var cancel = stop.Token.Register(client.Dispose);
        Task? writer = null;
        try
        {
            Status?.Invoke("正在连接 VNC…");
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                await client.ConnectAsync(options.Host, options.Port, deadline.Token);
                var stream = client.GetStream();
                await HandshakeAsync(stream, options.Password, deadline.Token);
            }
            var network = client.GetStream();
            ready = true;
            writer = WriteInputsAsync(network, stop.Token);
            Status?.Invoke("已连接 · VNC");
            await RequestAsync(network, false, stop.Token);
            while (!stop.IsCancellationRequested)
            {
                var type = (await ReadAsync(network, 1, stop.Token))[0];
                switch (type)
                {
                    case 0:
                        var header = await ReadAsync(network, 3, stop.Token);
                        var count = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
                        for (var i = 0; i < count; i++) await RectangleAsync(network, stop.Token);
                        Frame?.Invoke(new(width, height, (byte[])pixels.Clone()));
                        await RequestAsync(network, !resizePending, stop.Token);
                        resizePending = false;
                        break;
                    case 1:
                        var map = await ReadAsync(network, 5, stop.Token);
                        await ReadAsync(network, BinaryPrimitives.ReadUInt16BigEndian(map.AsSpan(3)) * 6, stop.Token);
                        break;
                    case 2: break; // Bell
                    case 3:
                        var cut = await ReadAsync(network, 7, stop.Token);
                        var length = BinaryPrimitives.ReadUInt32BigEndian(cut.AsSpan(3));
                        if (length > 1024 * 1024) throw new InvalidDataException("VNC 文本消息过大。");
                        await ReadAsync(network, (int)length, stop.Token);
                        break;
                    default: throw new NotSupportedException("VNC 服务发送了不支持的消息。");
                }
            }
        }
        finally
        {
            ready = false; stop.Cancel(); input.Writer.TryComplete();
            if (writer is not null) { try { await writer; } catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { } }
            pixels = [];
        }
    }

    private async Task HandshakeAsync(NetworkStream stream, string password, CancellationToken token)
    {
        var version = Encoding.ASCII.GetString(await ReadAsync(stream, 12, token));
        var minor = version switch { "RFB 003.003\n" => 3, "RFB 003.007\n" => 7, "RFB 003.008\n" => 8,
            _ => throw new NotSupportedException("仅支持 RFB 3.3、3.7 和 3.8 的 VNC 服务。") };
        await stream.WriteAsync(Encoding.ASCII.GetBytes(version), token);
        uint security;
        if (minor == 3) security = BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(stream, 4, token));
        else
        {
            var count = (await ReadAsync(stream, 1, token))[0];
            if (count == 0) throw new InvalidOperationException("VNC 服务拒绝连接。");
            var types = await ReadAsync(stream, count, token);
            security = types.Contains((byte)2) ? 2u : types.Contains((byte)1) ? 1u : 0u;
            if (security == 0) throw new NotSupportedException("该 VNC 服务需要暂不支持的认证方式；请选择 VNC 密码认证。");
            await stream.WriteAsync(new[] { (byte)security }, token);
        }
        if (security is not (1 or 2)) throw new NotSupportedException("不支持该 VNC 认证方式。");
        if (security == 2)
        {
            var challenge = await ReadAsync(stream, 16, token);
            var response = VncAuthentication.Respond(password, challenge);
            await stream.WriteAsync(response, token);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(response);
        }
        if (security == 2 || minor == 8)
            if (BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(stream, 4, token)) != 0)
                throw new InvalidOperationException("VNC 认证失败，请检查密码。");
        await stream.WriteAsync(new byte[] { 1 }, token); // Shared connection
        var init = await ReadAsync(stream, 24, token);
        Resize(BinaryPrimitives.ReadUInt16BigEndian(init), BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(2)));
        var nameLength = BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(20));
        if (nameLength > 65536) throw new InvalidDataException("VNC 桌面名称过长。");
        await ReadAsync(stream, (int)nameLength, token);
        // 32-bit little-endian true-color BGRX. Alpha is filled before painting.
        await stream.WriteAsync(new byte[] { 0, 0, 0, 0, 32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0 }, token);
        var encodings = new byte[16]; encodings[0] = 2; encodings[3] = 3;
        BinaryPrimitives.WriteInt32BigEndian(encodings.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32BigEndian(encodings.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32BigEndian(encodings.AsSpan(12), -223);
        await stream.WriteAsync(encodings, token);
    }

    private void Resize(int w, int h)
    {
        if (w < 1 || h < 1 || w > 8192 || h > 8192 || (long)w * h > 16 * 1024 * 1024)
            throw new InvalidDataException("VNC 桌面尺寸超出支持范围。");
        width = w; height = h; pixels = new byte[checked(w * h * 4)];
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
    }

    private async Task RectangleAsync(NetworkStream stream, CancellationToken token)
    {
        var rect = await ReadAsync(stream, 12, token);
        var x = BinaryPrimitives.ReadUInt16BigEndian(rect); var y = BinaryPrimitives.ReadUInt16BigEndian(rect.AsSpan(2));
        var w = BinaryPrimitives.ReadUInt16BigEndian(rect.AsSpan(4)); var h = BinaryPrimitives.ReadUInt16BigEndian(rect.AsSpan(6));
        var encoding = BinaryPrimitives.ReadInt32BigEndian(rect.AsSpan(8));
        if (encoding == -223) { Resize(w, h); resizePending = true; return; }
        if (x + w > width || y + h > height) throw new InvalidDataException("VNC 画面区域越界。");
        if (encoding == 0)
        {
            var data = await ReadAsync(stream, checked(w * h * 4), token);
            for (var i = 3; i < data.Length; i += 4) data[i] = 255;
            for (var row = 0; row < h; row++) Buffer.BlockCopy(data, row * w * 4, pixels, ((y + row) * width + x) * 4, w * 4);
        }
        else if (encoding == 1)
        {
            var source = await ReadAsync(stream, 4, token);
            var sx = BinaryPrimitives.ReadUInt16BigEndian(source); var sy = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(2));
            if (sx + w > width || sy + h > height) throw new InvalidDataException("VNC 复制区域越界。");
            var copy = new byte[checked(w * h * 4)];
            for (var row = 0; row < h; row++) Buffer.BlockCopy(pixels, ((sy + row) * width + sx) * 4, copy, row * w * 4, w * 4);
            for (var row = 0; row < h; row++) Buffer.BlockCopy(copy, row * w * 4, pixels, ((y + row) * width + x) * 4, w * 4);
        }
        else throw new NotSupportedException("VNC 服务发送了未协商的画面编码。");
    }

    private Task RequestAsync(NetworkStream stream, bool incremental, CancellationToken token)
    {
        var request = new byte[10]; request[0] = 3; request[1] = incremental ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), (ushort)height);
        // After the handshake the writer owns all writes, including update requests.
        return input.Writer.WriteAsync(request, token).AsTask();
    }

    private async Task WriteInputsAsync(NetworkStream stream, CancellationToken token)
    {
        try { await foreach (var packet in input.Reader.ReadAllAsync(token)) await stream.WriteAsync(packet, token); }
        catch { stream.Dispose(); throw; }
    }

    private void Queue(byte[] packet)
    {
        if (ready && !input.Writer.TryWrite(packet)) throw new IOException("输入队列已满，请断开后重试。");
    }

    public void Pointer(int x, int y, int buttons, int wheel = 0)
    {
        var packet = new byte[6]; packet[0] = 5;
        packet[1] = (byte)(buttons | (wheel > 0 ? 8 : wheel < 0 ? 16 : 0));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)Math.Clamp(x, 0, Math.Max(0, width - 1)));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)Math.Clamp(y, 0, Math.Max(0, height - 1)));
        Queue(packet);
        if (wheel != 0) { var release = (byte[])packet.Clone(); release[1] = (byte)buttons; Queue(release); }
    }

    public void Key(uint symbol, bool down)
    {
        var packet = new byte[8]; packet[0] = 4; packet[1] = down ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), symbol); Queue(packet);
    }

    private static async Task<byte[]> ReadAsync(NetworkStream stream, int count, CancellationToken token)
    {
        var buffer = new byte[count]; await stream.ReadExactlyAsync(buffer, token); return buffer;
    }
}
