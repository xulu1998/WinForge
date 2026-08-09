using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.WimEngine;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Regression coverage for the Step 3.2 real-desktop defect where the
/// "Prepare working image" command stayed greyed out even after a Ready selected
/// image existed.
///
/// Root cause: <see cref="AsyncRelayCommand"/> only re-evaluates CanExecute when
/// it raises <see cref="System.Windows.Input.ICommand.CanExecuteChanged"/>; it does
/// NOT subscribe to WPF's CommandManager.RequerySuggested. The ViewModel raised
/// PropertyChanged on the Can* properties, but a Button bound to the Command only
/// listens to the command's CanExecuteChanged event — so the cached disabled
/// state was never refreshed. The fix raises CanExecuteChanged on every Refresh().
///
/// These tests drive the REAL flow (no image -> inspect -> select edition ->
/// Ready) and assert both the live CanExecute value AND that CanExecuteChanged
/// actually fired, mirroring what the WPF binding caches.
/// </summary>
public class ImageViewModelPrepareEnableTests
{
    private static ImageWorkspace ReadyWorkspace()
        => new ImageWorkspace
        {
            SourceIsoPath = @"C:\images\win.iso",
            ImageRelativePath = @"sources\install.wim",
            ImageType = WindowsImageType.Wim,
            SelectedIndex = 4,
            SelectedEditionName = "Windows 11 Pro",
            Architecture = "x64",
            Build = "26200"
        };

    private static ImageViewModel BuildVm(
        AppState state,
        IIsoInspectionService inspection,
        IFilePicker picker,
        IImageServicingService servicing)
        => new ImageViewModel(
            state,
            new InMemoryLoggerService(),
            inspection,
            picker,
            new ImageWorkspaceFactory(),
            new WimService(),
            servicing);

    private sealed class NoOpFilePicker : IFilePicker
    {
        public string? NextPath { get; set; }

        public string? PickIsoFile() => NextPath;
    }

    /// <summary>A successful ISO inspection returning metadata with two editions.</summary>
    private sealed class CompletedInspection : IIsoInspectionService
    {
        public IReadOnlyList<WindowsEditionInfo> Editions { get; } = new List<WindowsEditionInfo>
        {
            new() { Index = 1, Name = "Windows 11 Home", Architecture = "x64", Build = "26200", Version = "10.0.26200.1" },
            new() { Index = 4, Name = "Windows 11 Pro", Architecture = "x64", Build = "26200", Version = "10.0.26200.1" }
        };

