using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using WinForge.App.Mvvm;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// Image page. Lets the user pick a Windows ISO via the native file dialog,
/// validates that the file exists, stores the path in <see cref="IAppState"/>,
/// and shows safe file metadata (name, size). It does NOT mount the ISO, read
/// install.wim, call DISM, or detect editions — those are future phases.
/// </summary>
public sealed class ImageViewModel : ViewModelBase
{
    private readonly IAppState _appState;
    private readonly ILoggerService _logger;
    private WindowsImageInfo _imageInfo = new();

    public ImageViewModel(IAppState appState, ILoggerService logger)
    {
        _appState = appState;
        _logger = logger;
        BrowseCommand = new RelayCommand(_ => Browse());
    }

    public ICommand BrowseCommand { get; }

    public string FileDisplay => _imageInfo.SourcePath ?? "Not selected";

    public string SizeDisplay => _imageInfo.SourcePath is null ? "Not selected" : FormatSize(_imageInfo.Size);

    public string ArchitectureDisplay => _imageInfo.Architecture ?? "Not detected";

    public string VersionDisplay => _imageInfo.Version ?? "Not detected";

    public string BuildDisplay => _imageInfo.Build ?? "Not detected";

    public string EditionsDisplay =>
        _imageInfo.Editions.Count > 0 ? $"{_imageInfo.Editions.Count} edition(s)" : "Not detected";

    private void Browse()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Windows ISO (*.iso)|*.iso",
            Title = "Select a Windows ISO"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var path = dialog.FileName;

        if (!File.Exists(path))
        {
            _logger.Warning($"Selected image file does not exist: {path}");
            return;
        }

        _appState.SourceImagePath = path;
        _imageInfo = new WindowsImageInfo
        {
            SourcePath = path,
            Size = new FileInfo(path).Length
        };

        _logger.Info($"Source image selected: {path}");

        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(FileDisplay));
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(ArchitectureDisplay));
        OnPropertyChanged(nameof(VersionDisplay));
        OnPropertyChanged(nameof(BuildDisplay));
        OnPropertyChanged(nameof(EditionsDisplay));
    }

    private static string FormatSize(long bytes)
    {
        const long scale = 1024;
        string[] units = { "B", "KB", "MB", "GB", "TB" };

        double value = bytes;
        var unit = 0;
        while (value >= scale && unit < units.Length - 1)
        {
            value /= scale;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
