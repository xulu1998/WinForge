using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using WinForge.App.Mvvm;
using WinForge.Core.Compatibility;
using WinForge.App.Services;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Core.WorkspaceLifecycle;

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
    private readonly IWorkspaceLifecycleManager? _lifecycle;
    private readonly ILocalizationService? _loc;
    private readonly IImageCompatibilityService _compat;

    private IsoInspectionResult? _result;
    private WinForge.Core.Compatibility.ImageCompatibilityProfile? _compatibility;
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
        IImageServicingService servicing,
        ILocalizationService? loc = null,
        IWorkspaceLifecycleManager? lifecycle = null,
        IImageCompatibilityService? compat = null)
    {
        _appState = appState;
        _logger = logger;
        _inspection = inspection;
        _lifecycle = lifecycle;
        _filePicker = filePicker;
        _workspaceFactory = workspaceFactory;
        _wimService = wimService;
        _servicing = servicing;
        _compat = compat ?? new WinForge.Infrastructure.Compatibility.ImageCompatibilityService();
        _loc = loc;

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
        string.IsNullOrEmpty(_appState.SourceImagePath) ? L("Source.NoIsoSelected", "No ISO selected") : _appState.SourceImagePath;

    public string FileNameDisplay => _result?.FileName ?? "—";

    public string SizeDisplay => _result is null ? "—" : FormatSize(_result.FileSizeBytes);

    public string DetectedTypeDisplay => _result switch
    {
        null => L("Source.NoIsoSelected", "No ISO selected"),
        _ when _result.Status == IsoInspectionStatus.Failed => L("Source.UnableToInspect", "Unable to inspect ISO"),
        _ when _result.DetectedType == IsoDetectedType.WindowsIsoCandidate => L("Source.Detection.Candidate", "Windows ISO Candidate"),
        _ => L("Source.Detection.Unknown", "Unknown")
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
        L("Common.NotDetected", "Not detected"));

    public string BuildDisplay => TopLevelOr(
        _result?.ImageMetadata?.Build,
        e => e.Build,
        L("Common.NotDetected", "Not detected"));

    public string ArchitectureDisplay => TopLevelOr(
        _result?.ImageMetadata?.Architecture,
        e => e.Architecture,
        L("Common.NotDetected", "Not detected"));

    public string LanguagesDisplay
    {
        get
        {
            var md = _result?.ImageMetadata;
            if (md is null || md.Editions.Count == 0)
            {
                return L("Common.NotDetected", "Not detected");
            }

            if (md.Languages is { Count: > 0 })
            {
                return string.Join(", ", md.Languages);
            }

            return md.Editions.Any(e => e.Languages.Count > 0)
                ? L("Common.Mixed", "Mixed")
                : L("Common.NotDetected", "Not detected");
        }
    }

    public string EditionsDisplay =>
        _result?.ImageMetadata?.Editions.Count > 0
            ? $"{_result.ImageMetadata.Editions.Count} edition(s)"
            : L("Common.NotDetected", "Not detected");

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
                _blockedMessage = L("Error.UnmountBeforeEdition", "Unmount the working image before selecting a different edition.");
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
        _appState.CurrentImageWorkspace is null
            ? L("Workspace.Status.SelectEdition", "Select an edition")
            : L("Workspace.Status.Ready", "Ready");

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

    // ---- Phase 13 compatibility (evaluated after every ISO inspection) ----

    public WinForge.Core.Compatibility.ImageCompatibilityProfile? CompatibilityProfile
    {
        get => _compatibility;
        private set => SetField(ref _compatibility, value);
    }

    /// <summary>Concise preflight status line (e.g. "Windows 11 25H2 · Pro · x64 · zh-CN · WIM").</summary>
    public string CompatibilityStatusText
    {
        get
        {
            var p = _compatibility;
            if (p is null || string.IsNullOrWhiteSpace(p.EditionId))
            {
                return L("Compat.NotEvaluated", "兼容性未评估");
            }

            var release = p.Release switch
            {
                WinForge.Core.Compatibility.WindowsRelease.Windows11_25H2 => L("Compat.Release.Win11_25H2", "Windows 11 25H2"),
                WinForge.Core.Compatibility.WindowsRelease.Windows11_24H2 => L("Compat.Release.Win11_24H2", "Windows 11 24H2"),
                WinForge.Core.Compatibility.WindowsRelease.Windows11_UnknownNewer => L("Compat.Release.Win11_Newer", "Windows 11 新版本"),
                WinForge.Core.Compatibility.WindowsRelease.OlderWindows => L("Compat.Release.Older", "旧版 Windows"),
                _ => L("Compat.Release.Unknown", "未知版本"),
            };

            var status = p.Status switch
            {
                WinForge.Core.Compatibility.CompatibilityStatus.Supported => L("Compat.Status.Supported", "✓ 支持"),
                WinForge.Core.Compatibility.CompatibilityStatus.SupportedWithWarnings => L("Compat.Status.SupportedWithWarnings", "⚠ 支持（有警告）"),
                WinForge.Core.Compatibility.CompatibilityStatus.PartiallySupported => L("Compat.Status.Partial", "△ 部分支持"),
                WinForge.Core.Compatibility.CompatibilityStatus.Unsupported => L("Compat.Status.Unsupported", "✗ 不支持"),
                _ => L("Compat.Status.Unknown", "未知"),
            };

            return $"{release} · {status}";
        }
    }

    public bool HasCompatibilityWarnings => _compatibility?.HasWarnings ?? false;
    public bool HasCompatibilityBlockers => _compatibility?.HasBlockers ?? false;

    /// <summary>Localized blocking-finding summary (Stage 13.10).</summary>
    public string CompatibilityBlockersText
    {
        get
        {
            var blockers = _compatibility?.Findings.Where(f => f.IsBlocking).ToList();
            if (blockers is null || blockers.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(Environment.NewLine, blockers.Select(f => "✗ " + f.Message));
        }
    }
    public string CompatibilityDetailsText
    {
        get
        {
            var p = _compatibility;
            if (p is null)
            {
                return string.Empty;
            }

            var parts = new System.Collections.Generic.List<string>
            {
                $"Build: {p.Build?.ToString() ?? "?"}",
                $"Index: {p.SelectedIndex}/{p.ImageCount}",
                $"Edition: {p.EditionId ?? "?"}",
                $"Arch: {p.Architecture ?? "?"}",
                $"Format: {p.ImageFormat}",
                $"Lang: {string.Join(",", p.AvailableLanguages)}",
            };
            return string.Join("  ·  ", parts);
        }
    }

    public bool IsServicingMounted =>
        _appState.CurrentServicingWorkspace?.State == ServicingWorkspaceState.Mounted;

    public string ServicingStatusDisplay => _appState.CurrentServicingWorkspace?.State switch
    {
        null => L("Servicing.NotPrepared", "Not prepared"),
        ServicingWorkspaceState.NotPrepared => L("Servicing.NotPrepared", "Not prepared"),
        ServicingWorkspaceState.Preparing => L("Servicing.Preparing", "Preparing…"),
        ServicingWorkspaceState.Prepared => L("Servicing.Prepared", "Prepared"),
        ServicingWorkspaceState.Mounting => L("Servicing.Mounting", "Mounting…"),
        ServicingWorkspaceState.Mounted => L("Servicing.Mounted", "Mounted"),
        ServicingWorkspaceState.Unmounting => L("Servicing.Unmounting", "Unmounting…"),
        ServicingWorkspaceState.Completed => L("Servicing.Completed", "Unmounted"),
        ServicingWorkspaceState.Failed => L("Servicing.Failed", "Failed"),
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
            || _appState.CurrentServicingWorkspace!.State is ServicingWorkspaceState.Failed
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
            _blockedMessage = L("Error.UnmountBeforeIso", "Unmount the working image before selecting a different ISO.");
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
            CompatibilityProfile = _compat.Evaluate(result);
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
        ServicingMessage = L("Servicing.Msg.Preparing", "Preparing working image…");
        _blockedMessage = null;
        Refresh();
        try
        {
            var result = await _servicing.PrepareWorkingImageAsync(source, workspaceId, CancellationToken.None);
            _appState.CurrentServicingWorkspace = result.Workspace;
            ServicingMessage = result.Success
                ? L("Servicing.Msg.Prepared", "Working image prepared.")
                : (result.ErrorMessage ?? L("Servicing.Msg.PrepareFailed", "Preparation failed."));
        }
        catch (Exception ex)
        {
            _appState.CurrentServicingWorkspace = null;
            ServicingMessage = L("Servicing.Msg.PrepareFailedUnexpected", "Preparation failed unexpectedly.");
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
        ServicingMessage = L("Servicing.Msg.Mounting", "Mounting working image…");
        Refresh();
        try
        {
            var result = await _servicing.MountAsync(workspace, CancellationToken.None);
            _appState.CurrentServicingWorkspace = result.Workspace;
            ServicingMessage = result.Success
                ? L("Servicing.Msg.Mounted", "Working image mounted.")
                : (result.ErrorMessage ?? L("Servicing.Msg.MountFailed", "Mount failed."));
        }
        catch (Exception ex)
        {
            ServicingMessage = L("Servicing.Msg.MountFailedUnexpected", "Mount failed unexpectedly.");
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
        ServicingMessage = L("Servicing.Msg.Unmounting", "Unmounting working image…");
        Refresh();
        try
        {
            var result = await _servicing.UnmountDiscardAsync(workspace, CancellationToken.None);
            _appState.CurrentServicingWorkspace = result.Workspace;
            ServicingMessage = result.Success
                ? L("Servicing.Msg.Unmounted", "Working image unmounted (changes discarded).")
                : (result.ErrorMessage ?? L("Servicing.Msg.UnmountFailed", "Unmount failed."));
            // Stage 12.2 Part E: after a successful discard the disposable workspace
            // is cleaned immediately (background, safe) so cancelled test runs never
            // accumulate; failures surface in the Storage cleanup UI instead.
            if (result.Success && _lifecycle is not null &&
                result.Workspace is { WorkingDirectory: { } wd } && !string.IsNullOrWhiteSpace(wd))
            {
                var id = System.IO.Path.GetFileName(wd.TrimEnd('\\', '/')) ?? string.Empty;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _lifecycle.CleanupCompletedWorkspaceAsync(id);
                    }
                    catch
                    {
                        // best effort — Storage UI remains the retry surface
                    }
                });
            }
        }
        catch (Exception ex)
        {
            ServicingMessage = L("Servicing.Msg.UnmountFailedUnexpected", "Unmount failed unexpectedly.");
            _logger.Error($"Servicing unmount failed: {ex.Message}");
        }
        finally
        {
            IsServicing = false;
            Refresh();
        }
    }

    /// <summary>
    /// Resolves a localized string by key, falling back to <paramref name="fallback"/>
    /// when no localization service is available (e.g. in unit tests) or the key
    /// is missing. Keeps the view model usable without an injected service.
    /// </summary>
    private string L(string key, string fallback) => _loc is null ? fallback : (_loc[key] ?? fallback);

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

        return md.Editions.Any(e => !string.IsNullOrEmpty(selector(e)))
            ? L("Common.Mixed", "Mixed")
            : whenAbsent;
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
        OnPropertyChanged(nameof(CompatibilityProfile));
        OnPropertyChanged(nameof(CompatibilityStatusText));
        OnPropertyChanged(nameof(HasCompatibilityWarnings));
        OnPropertyChanged(nameof(HasCompatibilityBlockers));
        OnPropertyChanged(nameof(CompatibilityDetailsText));
        OnPropertyChanged(nameof(CompatibilityBlockersText));
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

        // CRITICAL: a WPF ICommand bound to a Button only re-queries CanExecute
        // when the COMMAND raises CanExecuteChanged. Raising PropertyChanged on the
        // Can* properties above is NOT enough — without this, the buttons stay
        // disabled even after the underlying state flips to ready. This was the
        // Step 3.2 real-desktop defect: after ISO inspection + edition selection
        // the Prepare command's CanExecute became true, but the binding never
        // re-evaluated it because CanExecuteChanged was never raised.
        if (PrepareWorkingImageCommand is AsyncRelayCommand prepare)
        {
            prepare.RaiseCanExecuteChanged();
        }

        if (MountWorkingImageCommand is AsyncRelayCommand mount)
        {
            mount.RaiseCanExecuteChanged();
        }

        if (UnmountDiscardCommand is AsyncRelayCommand unmount)
        {
            unmount.RaiseCanExecuteChanged();
        }
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
