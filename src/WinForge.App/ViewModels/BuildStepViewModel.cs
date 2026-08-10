using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using WinForge.App.Mvvm;
using WinForge.App.Services;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// Build step — drives the real Build / ISO export pipeline (Phase 10). It derives
/// every input (working image, mount dir, source edition, build workspace) from
/// shared <see cref="IAppState"/>, lets the user choose an output location and file
/// name, runs <see cref="IBuildService"/> behind a cancellable command, and surfaces
/// the explicit lifecycle state, progress, log, and final output path. The original
/// source ISO is never modified; the pipeline only reads it. Success is never
/// reported for a failed or cancelled build — the terminal <see cref="BuildState"/>
/// flows from <see cref="BuildResult.FinalState"/>.
/// </summary>
public sealed class BuildStepViewModel : ViewModelBase
{
    private readonly IAppState _appState;
    private readonly IBuildService _buildService;
    private readonly IFileSystem _fileSystem;
    private readonly IFilePicker _filePicker;
    private readonly IAdkToolLocator _adk;
    private readonly ILoggerService _logger;
    private readonly ILocalizationService _loc;
    private readonly IFileLauncher? _launcher;

    private CancellationTokenSource? _cts;
    private bool _isBuilding;
    private bool _defaultsInitialized;

    private string _sourceEdition = string.Empty;
    private string _finalEditionName = string.Empty;
    private string _outputDirectory = string.Empty;
    private string _outputFileName = string.Empty;
    private BuildState _currentStage = BuildState.NotStarted;
    private int _progressPercent = -1;
    private string _statusMessage = string.Empty;
    private string _logText = string.Empty;
    private string _outputPath = string.Empty;
    private long _outputSizeBytes;
    private bool _adkMissing;

    public BuildStepViewModel(
        IAppState appState,
        IBuildService buildService,
        IFileSystem fileSystem,
        IFilePicker filePicker,
        IAdkToolLocator adk,
        ILoggerService logger,
        ILocalizationService loc,
        IFileLauncher? launcher = null)
    {
        _appState = appState;
        _buildService = buildService;
        _fileSystem = fileSystem;
        _filePicker = filePicker;
        _adk = adk;
        _logger = logger;
        _loc = loc;
        _launcher = launcher;

        BuildCommand = new AsyncRelayCommand(_ => BuildAsync(), _ => CanBuild);
        CancelCommand = new RelayCommand(_ => CancelBuild(), _ => CanCancel);
        BrowseOutputCommand = new RelayCommand(_ => BrowseOutput(), _ => CanBrowse);
        OpenOutputFolderCommand = new RelayCommand(_ => OpenOutputFolder(), _ => CanOpenOutputFolder);

        // Proactive ADK detection so the UI can clearly tell the user when the
        // Windows ADK Deployment Tools are required, instead of failing mid-build.
        _adkMissing = !_adk.IsAvailable();

        _appState.PropertyChanged += OnAppStateChanged;
        InitializeDefaultsFromState();
        UpdateStatusBanner();
        Refresh();
    }

    public ICommand BuildCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand OpenOutputFolderCommand { get; }

    /// <summary>True when the Apply step finished (successfully or with errors).</summary>
    public bool HasApplied => _appState.CustomizationExecutionState is
        CustomizationExecutionState.Completed or CustomizationExecutionState.CompletedWithErrors;

    public bool IsMounted =>
        _appState.CurrentServicingWorkspace?.State == ServicingWorkspaceState.Mounted;

    /// <summary>
    /// True when a durable committed/exported build artifact exists from a prior run
    /// (the exported <c>install.wim</c> in the build workspace). After a successful
    /// Commit the working image is unmounted, so <see cref="IsMounted"/> is no longer
    /// true — but the build can still be resumed from that checkpoint without
    /// re-applying or re-committing. This keeps <see cref="CanBuild"/> usable after a
    /// post-commit PreparingMedia failure.
    /// </summary>
    public bool HasBuildCheckpoint
    {
        get
        {
            var ws = _appState.CurrentServicingWorkspace;
            if (ws is null)
            {
                return false;
            }

            var buildWs = !string.IsNullOrWhiteSpace(ws.WorkingDirectory)
                ? _fileSystem.PathCombine(ws.WorkingDirectory, "build")
                : _fileSystem.PathCombine(_fileSystem.GetTempPath(), "WinForge", "Build");
            var finalWim = _fileSystem.PathCombine(buildWs, "install.wim");
            return _fileSystem.FileExists(finalWim);
        }
    }

