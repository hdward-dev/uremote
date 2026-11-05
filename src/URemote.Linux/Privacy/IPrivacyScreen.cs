namespace URemote.Linux;

public interface IPrivacyScreen : IAsyncDisposable
{
    bool IsActive { get; }
    Task SetImageAsync(URemote.Core.PrivacyScreenImage? image, CancellationToken ct);
}
