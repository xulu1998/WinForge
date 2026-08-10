using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using WinForge.App.ViewModels;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Phase 10 — <see cref="BuildStepViewModel"/> behavior: defaults derived from the
/// servicing workspace, build gating (Apply + mount + ADK + non-empty paths),
/// successful build outcome, and cancellation. Confirms the UI never reports
/// success for a failed/cancelled build and that success updates shared state.
/// </summary>
public sealed class BuildStepViewModelTests
{
    private static AppState ReadyState()
    {
        var state = new AppState();
        state.CurrentServicingWorkspace = new ImageServicingWorkspace
        {
            SelectedEditionName = "Windows 11 Pro",
            WorkingDirectory = @"C:\ws",
            SourceIsoPath = @"C:\src.iso",
            SourceImageRelativePath = @"sources\install.wim",
            WorkingImagePath = @"C:\work\install.wim",
            MountDirectory = @"C:\work\mount",
            WorkingIndex = 1,
            State = ServicingWorkspaceState.Mounted
        };
        state.CustomizationExecutionState = CustomizationExecutionState.Completed;
        return state;
    }

    [Fact]
    public void Defaults_Are_Seeded_From_ServicingWorkspace()
    {
        var state = ReadyState();
        var fs = new RecordingFileSystem();
        var vm = new BuildStepViewModel(
            state, new FakeBuildService(), fs, new FakeFilePicker(),
            new FakeAdkToolLocator(), new InMemoryLoggerService(), new FakeLocalizationService());

        Assert.Equal("Windows 11 Pro", vm.SourceEdition);
        Assert.Equal("Windows 11 Pro", vm.FinalEditionName);
        Assert.StartsWith("WinForge_Windows_11_Pro_", vm.OutputFileName);
        Assert.DoesNotContain(".iso", vm.OutputFileName);
        Assert.Equal(fs.PathCombine(fs.GetTempPath(), "WinForge", "Output"), vm.OutputDirectory);
    }

    [Fact]
    public void CanBuild_Requires_Adk()
    {
        var state = ReadyState();
        var vm = new BuildStepViewModel(
            state, new FakeBuildService(), new RecordingFileSystem(), new FakeFilePicker(),
            new MissingAdkToolLocator(), new InMemoryLoggerService(), new FakeLocalizationService());

        Assert.True(vm.AdkMissing);
        Assert.False(vm.CanBuild);
    }

    [Fact]
    public void CanBuild_Requires_Applied_And_Mounted()
    {
        var state = ReadyState();
        var vm = new BuildStepViewModel(
            state, new FakeBuildService(), new RecordingFileSystem(), new FakeFilePicker(),
            new FakeAdkToolLocator(), new InMemoryLoggerService(), new FakeLocalizationService());

        // Apply not yet completed -> cannot build.
        state.CustomizationExecutionState = CustomizationExecutionState.Idle;
        vm.Refresh();
        Assert.False(vm.CanBuild);

        // Applied but not mounted -> cannot build.
        state.CustomizationExecutionState = CustomizationExecutionState.Completed;
        state.CurrentServicingWorkspace!.State = ServicingWorkspaceState.Prepared;
        vm.Refresh();
        Assert.False(vm.CanBuild);

        // Applied and mounted -> can build.
        state.CurrentServicingWorkspace.State = ServicingWorkspaceState.Mounted;
        vm.Refresh();
        Assert.True(vm.CanBuild);
    }

    [Fact]
    public async Task Successful_Build_Sets_Output_Status_And_Prepared_State()
    {
        var state = ReadyState();
        var fs = new RecordingFileSystem();
        var vm = new BuildStepViewModel(
            state, new SuccessBuildService(), fs, new FakeFilePicker(),
            new FakeAdkToolLocator(), new InMemoryLoggerService(), new FakeLocalizationService());

        Assert.True(vm.CanBuild);
        await ExecuteCommandAsync(vm.BuildCommand);

        Assert.True(vm.HasOutput);
        Assert.Equal(@"C:\out\WinForge_Pro_20260810-1200.iso", vm.OutputPath);
        Assert.Equal(BuildState.Completed, state.BuildStatus);
        Assert.Equal(BuildState.Completed, vm.CurrentStage);
        Assert.Equal(ServicingWorkspaceState.Prepared, state.CurrentServicingWorkspace!.State);
        Assert.NotEmpty(vm.LogText);
    }

    [Fact]
    public async Task Cancelled_Build_Sets_Cancelled_State()
    {
        var state = ReadyState();
        var fs = new RecordingFileSystem();
        var slow = new SlowCancellableBuildService();
        var vm = new BuildStepViewModel(
            state, slow, fs, new FakeFilePicker(),
            new FakeAdkToolLocator(), new InMemoryLoggerService(), new FakeLocalizationService());

        Assert.True(vm.CanBuild);
        var task = ExecuteCommandAsync(vm.BuildCommand);
        await slow.Started.Task; // ensure the build is in flight
        vm.CancelCommand.Execute(null);
        await task;

        Assert.Equal(BuildState.Cancelled, state.BuildStatus);
        Assert.False(vm.IsBuilding);
        Assert.False(vm.HasOutput);
    }

    [Fact]
    public void CanCancel_Only_While_Building()
    {
        var state = ReadyState();
        var vm = new BuildStepViewModel(
            state, new FakeBuildService(), new RecordingFileSystem(), new FakeFilePicker(),
            new FakeAdkToolLocator(), new InMemoryLoggerService(), new FakeLocalizationService());

        Assert.False(vm.CanCancel);
        Assert.True(vm.CanBuild);
    }

    /// <summary>
    /// Invokes an <see cref="ICommand"/>'s async execution path without taking a
    /// compile-time dependency on CommunityToolkit.Mvvm's <c>IAsyncRelayCommand</c>
    /// (not directly referencable from the test project).
    /// </summary>
    private static Task ExecuteCommandAsync(ICommand command)
    {
        var method = command.GetType().GetMethod("ExecuteAsync", new[] { typeof(object) })
                    ?? throw new InvalidOperationException("ExecuteAsync not found on command type.");
        return (Task)method.Invoke(command, new object?[] { null })!;
    }
}
