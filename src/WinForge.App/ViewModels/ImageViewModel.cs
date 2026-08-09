using System.Collections.Generic;
using System.Linq;
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
/// read-only inspection that reports the detected type, install-image layout,
/// and (Step 2.2) the Windows image metadata: version, build, architecture,
/// languages, and the list of editions. All platform work (file dialog, ISO
/// mount, DISM) is reached through abstractions; this ViewModel never touches
/// WPF dialogs, the registry, or <c>Process</c> directly. Edition selection is
/// written to <see cref="IAppState.SelectedEdition"/> for the Home page to show;
/// it never mounts, extracts, or modifies an image.
/// </summary>
public sealed class ImageViewModel : ViewModelBase
{
    private readonly IAppState _appState;
    private readonly ILoggerService _logger;
    private readonly IIsoInspectionService _inspection;
    private readonly IFilePicker _filePicker;
    private readonly IImageWorkspaceFactory _workspaceFactory;
    private readonly IWimService _wimService;

    private IsoInspectionResult? _result;
    private bool _isInspecting;

    public ImageViewModel(
        IAppState appState,
        ILoggerService logger,
        IIsoInspectionService inspection,
        IFilePicker filePicker,
        IImageWorkspaceFactory workspaceFactory,
        IWimService wimService)
    {
        _appState = appState;
        _logger = logger;
        _inspection = inspection;
        _filePicker = filePicker;
        _workspaceFactory = workspaceFactory;
        _wimService = wimService;

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

    // Step 2.2 — Windows image metadata. When every edition agrees, the top-level
    // value is shown; when editions disagree, "Mixed"; when nothing was read,
    // "Not detected". The model keeps raw nullable data — the UI owns this choice.

    public string WindowsVersionDisplay => TopLevelOr(
        _result?.ImageMetadata?.Version,
        e => e.Version,
        "Not detected");

    public string BuildDisplay => TopLevelOr(
        _result?.ImageMetadata?.Build,
        e => e.Build,
        "Not detected");

    public string ArchitectureDisplay => TopLevelOr(
        _result?.ImageMetadata?.Architecture,
        e => e.Architecture,
        "Not detected");

    public string LanguagesDisplay
    {
        get
        {
            var md = _result?.ImageMetadata;
            if (md is null || md.Editions.Count == 0)
            {
                return "Not detected";
            }

            if (md.Languages is { Count: > 0 })
            {
                return string.Join(", ", md.Languages);
            }

            return md.Editions.Any(e => e.Languages.Count > 0) ? "Mixed" : "Not detected";
        }
    }

    public string EditionsDisplay =>
        _result?.ImageMetadata?.Editions.Count > 0
            ? $"{_result.ImageMetadata.Editions.Count} edition(s)"
            : "Not detected";

    /// <summary>Editions (image indexes) detected in the install image.</summary>
    public IReadOnlyList<WindowsEditionInfo> Editions =>
        (IReadOnlyList<WindowsEditionInfo>?)_result?.ImageMetadata?.Editions
        ?? Array.Empty<WindowsEditionInfo>();

    /// <summary>
    /// The edition the user has selected for customization. Writing it updates
    /// <see cref="IAppState.SelectedEdition"/> so the Home page reflects it, and
    /// also creates/updates the durable <see cref="IAppState.CurrentImageWorkspace"/>
    /// for the selected index. This is a status selection only — it performs no
    /// image extraction, mount, or modification.
    /// </summary>
    public WindowsEditionInfo? SelectedEdition
    {
        get => _appState.SelectedEdition;
        set
        {
            if (Equals(_appState.SelectedEdition, value))
            {
                return;
            }

            _appState.SelectedEdition = value;
            UpdateWorkspace(value);
            Refresh();
        }
    }

    // Step 3.1 — Durable selected-image workspace. These read from the durable
    // AppState workspace, which never holds a temporary mounted drive letter.

    /// <summary>The durable selected-image workspace, or null when none is ready.</summary>
    public ImageWorkspace? Workspace => _appState.CurrentImageWorkspace;

    public string WorkspaceStatusDisplay =>
        _appState.CurrentImageWorkspace is null ? "Select an edition" : "Ready";

    public string WorkspaceEditionDisplay => _appState.CurrentImageWorkspace?.SelectedEditionName ?? "—";

    public string WorkspaceIndexDisplay =>
        _appState.CurrentImageWorkspace is { SelectedIndex: > 0 } ws ? ws.SelectedIndex.ToString() : "—";

    public string WorkspaceImageDisplay =>
        _appState.CurrentImageWorkspace?.ImageRelativePath is { } p
            ? System.IO.Path.GetFileName(p)
            : "—";

    public string WorkspaceArchitectureDisplay => _appState.CurrentImageWorkspace?.Architecture ?? "—";

    public string WorkspaceBuildDisplay => _appState.CurrentImageWorkspace?.Build ?? "—";

    public string WorkspaceSourceDisplay =>
        _appState.CurrentImageWorkspace?.SourceIsoPath is { } p ? System.IO.Path.GetFileName(p) : "—";

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

        // A new inspection supersedes any prior edition selection and any prior
        // durable workspace. Clearing the workspace first prevents a stale
        // selected index from the previous ISO from surviving into the new one.
        _appState.SelectedEdition = null;
        _appState.CurrentImageWorkspace = null;

        IsInspecting = true;
        _logger.Info("ISO inspection started.");
        try
        {
            var result = await _inspection.InspectAsync(path, CancellationToken.None);
            _result = result;
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

    private string TopLevelOr(
        string? consistent,
        Func<WindowsEditionInfo, string?> selector,
        string whenAbsent)
    {
        var md = _result?.ImageMetadata;
        if (md is null || md.Editions.Count == 0)
        {
            return whenAbsent;
        }

        if (consistent is not null)
        {
            return consistent;
        }

        return md.Editions.Any(e => !string.IsNullOrEmpty(selector(e))) ? "Mixed" : whenAbsent;
    }

    /// <summary>
    /// Builds/updates the durable <see cref="IAppState.CurrentImageWorkspace"/> for
    /// the given selected edition. A workspace is persisted only when the factory
    /// produces a ready descriptor and <see cref="IWimService.ValidateWorkspace"/>
    /// agrees; otherwise the workspace is cleared (including a rejected/invalid
    /// selection) so no stale or invalid index is retained.
    /// </summary>
    private void UpdateWorkspace(WindowsEditionInfo? selectedEdition)
    {
        ImageWorkspace? next = null;

        if (_result?.ImageMetadata is not null)
        {
            var built = _workspaceFactory.BuildWorkspace(_result, selectedEdition);
            if (built.IsReady && built.Workspace is not null &&
                _wimService.ValidateWorkspace(built.Workspace) == ImageWorkspaceStatus.Ready)
            {
                next = built.Workspace;
            }
        }

        _appState.CurrentImageWorkspace = next;
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
        OnPropertyChanged(nameof(WindowsVersionDisplay));
        OnPropertyChanged(nameof(BuildDisplay));
        OnPropertyChanged(nameof(ArchitectureDisplay));
        OnPropertyChanged(nameof(LanguagesDisplay));
        OnPropertyChanged(nameof(EditionsDisplay));
        OnPropertyChanged(nameof(Editions));
        OnPropertyChanged(nameof(SelectedEdition));
        OnPropertyChanged(nameof(Workspace));
        OnPropertyChanged(nameof(WorkspaceStatusDisplay));
        OnPropertyChanged(nameof(WorkspaceEditionDisplay));
        OnPropertyChanged(nameof(WorkspaceIndexDisplay));
        OnPropertyChanged(nameof(WorkspaceImageDisplay));
        OnPropertyChanged(nameof(WorkspaceArchitectureDisplay));
        OnPropertyChanged(nameof(WorkspaceBuildDisplay));
        OnPropertyChanged(nameof(WorkspaceSourceDisplay));
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
