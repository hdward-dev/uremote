using URemote.Core;

namespace URemote.Linux;

// Native Wayland protocol, no shell input tools, root access or /dev/uinput required.
// Only the authenticated media/data-channel owner should pass input to this backend.
public sealed class WaylandVirtualPointer : IAsyncDisposable
{
    private readonly WaylandConnection connection;
    private readonly uint pointer;
    private readonly uint manager;
    private readonly HashSet<uint> held = [];
    private readonly SemaphoreSlim gate = new(1);
    private bool disposed;
    public uint OutputGlobalName { get; }
    private WaylandVirtualPointer(WaylandConnection connection, uint pointer, uint manager, uint output)
    { this.connection = connection; this.pointer = pointer; this.manager = manager; OutputGlobalName = output; }

    public static async Task<WaylandVirtualPointer> CreateAsync(uint outputGlobalName, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var connection = await WaylandConnection.OpenAsync(deadline.Token);
        try
        {
            var global = connection.Globals.FirstOrDefault(g => g.Interface == "zwlr_virtual_pointer_manager_v1" && g.Version >= 2)
                ?? throw new NotSupportedException("Compositor lacks output-bound virtual pointer support.");
            var output = connection.Globals.FirstOrDefault(g => g.Interface == "wl_output" && g.Name == outputGlobalName)
                ?? throw new ArgumentException("Selected output is no longer available.");
            var manager = await connection.BindAsync(global, 2, deadline.Token);
            var outputObject = await connection.BindAsync(output, 1, deadline.Token);
            var pointer = connection.AllocateId();
            await connection.SendAsync(manager, 2, WaylandConnection.Words(0, outputObject, pointer), deadline.Token);
            await connection.RoundtripAsync(deadline.Token);
            return new(connection, pointer, manager, outputGlobalName);
        }
        catch { connection.Dispose(); throw; }
    }

    public async Task ApplyAsync(HostMouseMessage message, PointerCoordinates coordinates,
        uint pixelWidth, uint pixelHeight, CancellationToken ct = default)
    {
        if (message.ScreenId is not null) throw new ArgumentException("Resolve UU screen_id to a bound output before routing input.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        await gate.WaitAsync(deadline.Token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var time = unchecked((uint)Environment.TickCount64);
            switch (message.Action)
            {
                case MouseAction.MoveAbsolute:
                    var motion = AbsolutePayload(time, message.X, message.Y, coordinates, pixelWidth, pixelHeight);
                    await connection.SendAsync(pointer, 1, motion, deadline.Token);
                    break;
                case MouseAction.Press:
                case MouseAction.Release:
                case MouseAction.Click:
                    var button = LinuxButton(message.Button);
                    if (message.Action == MouseAction.Click && held.Contains(button)) throw new InvalidOperationException("Cannot click an already held button.");
                    if (message.Action != MouseAction.Release)
                    {
                        await connection.SendAsync(pointer, 2, WaylandConnection.Words(time, button, 1), deadline.Token);
                        held.Add(button);
                    }
                    if (message.Action != MouseAction.Press)
                    {
                        // Click needs separate frames so a compositor observes both transitions.
                        if (message.Action == MouseAction.Click) await connection.SendAsync(pointer, 4, [], deadline.Token);
                        await connection.SendAsync(pointer, 2, WaylandConnection.Words(time, button, 0), deadline.Token);
                        held.Remove(button);
                    }
                    break;
                case MouseAction.Scroll:
                    await connection.SendAsync(pointer, 5, WaylandConnection.Words(0), deadline.Token);
                    if (message.X != 0) await connection.SendAsync(pointer, 3, AxisPayload(time, 1, message.X), deadline.Token);
                    if (message.Y != 0) await connection.SendAsync(pointer, 3, AxisPayload(time, 0, message.Y), deadline.Token);
                    break;
                default: throw new ArgumentException("Unsupported pointer action.");
            }
            await connection.SendAsync(pointer, 4, [], deadline.Token);
            await connection.RoundtripAsync(deadline.Token);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or OperationCanceledException)
        {
            disposed = true;
            connection.Dispose(); // Compositor removes this device and releases its pressed buttons.
            throw;
        }
        finally { gate.Release(); }
    }

    // UU desktop vertical deltas are opposite Wayland; horizontal deltas retain direction.
    // One unit maps to one logical pixel; physical wheel scaling can be tuned separately.
    public static byte[] AxisPayload(uint time, uint axis, double delta)
    {
        if (axis > 1 || !double.IsFinite(delta) || Math.Abs(delta) > 10000) throw new ArgumentException("Invalid axis.");
        return WaylandConnection.Words(time, axis, unchecked((uint)(int)Math.Round((axis == 0 ? -delta : delta) * 256)));
    }

    public static uint LinuxButton(int uuButton) => uuButton switch
    { 1 => 0x110, 2 => 0x111, 4 => 0x112, 8 => 0x113, 16 => 0x114, _ => throw new ArgumentException("Unknown UU mouse button.") };

    public static byte[] AbsolutePayload(uint time, double x, double y, PointerCoordinates coordinates, uint width, uint height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0) throw new ArgumentException("Invalid pointer position.");
        if (coordinates == PointerCoordinates.MacNormalized)
        {
            if (x > 1 || y > 1) throw new ArgumentException("Normalized pointer position out of bounds.");
            const uint scale = 1_000_000;
            return WaylandConnection.Words(time, (uint)Math.Round(x * scale), (uint)Math.Round(y * scale), scale, scale);
        }
        if (coordinates != PointerCoordinates.WindowsPixels || width is 0 or > 131072 || height is 0 or > 131072 || x > width || y > height)
            throw new ArgumentException("Invalid pixel pointer bounds.");
        return WaylandConnection.Words(time, (uint)Math.Round(x), (uint)Math.Round(y), width, height);
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                foreach (var button in held)
                    await connection.SendAsync(pointer, 2, WaylandConnection.Words(unchecked((uint)Environment.TickCount64), button, 0), timeout.Token);
                await connection.SendAsync(pointer, 4, [], timeout.Token);
                await connection.SendAsync(pointer, 8, [], timeout.Token);
                await connection.SendAsync(manager, 1, [], timeout.Token);
                await connection.RoundtripAsync(timeout.Token);
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or OperationCanceledException) { }
            finally { connection.Dispose(); }
        }
        finally { gate.Release(); }
    }
}
