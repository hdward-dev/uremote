using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Media.Imaging;

namespace URemote.Module;

public abstract class CardModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
}

public sealed class AsyncCommand(Func<Task> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public async void Execute(object? parameter) => await execute();
}

public sealed class ScreenShareModel : CardModel
{
    public uint Id { get; init; }
    public string Label { get; init; } = "";
    private bool? _isChecked = true;
    public bool? IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
}

public sealed class LocalScreenModel : CardModel
{
    private string _caption = "";
    private Bitmap? _snapshot;
    private bool _showSnapshot = true;
    public string Caption { get => _caption; set => Set(ref _caption, value); }
    public Bitmap? Snapshot { get => _snapshot; set => Set(ref _snapshot, value); }
    public bool ShowSnapshot { get => _showSnapshot; set => Set(ref _showSnapshot, value); }
    public ICommand? OpenSettings { get; init; }
}

public sealed class LocalHistoryModel
{
    public required string Title { get; init; }
    public required string Account { get; init; }
    public required ICommand QuickConnect { get; init; }
}

public sealed class CatalogMessageModel
{
    public required string Title { get; init; }
    public required string Message { get; init; }
}

public sealed class DeviceDetailModel
{
    public required string Name { get; init; }
    public required string Summary { get; init; }
    public required string Identifier { get; init; }
    public required string Version { get; init; }
    public required string Description { get; init; }
    public required string DeviceId { get; init; }
}

public sealed class DeviceCardModel : CardModel
{
    private bool _showLabels = true;
    public required string Name { get; init; }
    public required string StatusText { get; init; }
    public required string PlatformName { get; init; }
    public required string BadgeText { get; init; }
    public required string FavoriteLabel { get; init; }
    public required string TransferTip { get; init; }
    public required string TerminalTip { get; init; }
    public required string TransferName { get; init; }
    public required string TerminalName { get; init; }
    public required string PortName { get; init; }
    public required string WakeName { get; init; }
    public bool Online { get; init; }
    public bool Offline => !Online;
    public bool HasWallpaper { get; init; }
    public bool ShowPlaceholder => !HasWallpaper;
    public Bitmap? Wallpaper { get; init; }
    public double WallpaperOpacity { get; init; } = 1;
    public bool IsMobile { get; init; }
    public bool IsDesktop => !IsMobile;
    public bool ShowBadge { get; init; }
    public bool CanControl { get; init; }
    public bool ListView { get; init; }
    public double PreviewHeight { get; init; }
    public bool IsSelected { get; init; }
    public string? ConnectTip { get; init; }
    public string ConnectName { get; init; } = "";
    public double TransferOpacity => CanControl ? 1 : .35;
    public bool ShowLabels { get => _showLabels; set => Set(ref _showLabels, value); }
    public Action? RequestRemote { get; init; }
    public Action? RequestFiles { get; init; }
    public Action? RequestTerminal { get; init; }
    public Action? RequestFavorite { get; init; }
    public Action? RequestDetails { get; init; }
}