    public string SourceEdition
    {
        get => _sourceEdition;
        private set => SetField(ref _sourceEdition, value);
    }

    public string FinalEditionName
    {
        get => _finalEditionName;
        set => SetField(ref _finalEditionName, value);
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set => SetField(ref _outputDirectory, value);
    }

    public string OutputFileName
    {
        get => _outputFileName;
        set => SetField(ref _outputFileName, value);
    }

    public string BuildModeText => _loc["Build.Mode.Single"];

    public BuildState CurrentStage
    {
        get => _currentStage;
        private set => SetField(ref _currentStage, value);
    }

    public string CurrentStageText => _loc[$"Build.Stage.{_currentStage}"];

    public int ProgressPercent
    {
        get => _progressPercent;
        private set => SetField(ref _progressPercent, value);
    }

    public bool IsIndeterminate => _progressPercent < 0;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public string LogText
    {
        get => _logText;
        private set => SetField(ref _logText, value);
    }

    public string OutputPath
    {
        get => _outputPath;
        private set
        {
            if (!SetField(ref _outputPath, value))
            {
                return;
            }

            // Output existence (and thus the Open-folder affordance) depends on it.
            if (OpenOutputFolderCommand is RelayCommand open)
            {
                open.RaiseCanExecuteChanged();
            }
        }
    }

    public long OutputSizeBytes
    {
        get => _outputSizeBytes;
        private set => SetField(ref _outputSizeBytes, value);
    }

    public string OutputSizeText
    {
        get
        {
            if (_outputSizeBytes <= 0)
            {
                return string.Empty;
            }

            double bytes = _outputSizeBytes;
            return bytes >= 1 << 30 ? $"{bytes / (1 << 30):F2} GB" : $"{bytes / (1 << 20):F1} MB";
        }
    }

    /// <summary>True once a final ISO path has been produced (for output-panel visibility).</summary>
    public bool HasOutput => !string.IsNullOrEmpty(_outputPath);

    /// <summary>
    /// True only when a final ISO actually exists on disk, so the "Open output
    /// folder" affordance is never offered for a missing/partial artifact.
    /// </summary>
    public bool CanOpenOutputFolder => HasOutput && _fileSystem.FileExists(_outputPath);

    public bool AdkMissing
    {
        get => _adkMissing;
        private set => SetField(ref _adkMissing, value);
    }

    public bool IsBuilding
    {
        get => _isBuilding;
        private set
        {
            if (!SetField(ref _isBuilding, value))
            {
                return;
            }

            // Build/Cancel/Browse availability depends on this flag.
            RaiseBuildCommands();
        }
    }

    public bool CanBuild =>
        HasApplied && (IsMounted || HasBuildCheckpoint) && !IsBuilding && !AdkMissing &&
        !string.IsNullOrWhiteSpace(OutputDirectory) &&
        !string.IsNullOrWhiteSpace(OutputFileName) &&
        _appState.CurrentServicingWorkspace is not null;

    public bool CanCancel => IsBuilding;

    public bool CanBrowse => !IsBuilding;

