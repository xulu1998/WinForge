using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using WinForge.App.Mvvm;
using WinForge.App.Services;
using WinForge.Core.Services;
using WinForge.Core.WorkspaceLifecycle;

namespace WinForge.App.ViewModels;

/// <summary>
/// Phase 12 disk-usage surface (Parts H/I/Q): async, cancellable scan of every
/// wf-* workspace under the WinForge workspace root; safe-cleanup preview and
/// execution. The component list never blocks the UI — all directory-size
/// measurement runs on the thread pool with a cancellation token.
/// </summary>
public sealed class StorageViewModel : ViewModelBase
{
    private readonly IWorkspaceLifecycleManager _lifecycle;
    private readonly ILocalizationService _loc;
    private readonly IWorkspaceRootSettingsService? _rootSettings;
    private readonly IFilePicker? _filePicker;
    private readonly IAppState? _appState;
    private bool _isScanning;
    private bool _isCleaning;
    private bool _hasScanned;
    private long _totalBytes;
    private long _activeBytes;
    private long _recoverableBytes;
    private long _disposableBytes;
    private string _cleanResultText = string.Empty;
    private string _rootErrorText = string.Empty;

    public StorageViewModel(
        IWorkspaceLifecycleManager lifecycle,
        ILocalizationService loc,
        IWorkspaceRootSettingsService? rootSettings = null,
        IFilePicker? filePicker = null,
        IAppState? appState = null)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));
        _rootSettings = rootSettings;
        _filePicker = filePicker;
        _appState = appState;

        ScanCommand = new RelayCommand(_ => _ = ScanAsync());
        CleanCommand = new AsyncRelayCommand(_ => CleanAsync(), _ => !IsCleaning && CleanupCandidates.Count > 0);
        ChangeRootCommand = new RelayCommand(_ => ChangeRoot());
        RestoreDefaultCommand = new RelayCommand(_ => RestoreDefault());
    }

    public ObservableCollection<StorageCandidateItem> CleanupCandidates { get; } = new();

    public ICommand ScanCommand { get; }

    public ICommand CleanCommand { get; }

    /// <summary>Opens a folder picker and switches the workspace root (Part A).</summary>
    public ICommand ChangeRootCommand { get; }

    /// <summary>Restores the default workspace root (Part A).</summary>
    public ICommand RestoreDefaultCommand { get; }

    public bool IsScanning
    {
        get => _isScanning;
        private set => SetField(ref _isScanning, value);
    }

    public bool IsCleaning
    {
        get => _isCleaning;
        private set => SetField(ref _isCleaning, value);
    }

    public bool HasScanned
    {
        get => _hasScanned;
        private set => SetField(ref _hasScanned, value);
    }

    public bool HasCandidates => CleanupCandidates.Count > 0;

    public string TotalBytesText => DiskSpaceEstimator.FormatBytes(_totalBytes);
    public string ActiveBytesText => DiskSpaceEstimator.FormatBytes(_activeBytes);
    public string RecoverableBytesText => DiskSpaceEstimator.FormatBytes(_recoverableBytes);
    public string DisposableBytesText => DiskSpaceEstimator.FormatBytes(_disposableBytes);

    // Stage 12.2 fix (REAL DESKTOP BLOCKER): the Storage page used to compose the
    // usage line from multiple <Run> inlines inside XAML. WPF throws while setting
    // System.Windows.Documents.Run.Text when the inlines are materialized during
    // layout of a previously-collapsed TextBlock, producing global error dialogs
    // on the real desktop. The UI now binds a single fully-formatted string from
    // the view model (stable TextBlock.Text binding), keeping localization intact.
    public string ActiveLabel => _loc["Storage.Active"];
    public string RecoverableLabel => _loc["Storage.Recoverable"];
    public string DisposableLabel => _loc["Storage.Disposable"];

    /// <summary>Single-line usage summary (active · recoverable · disposable).</summary>
    public string UsageSummaryText => string.Format("{0} — {1} · {2} — {3} · {4} — {5}",
        ActiveBytesText, ActiveLabel,
        RecoverableBytesText, RecoverableLabel,
        DisposableBytesText, DisposableLabel);

    public string CleanResultText
    {
        get => _cleanResultText;
        private set => SetField(ref _cleanResultText, value);
    }

    // ---- Stage 12.2 — workspace root (Part A/B/F) ----

    public string CurrentRootText => _rootSettings?.CurrentRoot ?? _lifecycle.WorkspaceRoot;

    public string RootFreeSpaceText
    {
        get
        {
            try
            {
                var root = CurrentRootText;
                var drive = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(root));
                if (!string.IsNullOrWhiteSpace(drive) && System.IO.Directory.Exists(drive))
                {
                    return string.Format(_loc["Storage.Root.Free"],
                        DiskSpaceEstimator.FormatBytes(new System.IO.DriveInfo(drive).AvailableFreeSpace));
                }
            }
            catch
            {
                // best effort
            }

            return string.Empty;
        }
    }

    public bool RootLowSpaceWarning => RootFreeBytes < 10L * 1024 * 1024 * 1024;

    public string RootLowSpaceWarningText => string.Format(_loc["Storage.Root.Warning"], RootFreeSpaceText);

    public string RootErrorText
    {
        get => _rootErrorText;
        private set => SetField(ref _rootErrorText, value);
    }

    private long RootFreeBytes
    {
        get
        {
            try
            {
                var drive = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(CurrentRootText));
                if (!string.IsNullOrWhiteSpace(drive) && System.IO.Directory.Exists(drive))
                {
                    return new System.IO.DriveInfo(drive).AvailableFreeSpace;
                }
            }
            catch
            {
                // best effort
            }

            return long.MaxValue;
        }
    }

    /// <summary>
    /// Applies a candidate root (testable, no dialog). Part A: rejects an actively
    /// mounted session, validates the path, persists the change, and affects new
    /// workflows only — existing workspaces are never moved.
    /// </summary>
    public bool TrySetRoot(string candidate)
    {
        RootErrorText = string.Empty;
        if (_rootSettings is null)
        {
            return false;
        }

        // Part B: never switch the root while the current image is mounted.
        if (_appState?.CurrentServicingWorkspace?.State == WinForge.Core.Models.ServicingWorkspaceState.Mounted)
        {
            RootErrorText = _loc["Storage.Root.Mounted"];
            return false;
        }

        if (!_rootSettings.SetCurrentRoot(candidate, out var errorKey))
        {
            RootErrorText = _loc[errorKey ?? "Storage.Root.Invalid"];
            return false;
        }

        OnPropertyChanged(nameof(CurrentRootText));
        OnPropertyChanged(nameof(RootFreeSpaceText));
        OnPropertyChanged(nameof(RootLowSpaceWarning));
        OnPropertyChanged(nameof(RootLowSpaceWarningText));
        return true;
    }

    private void ChangeRoot()
    {
        if (_filePicker is null)
        {
            return;
        }

        var picked = _filePicker.PickFolder();
        if (string.IsNullOrWhiteSpace(picked))
        {
            return; // user cancelled
        }

        TrySetRoot(picked);
    }

    private void RestoreDefault()
    {
        _rootSettings?.RestoreDefault();
        OnPropertyChanged(nameof(CurrentRootText));
        OnPropertyChanged(nameof(RootFreeSpaceText));
        OnPropertyChanged(nameof(RootLowSpaceWarning));
        OnPropertyChanged(nameof(RootLowSpaceWarningText));
    }

    /// <param name="clearResultText">
    /// When true (user-initiated scan) the previous cleanup result line is reset.
    /// CleanAsync refreshes sizes with false so the "已清理 X" result the user
    /// just saw is NOT wiped by the follow-up re-scan.
    /// </param>
    public async Task ScanAsync(bool clearResultText = true)
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        if (clearResultText)
        {
            CleanResultText = string.Empty;
        }
        try
        {
            var classified = await _lifecycle.ClassifyAllAsync();
            CleanupCandidates.Clear();
            long total = 0, active = 0, recoverable = 0, disposable = 0;
            foreach (var c in classified)
            {
                var size = await _lifecycle.MeasureDirectorySizeAsync(c.WorkspaceDirectory);
                total += size;
                switch (c.Classification)
                {
                    case WorkspaceClassification.Active:
                        active += size;
                        break;
                    case WorkspaceClassification.Recoverable:
                        recoverable += size;
                        break;
                    case WorkspaceClassification.Disposable:
                    case WorkspaceClassification.LegacyUnknown:
                        disposable += size;
                        CleanupCandidates.Add(new StorageCandidateItem(c.WorkspaceId, c.WorkspaceDirectory, size, c.Classification));
                        break;
                }
            }

            _totalBytes = total;
            _activeBytes = active;
            _recoverableBytes = recoverable;
            _disposableBytes = disposable;
            HasScanned = true;
            RefreshSummary();
        }
        finally
        {
            IsScanning = false;
            if (CleanCommand is AsyncRelayCommand c)
            {
                c.RaiseCanExecuteChanged();
            }
        }
    }

    public async Task CleanAsync()
    {
        if (IsCleaning)
        {
            return;
        }

        IsCleaning = true;
        try
        {
            long reclaimed = 0;
            foreach (var candidate in CleanupCandidates.ToList())
            {
                var result = await _lifecycle.CleanupWorkspaceAsync(candidate.WorkspaceId);
                if (result.Succeeded)
                {
                    reclaimed += result.BytesReclaimed;
                    CleanupCandidates.Remove(candidate);
                }
            }

            CleanResultText = string.Format(_loc["Storage.Cleaned"], DiskSpaceEstimator.FormatBytes(reclaimed));
            await ScanAsync(clearResultText: false); // refresh remaining sizes, keep the result line
        }
        finally
        {
            IsCleaning = false;
            if (CleanCommand is AsyncRelayCommand c)
            {
                c.RaiseCanExecuteChanged();
            }
        }
    }

    private void RefreshSummary()
    {
        OnPropertyChanged(nameof(TotalBytesText));
        OnPropertyChanged(nameof(ActiveBytesText));
        OnPropertyChanged(nameof(RecoverableBytesText));
        OnPropertyChanged(nameof(DisposableBytesText));
        OnPropertyChanged(nameof(ActiveLabel));
        OnPropertyChanged(nameof(RecoverableLabel));
        OnPropertyChanged(nameof(DisposableLabel));
        OnPropertyChanged(nameof(UsageSummaryText));
        OnPropertyChanged(nameof(HasScanned));
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(CleanResultText));
        OnPropertyChanged(nameof(CurrentRootText));
        OnPropertyChanged(nameof(RootFreeSpaceText));
        OnPropertyChanged(nameof(RootLowSpaceWarning));
        OnPropertyChanged(nameof(RootLowSpaceWarningText));
        OnPropertyChanged(nameof(RootErrorText));
    }
}

/// <summary>One safe-cleanup candidate row in the storage surface.</summary>
public sealed class StorageCandidateItem
{
    public StorageCandidateItem(string workspaceId, string directory, long bytes, WorkspaceClassification classification)
    {
        WorkspaceId = workspaceId;
        Directory = directory;
        Bytes = bytes;
        Classification = classification;
    }

    public string WorkspaceId { get; }
    public string Directory { get; }
    public long Bytes { get; }
    public WorkspaceClassification Classification { get; }

    public string SizeText => DiskSpaceEstimator.FormatBytes(Bytes);
    public bool IsLegacy => Classification == WorkspaceClassification.LegacyUnknown;
}
