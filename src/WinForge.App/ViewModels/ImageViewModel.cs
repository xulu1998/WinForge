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
/// and the Windows image metadata: version, build, architecture, languages, and
/// the list of editions. Edition selection creates the durable selected-image
/// <see cref="IAppState.CurrentImageWorkspace"/>. Phase 3 Step 3.2 then lets the
/// user prepare an isolated working image, mount it, and discard the unmount —
/// all through <see cref="IImageServicingService"/>. All platform work (file
/// dialog, ISO mount, DISM) is reached through abstractions; this ViewModel never
/// touches WPF dialogs, the registry, or <c>Process</c> directly.
/// </summary>
public sealed class ImageViewModel : ViewModelBase
{
    private readonly IAppState _appState;
    private readonly ILoggerService _logger;
    private readonly IIsoInspectionService _inspection;
    private readonly IFilePicker _filePicker;
    private readonly IImageWorkspaceFactory _workspaceFactory;
    private readonly IWimService _wimService;
    private readonly IImageServicingService _servicing;

    private IsoInspectionResult? _result;
    private bool _isInspecting;
    private bool _isServicing;
    private string _servicingMessage = string.Empty;
    private string? _blockedMessage;

    public ImageViewModel(
        IAppState appState,
        ILoggerService logger,
        IIsoInspectionService inspection,
        IFilePicker filePicker,
        IImageWorkspaceFactory workspaceFactory,
        IWimService wimService,
        IImageServicingService servicing)
    {
        _appState = appState;
        _logger = logger;
        _inspection = inspection;
        _filePicker = filePicker;
        _workspaceFactory = workspaceFactory;
        _wimService = wimService;
        _servicing = servicing;

        SelectIsoCommand = new AsyncRelayCommand(_ => SelectIsoAsync());
        InspectIsoCommand = new AsyncRelayCommand(_ => InspectCurrentAsync());
        PrepareWorkingImageCommand = new AsyncRelayCommand(
            _ => PrepareWorkingImageAsync(), _ => CanPrepareWorkingImage);
        MountWorkingImageCommand = new AsyncRelayCommand(
            _ => MountWorkingImageAsync(), _ => CanMountWorkingImage);
        UnmountDiscardCommand = new AsyncRelayCommand(
            _ => UnmountDiscardAsync(), _ => CanUnmountDiscard);
    }

    public ICommand SelectIsoCommand { get; }

    public ICommand InspectIsoCommand { get; }

    public ICommand PrepareWorkingImageCommand { get; }

    public ICommand MountWorkingImageCommand { get; }

    public ICommand UnmountDiscardCommand { get; }

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
    /// <see cref="IAppState.SelectedEdition"/> so the Home page reflects it, creates
    /// /updates the durable <see cref="IAppState.CurrentImageWorkspace"/> for the
    /// selected index, and INVALIDATES any prepared (not mounted) servicing
    /// workspace so a stale working image from a previous edition cannot linger.
    /// When an active mount exists, the change is REFUSED with an explanatory
    /// message — the user must unmount first. This never silently destroys an
    /// active servicing session.
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

            if (IsServicingMounted)
            {
                _blockedMessage = "Unmount the working image before selecting a different edition.";
                Refresh();
                return;
            }

