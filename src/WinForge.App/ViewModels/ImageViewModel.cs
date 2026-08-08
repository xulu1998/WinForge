using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using WinForge.App.Mvvm;
using WinForge.App.Services;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// Image page. Lets the user pick a Windows ISO, validates it, and runs a safe,
/// read-only inspection that reports the detected type and install-image layout.
/// All platform work (file dialog, ISO mount, PowerShell) is reached through
/// abstractions; this ViewModel never touches WPF dialogs, the registry, or
/// <c>Process</c> directly.
/// </summary>
public sealed class ImageViewModel : ViewModelBase
{
    private readonly IAppState _appState;
    private readonly ILoggerService _logger;
    private readonly IIsoInspectionService _inspection;
    private readonly IFilePicker _filePicker;

    private IsoInspectionResult? _result;
    private bool _isInspecting;
    private readonly WindowsImageInfo _imageInfo = new();

    public ImageViewModel(IAppState appState, ILoggerService logger, IIsoInspectionService inspection, IFilePicker filePicker)
    {
        _appState = appState;
        _logger = logger;
        _inspection = inspection;
        _filePicker = filePicker;

        SelectIsoCommand = new AsyncRelayCommand(_ => SelectIsoAsync());
        InspectIsoCommand = new AsyncRelayCommand(_ => InspectCurrentAsync());
    }

    public ICommand SelectIsoCommand { get; }

    public ICommand InspectIsoCommand { get; }

    public string FileDisplay =>
        string.IsNullOrEmpty(_appState.SourceImagePath) ? "No ISO selected" : _appState.SourceImagePath;

    public string FileNameDisplay => _result?.FileName ?? "—";

    public string SizeDisplay => _result is null ? "—" : FormatSize(_result.FileSizeBytes);

    public string DetectedTypeDisplay => _result switch
    {
        null => "No ISO selected",
        _ when _result.Status == IsoInspectionStatus.Failed => "Unable to inspect ISO",
        _ when _result.DetectedType == IsoDetectedType.WindowsIsoCandidate => "Windows ISO Candidate",
        _ => "Unknown"
    };

    public string InstallImageDisplay => _result switch
    {
        null => "—",
        _ when _result.InstallImageType == InstallImageType.Wim => "install.wim",
        _ when _result.InstallImageType == InstallImageType.Esd => "install.esd",
        _ => "None"
    };

    public string StatusMessage => _result?.ErrorMessage ?? string.Empty;

    public bool IsInspecting
    {
        get => _isInspecting;
        private set => SetField(ref _isInspecting, value);
    }

    public bool HasError => _result?.Status == IsoInspectionStatus.Failed;

    public bool HasResult => _result is not null;

    // Future-phase fields — not populated by Step 2.1 inspection.
    public string ArchitectureDisplay => _imageInfo.Architecture ?? "Not detected";
    public string VersionDisplay => _imageInfo.Version ?? "Not detected";
    public string BuildDisplay => _imageInfo.Build ?? "Not detected";
    public string EditionsDisplay =>
        _imageInfo.Editions.Count > 0 ? $"{_imageInfo.Editions.Count} edition(s)" : "Not detected";

    public async Task SelectIsoAsync()
    {
        var path = _filePicker.PickIsoFile();
        if (path is null)
        {
            // Cancellation is not an error and must not produce a failure state.
            _logger.Debug("ISO picker cancelled by user.");
            return;
        }

        _appState.SourceImagePath = path;
        _imageInfo.SourcePath = path;
        _logger.Info($"ISO selected: {path}");

        await InspectCurrentAsync();
    }

    public async Task InspectCurrentAsync()
    {
        var path = _appState.SourceImagePath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        IsInspecting = true;
        _logger.Info("ISO inspection started.");
        try
        {
            var result = await _inspection.InspectAsync(path, CancellationToken.None);
            _result = result;
            _imageInfo.Size = result.FileSizeBytes;
            _logger.Info(result.Status == IsoInspectionStatus.Completed
                ? "ISO inspection completed."
                : "ISO inspection failed.");
        }
        catch (Exception ex)
        {
            _result = IsoInspectionResult.Failed(path, "Unexpected error during inspection.");
            _logger.Error($"ISO inspection failed unexpectedly: {ex.Message}");
        }
        finally
        {
            IsInspecting = false;
            Refresh();
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(FileDisplay));
        OnPropertyChanged(nameof(FileNameDisplay));
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(DetectedTypeDisplay));
        OnPropertyChanged(nameof(InstallImageDisplay));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(IsInspecting));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasResult));
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