    private void InitializeDefaultsFromState()
    {
        var ws = _appState.CurrentServicingWorkspace;
        if (ws is null)
        {
            return;
        }

        if (!_defaultsInitialized && !string.IsNullOrWhiteSpace(ws.SelectedEditionName))
        {
            SourceEdition = ws.SelectedEditionName;
            FinalEditionName = ws.SelectedEditionName;
            var def = BuildFileName.DefaultIsoName(ws.SelectedEditionName, DateTime.Now);
            OutputFileName = def.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)
                ? def.Substring(0, def.Length - 4)
                : def;
            _defaultsInitialized = true;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            OutputDirectory = _fileSystem.PathCombine(_fileSystem.GetTempPath(), "WinForge", "Output");
        }
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IAppState.CustomizationExecutionState)
            or nameof(IAppState.CurrentServicingWorkspace))
        {
            InitializeDefaultsFromState();
            UpdateStatusBanner();
            Refresh();
        }
    }

    /// <summary>
    /// Recomputes the prerequisite status banner from the live shared state. This
    /// is the core fix for the real-desktop defect where the Build page kept showing
    /// "please run Apply first" even after Apply completed successfully.
    ///
    /// <see cref="BuildStepViewModel"/> is a singleton: it is constructed once
    /// (before Apply runs), so <see cref="StatusMessage"/> was frozen at construction
    /// time. When Apply later flips <see cref="IAppState.CustomizationExecutionState"/>
    /// to Completed, <see cref="OnAppStateChanged"/> fires and we must actively CLEAR
    /// the stale warning — not merely set it when not-applied. Precedence: build in
    /// flight &gt; not-applied &gt; not-mounted &gt; ADK missing &gt; prerequisite satisfied.
    /// </summary>
    private void UpdateStatusBanner()
    {
        // While a build is in flight (or finishing) BuildAsync owns StatusMessage;
        // AppState changes must not clobber the live progress/result text.
        if (IsBuilding)
        {
            return;
        }

        if (!HasApplied)
        {
            StatusMessage = _loc["Build.Status.NeedsApply"];
        }
        else if (!IsMounted && !HasBuildCheckpoint)
        {
            StatusMessage = _loc["Build.Status.NeedsMount"];
        }
        else if (_adkMissing)
        {
            StatusMessage = _loc["Build.Status.AdapterMissing"];
        }
        else
        {
            // Apply prerequisite satisfied, image mounted, ADK present: no warning.
            StatusMessage = string.Empty;
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(HasApplied));
        OnPropertyChanged(nameof(IsMounted));
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanBrowse));
        OnPropertyChanged(nameof(CanOpenOutputFolder));
        OnPropertyChanged(nameof(CurrentStageText));
        OnPropertyChanged(nameof(IsIndeterminate));
        OnPropertyChanged(nameof(OutputSizeText));
        OnPropertyChanged(nameof(HasOutput));
        OnPropertyChanged(nameof(BuildModeText));
        RaiseBuildCommands();
    }

    private void RaiseBuildCommands()
    {
        if (BuildCommand is AsyncRelayCommand b)
        {
            b.RaiseCanExecuteChanged();
        }

        if (CancelCommand is RelayCommand c)
        {
            c.RaiseCanExecuteChanged();
        }

        if (BrowseOutputCommand is RelayCommand br)
        {
            br.RaiseCanExecuteChanged();
        }

        if (OpenOutputFolderCommand is RelayCommand open)
        {
            open.RaiseCanExecuteChanged();
        }
    }

    private void BrowseOutput()
    {
        var picked = _filePicker.PickFolder();
        if (!string.IsNullOrWhiteSpace(picked))
        {
            OutputDirectory = picked;
        }
    }

    private void OpenOutputFolder()
    {
        if (_launcher is null || string.IsNullOrEmpty(_outputPath))
        {
            return;
        }

        // Open the folder that contains the produced ISO; never the ISO itself.
        var dir = System.IO.Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            _launcher.OpenFolder(dir);
        }
    }

    private async Task BuildAsync()
    {
        if (!CanBuild || _appState.CurrentServicingWorkspace is null)
        {
            return;
        }

        var ws = _appState.CurrentServicingWorkspace;
        var sourceEdition = ws.SelectedEditionName;
        var finalEdition = string.IsNullOrWhiteSpace(FinalEditionName) ? sourceEdition : FinalEditionName;
        var safeBaseName = BuildFileName.SanitizeBaseName(OutputFileName);

        // Build workspace is a WinForge-owned subdir of the servicing session when
        // available (stable across runs for interrupted-build recovery), else a
        // temp location. It is deleted on clean success/failure/cancel.
        var buildWs = !string.IsNullOrWhiteSpace(ws.WorkingDirectory)
            ? _fileSystem.PathCombine(ws.WorkingDirectory, "build")
            : _fileSystem.PathCombine(_fileSystem.GetTempPath(), "WinForge", "Build");

        var request = new BuildRequest
        {
            SourceIsoPath = ws.SourceIsoPath ?? string.Empty,
            SourceImageRelativePath = ws.SourceImageRelativePath ?? string.Empty,
            SourceImageType = ws.SourceImageType,
            WorkingImagePath = ws.WorkingImagePath ?? string.Empty,
            MountDirectory = ws.MountDirectory ?? string.Empty,
            WorkingIndex = ws.WorkingIndex,
            SourceEditionName = sourceEdition,
            FinalEditionName = finalEdition,
            OutputDirectory = OutputDirectory,
            OutputFileName = safeBaseName,
            Mode = BuildMode.SingleCustomizedEdition,
            OverwritePolicy = BuildOverwritePolicy.GenerateUniqueName,
            BuildWorkspaceDirectory = buildWs
        };

        _cts = new CancellationTokenSource();
        IsBuilding = true;
        OutputPath = string.Empty;
        OutputSizeBytes = 0;
        LogText = string.Empty;
        ProgressPercent = -1;
        CurrentStage = BuildState.Preflight;
        StatusMessage = _loc["Build.Status.Starting"];
        Refresh();

        var progress = new Progress<BuildProgress>(p =>
        {
            CurrentStage = p.Phase;
            ProgressPercent = p.Percent;
            StatusMessage = p.Message;
            // Only append while still building: the final progress event can be
            // delivered after the build completes, and the terminal log is set
            // deterministically from the result below to avoid duplication.
            if (IsBuilding && p.Message.Length > 0)
            {
                LogText = string.IsNullOrEmpty(LogText)
                    ? p.Message
                    : LogText + Environment.NewLine + p.Message;
            }

            OnPropertyChanged(nameof(CurrentStageText));
            OnPropertyChanged(nameof(IsIndeterminate));
        });

        try
        {
            // The build service owns all resume/cleanup decisions (durable committed
            // and exported checkpoints, deterministic media-tree cleanup). The
            // ViewModel must not delete a durable checkpoint before the service runs.
            var result = await _buildService.BuildAsync(request, progress, _cts.Token);

            // The terminal state is the authority: never derive success from a flag.
            _appState.BuildStatus = result.FinalState;

            if (result.Success)
            {
                // Pin the final stage explicitly: progress events are delivered
                // asynchronously, so the last one may arrive after this point.
                CurrentStage = BuildState.Completed;
                OutputPath = result.OutputPath ?? string.Empty;
                OutputSizeBytes = result.OutputSizeBytes;
                if (string.IsNullOrEmpty(LogText) && result.Log.Count > 0)
                {
                    LogText = string.Join(Environment.NewLine, result.Log);
                }

                StatusMessage = _loc["Build.Status.Completed"];
                // The working image is now committed & unmounted; reflect reality.
                if (ws.State == ServicingWorkspaceState.Mounted)
                {
                    ws.State = ServicingWorkspaceState.Prepared;
                }
            }
            else
            {
                CurrentStage = result.FailedPhase ?? result.FinalState;
                var heading = result.FinalState == BuildState.Cancelled
                    ? _loc["Build.Status.Cancelled"]
                    : _loc["Build.Status.Failed"];
                StatusMessage = $"{heading}: {result.ErrorMessage}";
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                {
                    LogText = string.IsNullOrEmpty(LogText)
                        ? result.ErrorMessage
                        : LogText + Environment.NewLine + result.ErrorMessage;
                }
            }

            OnPropertyChanged(nameof(OutputSizeText));
        }
        catch (OperationCanceledException)
        {
            _appState.BuildStatus = BuildState.Cancelled;
            CurrentStage = BuildState.Cancelled;
            StatusMessage = _loc["Build.Status.Cancelled"];
        }
        catch (Exception ex)
        {
            _appState.BuildStatus = BuildState.Failed;
            StatusMessage = $"{_loc["Build.Status.Failed"]}: {ex.Message}";
            _logger.Error($"Build: UI error: {ex.Message}");
        }
        finally
        {
            IsBuilding = false;
            _cts?.Dispose();
            _cts = null;
            Refresh();
        }
    }

    private void CancelBuild()
    {
        if (!IsBuilding)
        {
            return;
        }

        _cts?.Cancel();
        StatusMessage = _loc["Build.Status.Cancelling"];
    }
}