            _appState.SelectedEdition = value;
            UpdateWorkspace(value);
            InvalidatePreparedServicingWorkspace();
            Refresh();
        }
    }

    // ---- Step 3.1 — durable selected-image workspace ----

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

    // ---- Step 3.2 — offline servicing lifecycle ----

    public ImageServicingWorkspace? Servicing => _appState.CurrentServicingWorkspace;

    /// <summary>
    /// True while a DISM-backed servicing operation (prepare / mount / unmount) is
    /// running, so the UI can show a busy state and disable controls.
    /// </summary>
    public bool IsServicing
    {
        get => _isServicing;
        private set => SetField(ref _isServicing, value);
    }

    public string ServicingMessage
    {
        get => _servicingMessage;
        private set => SetField(ref _servicingMessage, value);
    }

    public string? BlockedMessage
    {
        get => _blockedMessage;
        private set => SetField(ref _blockedMessage, value);
    }

    public bool IsServicingMounted =>
        _appState.CurrentServicingWorkspace?.State == ServicingWorkspaceState.Mounted;

    public string ServicingStatusDisplay => _appState.CurrentServicingWorkspace?.State switch
    {
        null => "Not prepared",
        ServicingWorkspaceState.NotPrepared => "Not prepared",
        ServicingWorkspaceState.Preparing => "Preparing…",
        ServicingWorkspaceState.Prepared => "Prepared",
        ServicingWorkspaceState.Mounting => "Mounting…",
        ServicingWorkspaceState.Mounted => "Mounted",
        ServicingWorkspaceState.Unmounting => "Unmounting…",
        ServicingWorkspaceState.Completed => "Unmounted",
        ServicingWorkspaceState.Failed => "Failed",
        _ => "—"
    };

    public string ServicingSourceEditionDisplay => _appState.CurrentServicingWorkspace?.SelectedEditionName ?? "—";

    public string ServicingSourceIndexDisplay =>
        _appState.CurrentServicingWorkspace is { SelectedIndex: > 0 } s ? s.SelectedIndex.ToString() : "—";

    public string ServicingWorkingImageDisplay => _appState.CurrentServicingWorkspace?.WorkingImagePath is { } p
        ? System.IO.Path.GetFileName(p) : "—";

    public string ServicingWorkingIndexDisplay =>
        _appState.CurrentServicingWorkspace is { WorkingIndex: > 0 } s ? s.WorkingIndex.ToString() : "—";

    public string ServicingWorkingDirectoryDisplay =>
        _appState.CurrentServicingWorkspace?.WorkingDirectory ?? "—";

    public string ServicingMountDirectoryDisplay =>
        _appState.CurrentServicingWorkspace?.MountDirectory ?? "—";

    public string ServicingErrorDisplay => _appState.CurrentServicingWorkspace?.LastError ?? string.Empty;

    public bool CanPrepareWorkingImage =>
        !IsServicing
        && _appState.CurrentImageWorkspace is { } ws
        && _wimService.ValidateWorkspace(ws) == ImageWorkspaceStatus.Ready
        && (_appState.CurrentServicingWorkspace is null
            || _appState.CurrentServicingWorkspace!.State is ServicingWorkspaceState.Prepared
                or ServicingWorkspaceState.Failed
                or ServicingWorkspaceState.NotPrepared);

    public bool CanMountWorkingImage =>
        !IsServicing
        && _appState.CurrentServicingWorkspace is { State: ServicingWorkspaceState.Prepared };

    public bool CanUnmountDiscard =>
        !IsServicing
        && _appState.CurrentServicingWorkspace is { State: ServicingWorkspaceState.Mounted };

    public async Task SelectIsoAsync()
    {
        var path = _filePicker.PickIsoFile();
        if (path is null)
        {
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

        if (IsServicingMounted)
        {
            // Do NOT silently forget an active mount: refuse the new ISO and tell
            // the user to unmount first.
            _blockedMessage = "Unmount the working image before selecting a different ISO.";
            Refresh();
            return;
        }

        // A new inspection supersedes any prior edition selection and any prior
        // durable workspace (and any prepared, non-mounted servicing workspace).
        _appState.SelectedEdition = null;
        _appState.CurrentImageWorkspace = null;
        InvalidatePreparedServicingWorkspace();

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

    public async Task PrepareWorkingImageAsync()
    {
        if (!CanPrepareWorkingImage)
        {
            return;
        }

        var source = _appState.CurrentImageWorkspace!;
        var workspaceId = "wf-" + System.Guid.NewGuid().ToString("N").Substring(0, 12);

        IsServicing = true;
        ServicingMessage = "Preparing working image…";
        _blockedMessage = null;
        Refresh();
        try
        {
            var result = await _servicing.PrepareWorkingImageAsync(source, workspaceId, CancellationToken.None);
            _appState.CurrentServicingWorkspace = result.Workspace;
            ServicingMessage = result.Success
                ? "Working image prepared."
                : (result.ErrorMessage ?? "Preparation failed.");
        }
        catch (Exception ex)
        {
            _appState.CurrentServicingWorkspace = null;
            ServicingMessage = "Preparation failed unexpectedly.";
            _logger.Error($"Servicing prepare failed: {ex.Message}");
        }
        finally
        {
            IsServicing = false;
            Refresh();
        }
    }

    public async Task MountWorkingImageAsync()
    {
        if (!CanMountWorkingImage)
        {
            return;
        }

        var workspace = _appState.CurrentServicingWorkspace!;
        IsServicing = true;
        ServicingMessage = "Mounting working image…";
        Refresh();
        try
        {
            var result = await _servicing.MountAsync(workspace, CancellationToken.None);
            _appState.CurrentServicingWorkspace = result.Workspace;
            ServicingMessage = result.Success
                ? "Working image mounted."
                : (result.ErrorMessage ?? "Mount failed.");
        }
        catch (Exception ex)
        {
            ServicingMessage = "Mount failed unexpectedly.";
            _logger.Error($"Servicing mount failed: {ex.Message}");
        }
        finally
        {
            IsServicing = false;
            Refresh();
        }
    }

    public async Task UnmountDiscardAsync()
    {
        if (!CanUnmountDiscard)
        {
            return;
        }

        var workspace = _appState.CurrentServicingWorkspace!;
        IsServicing = true;
        ServicingMessage = "Unmounting working image…";
        Refresh();
        try
        {
            var result = await _servicing.UnmountDiscardAsync(workspace, CancellationToken.None);
            _appState.CurrentServicingWorkspace = result.Workspace;
            ServicingMessage = result.Success
                ? "Working image unmounted (changes discarded)."
                : (result.ErrorMessage ?? "Unmount failed.");
        }
        catch (Exception ex)
        {
            ServicingMessage = "Unmount failed unexpectedly.";
            _logger.Error($"Servicing unmount failed: {ex.Message}");
        }
        finally
        {
            IsServicing = false;
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
    /// Builds/updates the durable selected-image workspace for the given edition.
    /// A workspace is persisted only when the factory produces a ready descriptor
    /// and <see cref="IWimService.ValidateWorkspace"/> agrees.
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

    /// <summary>
    /// Invalidates a prepared (not mounted) servicing workspace when the source
    /// ISO or selected edition changes. An actively mounted session is never
    /// invalidated here — that requires an explicit unmount by the user.
    /// </summary>
    private void InvalidatePreparedServicingWorkspace()
    {
        var s = _appState.CurrentServicingWorkspace;
        if (s is null)
        {
            return;
        }

        if (s.State is ServicingWorkspaceState.Mounted or ServicingWorkspaceState.Unmounting
            or ServicingWorkspaceState.Mounting)
        {
            return;
        }

        _appState.CurrentServicingWorkspace = null;
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
        OnPropertyChanged(nameof(Servicing));
        OnPropertyChanged(nameof(IsServicing));
        OnPropertyChanged(nameof(ServicingMessage));
        OnPropertyChanged(nameof(BlockedMessage));
        OnPropertyChanged(nameof(IsServicingMounted));
        OnPropertyChanged(nameof(ServicingStatusDisplay));
        OnPropertyChanged(nameof(ServicingSourceEditionDisplay));
        OnPropertyChanged(nameof(ServicingSourceIndexDisplay));
        OnPropertyChanged(nameof(ServicingWorkingImageDisplay));
        OnPropertyChanged(nameof(ServicingWorkingIndexDisplay));
        OnPropertyChanged(nameof(ServicingWorkingDirectoryDisplay));
        OnPropertyChanged(nameof(ServicingMountDirectoryDisplay));
        OnPropertyChanged(nameof(ServicingErrorDisplay));
        OnPropertyChanged(nameof(CanPrepareWorkingImage));
        OnPropertyChanged(nameof(CanMountWorkingImage));
        OnPropertyChanged(nameof(CanUnmountDiscard));
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
