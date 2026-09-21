namespace AsterDock.Contracts;

public sealed record SystemDeviceDetails(
    string DeviceName,
    string DeviceModel,
    string OperatingSystem,
    string Architecture,
    string ProcessorName,
    string GraphicsName,
    long TotalMemoryBytes,
    string SystemDrive);

public sealed record SystemMetricsSnapshot(
    DateTimeOffset Timestamp,
    double CpuUsage,
    double? CpuTemperature,
    double? GpuUsage,
    double? GpuTemperature,
    long UsedMemoryBytes,
    long TotalMemoryBytes,
    double DiskUsage,
    long DownloadBytesPerSecond,
    long UploadBytesPerSecond);

/// <summary>
/// Container-owned, shared hardware metrics stream. Sampling starts with the
/// first subscription and becomes idle after the final subscription is disposed.
/// </summary>
public interface ISystemMetricsService
{
    SystemMetricsSnapshot? Current { get; }
    Task<SystemDeviceDetails> GetDeviceDetailsAsync(CancellationToken cancellationToken = default);
    IDisposable Subscribe(
        Action<SystemMetricsSnapshot> onUpdated,
        Action<Exception>? onError = null);
}
