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
    private bool _isScanning;
    private bool _isCleaning;
    private bool _hasScanned;
    private long _totalBytes;
    private long _activeBytes;
    private long _recoverableBytes;
    private long _disposableBytes;
    private string _cleanResultText = string.Empty;

    public StorageViewModel(
        IWorkspaceLifecycleManager lifecycle,
        ILocalizationService loc)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));

        ScanCommand = new RelayCommand(_ => _ = ScanAsync());
        CleanCommand = new AsyncRelayCommand(_ => CleanAsync(), _ => !IsCleaning && CleanupCandidates.Count > 0);
    }

    public ObservableCollection<StorageCandidateItem> CleanupCandidates { get; } = new();

    public ICommand ScanCommand { get; }

    public ICommand CleanCommand { get; }

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

    public string CleanResultText
    {
        get => _cleanResultText;
        private set => SetField(ref _cleanResultText, value);
    }

    public async Task ScanAsync()
    {
        if (IsScanning)
        {
            return;
        }

        IsScanning = true;
        CleanResultText = string.Empty;
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
            await ScanAsync(); // refresh remaining sizes
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
        OnPropertyChanged(nameof(HasScanned));
        OnPropertyChanged(nameof(HasCandidates));
        OnPropertyChanged(nameof(CleanResultText));
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
