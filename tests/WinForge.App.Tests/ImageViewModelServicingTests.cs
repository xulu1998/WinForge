using System.Threading.Tasks;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.WimEngine;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// ViewModel-level safety guards for the Step 3.2 servicing lifecycle: the
/// prepare/mount/unmount command availability follows the servicing state
/// machine, and an active mount REFUSES source-ISO re-inspection and edition
/// re-selection (with an explanatory <see cref="ImageViewModel.BlockedMessage"/>)
/// instead of silently destroying the session.
/// </summary>
public class ImageViewModelServicingTests
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
            Build = "26100"
        };

    private static ImageViewModel BuildVm(AppState state)
        => new ImageViewModel(
            state,
            new InMemoryLoggerService(),
            new NoOpInspection(),
            new NoOpFilePicker(),
            new ImageWorkspaceFactory(),
            new WimService(),
            new FakeImageServicingService());

    private sealed class NoOpInspection : IIsoInspectionService
    {
        public Task<IsoInspectionResult> InspectAsync(string isoPath, CancellationToken cancellationToken = default)
            => Task.FromResult(IsoInspectionResult.Failed(isoPath, "not used"));
    }

    private sealed class NoOpFilePicker : IFilePicker
    {
        public string? NextPath { get; set; }

        public string? PickIsoFile() => NextPath;
    }

    [Fact]
    public void CanPrepare_IsTrue_WhenWorkspaceReady_AndNoServicing()
    {
        var state = new AppState { CurrentImageWorkspace = ReadyWorkspace() };
        var vm = BuildVm(state);

        Assert.True(vm.CanPrepareWorkingImage);
        Assert.False(vm.CanMountWorkingImage);
        Assert.False(vm.CanUnmountDiscard);
    }

    [Fact]
    public void CanPrepare_IsFalse_WhenServicingMounted()
    {
        var state = new AppState { CurrentImageWorkspace = ReadyWorkspace() };
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        var vm = BuildVm(state);

        Assert.False(vm.CanPrepareWorkingImage);
        Assert.False(vm.CanMountWorkingImage);
        Assert.True(vm.CanUnmountDiscard);
    }

    [Fact]
    public void CanMount_IsTrue_OnlyWhenPrepared()
    {
        var state = new AppState { CurrentImageWorkspace = ReadyWorkspace() };
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Prepared };
        var vm = BuildVm(state);

        // A prepared session can be re-prepared (overwrite) or mounted, but not
        // unmounted (nothing is mounted yet).
        Assert.True(vm.CanPrepareWorkingImage);
        Assert.True(vm.CanMountWorkingImage);
        Assert.False(vm.CanUnmountDiscard);
    }

    [Fact]
    public void SelectingEdition_WhileMounted_IsRefused_WithBlockedMessage()
    {
        var state = new AppState { CurrentImageWorkspace = ReadyWorkspace() };
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        var vm = BuildVm(state);

        var before = state.SelectedEdition;
        vm.SelectedEdition = new WindowsEditionInfo { Index = 1, Name = "Windows 11 Home" };

        Assert.False(string.IsNullOrEmpty(vm.BlockedMessage));
        Assert.Equal(before, state.SelectedEdition); // unchanged
    }

    [Fact]
    public async Task InspectingNewIso_WhileMounted_IsRefused_WithBlockedMessage()
    {
        var state = new AppState
        {
            SourceImagePath = @"C:\images\win.iso",
            CurrentImageWorkspace = ReadyWorkspace()
        };
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        var vm = BuildVm(state);

        // A new inspection must NOT silently forget an active mount: it is refused
        // and the original ISO path and session are preserved.
        await vm.InspectCurrentAsync();

        Assert.False(string.IsNullOrEmpty(vm.BlockedMessage));
        Assert.Equal(@"C:\images\win.iso", state.SourceImagePath);
        Assert.Equal(ServicingWorkspaceState.Mounted, state.CurrentServicingWorkspace!.State);
    }

    [Fact]
    public void ServicingStatusDisplay_Maps_State()
    {
        var state = new AppState();
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        var vm = BuildVm(state);

        Assert.Equal("Mounted", vm.ServicingStatusDisplay);
        Assert.True(vm.IsServicingMounted);
    }
}