        public Task<IsoInspectionResult> InspectAsync(string isoPath, CancellationToken cancellationToken = default)
        {
            var result = new IsoInspectionResult
            {
                IsoPath = isoPath,
                FileName = System.IO.Path.GetFileName(isoPath),
                Exists = true,
                ExtensionValid = true,
                IsReadable = true,
                DetectedType = IsoDetectedType.WindowsIsoCandidate,
                HasSourcesDirectory = true,
                HasInstallWim = true,
                InstallImageType = InstallImageType.Wim,
                Status = IsoInspectionStatus.Completed,
                ImageMetadata = new WindowsImageMetadataResult
                {
                    ImagePath = isoPath,
                    ImageType = WindowsImageType.Wim,
                    Status = WindowsImageMetadataStatus.Completed,
                    Architecture = "x64",
                    Build = "26200",
                    Version = "10.0.26200.1",
                    Editions = new List<WindowsEditionInfo>(Editions)
                }
            };
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// Servicing fake that blocks inside Prepare until the test releases it, so
    /// the mid-operation (busy) state can be observed. It is genuinely async so
    /// the blocking await runs on a pool thread and does NOT deadlock the test
    /// thread that kick-started the ViewModel operation.
    /// </summary>
    private sealed class HoldServicingService : IImageServicingService
    {
        public bool Started;
        public TaskCompletionSource<int>? Release;
        public ServicingResult? PrepareResult;

        public async Task<ServicingResult> PrepareWorkingImageAsync(
            ImageWorkspace source, string workspaceId, CancellationToken cancellationToken = default)
        {
            Started = true;
            if (Release is not null)
            {
                await Release.Task.ConfigureAwait(false);
            }

            return PrepareResult!;
        }

        public Task<ServicingResult> MountAsync(
            ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
            => Task.FromResult(ServicingResult.Ok(workspace, ServicingHealth.Mounted));

        public Task<ServicingResult> UnmountDiscardAsync(
            ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
            => Task.FromResult(ServicingResult.Ok(workspace, ServicingHealth.Prepared));

        public Task<ServicingResult> ValidateServicingWorkspaceAsync(
            ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
            => Task.FromResult(ServicingResult.Ok(workspace, ServicingHealth.Prepared));
    }

    [Fact]
    public async Task RealSequence_InspectThenSelectEdition_EnablesPrepareCommand()
    {
        var state = new AppState();
        var inspection = new CompletedInspection();
        var picker = new NoOpFilePicker { NextPath = @"C:\images\win11.iso" };
        var vm = BuildVm(state, inspection, picker, new FakeImageServicingService());

        // 1. No image selected yet -> Prepare disabled.
        Assert.False(vm.PrepareWorkingImageCommand.CanExecute(null));

        // Simulate the WPF binding: it caches CanExecute and only re-queries when
        // the command raises CanExecuteChanged. We record the queried value at the
        // moment the event fires — this is exactly what the button's IsEnabled
        // tracks. On the buggy build this handler never runs, so the cached value
        // stays the initial disabled state.
        bool? lastQueried = null;
        vm.PrepareWorkingImageCommand.CanExecuteChanged += (_, _) =>
            lastQueried = vm.PrepareWorkingImageCommand.CanExecute(null);

        // 2+3. ISO metadata becomes available.
        await vm.SelectIsoAsync();
        // No edition selected yet -> still not Ready, Prepare stays disabled.
        Assert.False(vm.PrepareWorkingImageCommand.CanExecute(null));

        // 4. User selects an edition -> workspace becomes Ready.
        var pro = inspection.Editions.First(e => e.Name == "Windows 11 Pro");
        vm.SelectedEdition = pro;

        // 5. Without recreating the VM, Prepare MUST become enabled AND the command
        //    MUST have notified the binding (CanExecuteChanged) so the button
        //    actually updates.
        Assert.True(vm.CanPrepareWorkingImage);
        Assert.True(vm.PrepareWorkingImageCommand.CanExecute(null));
        Assert.True(lastQueried == true,
            "WPF button would stay disabled: CanExecuteChanged did not fire after the workspace became Ready.");
    }

    [Fact]
    public async Task EditionChange_AfterReady_ContinuesToRaiseCanExecuteChanged()
    {
        var state = new AppState();
        var inspection = new CompletedInspection();
        var picker = new NoOpFilePicker { NextPath = @"C:\images\win11.iso" };
        var vm = BuildVm(state, inspection, picker, new FakeImageServicingService());

        await vm.SelectIsoAsync();
        vm.SelectedEdition = inspection.Editions.First(e => e.Name == "Windows 11 Pro");
        Assert.True(vm.CanPrepareWorkingImage);

        var raises = 0;
        vm.PrepareWorkingImageCommand.CanExecuteChanged += (_, _) => raises++;

        // Change to a different (still valid, not mounted) edition.
        vm.SelectedEdition = inspection.Editions.First(e => e.Name == "Windows 11 Home");
        Assert.True(vm.CanPrepareWorkingImage);
        Assert.True(raises > 0, "CanExecuteChanged must fire after an edition change.");
    }

    [Fact]
    public async Task NewIsoInspection_ResetsWorkspace_AndDisablesPrepare()
    {
        var state = new AppState();
        var inspection = new CompletedInspection();
        var picker = new NoOpFilePicker { NextPath = @"C:\images\win11.iso" };
        var vm = BuildVm(state, inspection, picker, new FakeImageServicingService());

        await vm.SelectIsoAsync();
        vm.SelectedEdition = inspection.Editions.First(e => e.Name == "Windows 11 Pro");
        Assert.True(vm.CanPrepareWorkingImage);

        var raises = 0;
        vm.PrepareWorkingImageCommand.CanExecuteChanged += (_, _) => raises++;

        // Re-inspect a different ISO -> workspace reset -> Prepare disabled.
        picker.NextPath = @"C:\images\win11-other.iso";
        await vm.SelectIsoAsync();

        Assert.False(vm.CanPrepareWorkingImage);
        Assert.False(vm.PrepareWorkingImageCommand.CanExecute(null));
        Assert.True(raises > 0, "CanExecuteChanged must fire so the button disables.");
    }

    [Fact]
    public async Task ServicingBusy_DisablesPrepareCommand()
    {
        var state = new AppState();
        var inspection = new CompletedInspection();
        var picker = new NoOpFilePicker { NextPath = @"C:\images\win11.iso" };
        var svc = new HoldServicingService
        {
            Release = new TaskCompletionSource<int>(),
            PrepareResult = ServicingResult.Ok(
                new ImageServicingWorkspace { State = ServicingWorkspaceState.Prepared },
                ServicingHealth.Prepared)
        };
        var vm = BuildVm(state, inspection, picker, svc);

        await vm.SelectIsoAsync();
        vm.SelectedEdition = inspection.Editions.First(e => e.Name == "Windows 11 Pro");
        Assert.True(vm.CanPrepareWorkingImage);

        var op = vm.PrepareWorkingImageAsync();

        // Wait for the operation to actually start (IsServicing == true).
        var started = SpinWait.SpinUntil(() => svc.Started, System.TimeSpan.FromSeconds(5));
        Assert.True(started);

        // While busy, Prepare must be disabled and the binding notified.
        Assert.False(vm.PrepareWorkingImageCommand.CanExecute(null));

        svc.Release.TrySetResult(0);
        await op;

        // After prepare completes (Prepared), Prepare is disabled by design.
        Assert.False(vm.CanPrepareWorkingImage);
    }

    [Fact]
    public void PreparedServicingState_DisablesPrepare()
    {
        var state = new AppState { CurrentImageWorkspace = ReadyWorkspace() };
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Prepared };
        var vm = BuildVm(state, new CompletedInspection(), new NoOpFilePicker(), new FakeImageServicingService());

        Assert.False(vm.CanPrepareWorkingImage);
        Assert.False(vm.PrepareWorkingImageCommand.CanExecute(null));
        Assert.True(vm.CanMountWorkingImage);
    }

    [Fact]
    public void MountedServicingState_DisablesPrepare()
    {
        var state = new AppState { CurrentImageWorkspace = ReadyWorkspace() };
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        var vm = BuildVm(state, new CompletedInspection(), new NoOpFilePicker(), new FakeImageServicingService());

        Assert.False(vm.CanPrepareWorkingImage);
        Assert.False(vm.PrepareWorkingImageCommand.CanExecute(null));
    }
}
