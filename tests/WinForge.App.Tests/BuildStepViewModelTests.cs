using System.Globalization;
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

    // ---- Phase 10 real-desktop defect: Build page keeps the "run Apply first"
    // warning after Apply completed successfully. BuildStepViewModel is a singleton
    // (constructed once, before Apply runs), so it must react live to the shared
    // CustomizationExecutionState change and clear the stale banner. ----

    private static BuildStepViewModel NewVm(AppState state, IAdkToolLocator adk, IFileSystem? fs = null)
        => new BuildStepViewModel(state, new FakeBuildService(), fs ?? new RecordingFileSystem(),
            new FakeFilePicker(), adk, new InMemoryLoggerService(), new FakeLocalizationService());

    /// <summary>
    /// Minimal culture-aware <see cref="ILocalizationService"/> used to prove the
    /// Build gating does not depend on the active UI culture. It tags nothing — the
    /// resource KEY is returned verbatim — so the banner key (and thus the gate) is
    /// identical regardless of the culture passed to <see cref="SetCulture"/>.
    /// </summary>
#pragma warning disable CS0067 // interface-required events are never raised by this fake
    private sealed class CultureAwareLoc : ILocalizationService
    {
        public CultureInfo CurrentCulture { get; private set; } = CultureInfo.GetCultureInfo("en");
        public event EventHandler? CultureChanged;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public string this[string key] => key;
        public bool Contains(string key) => true;
        public void SetCulture(CultureInfo culture) => CurrentCulture = culture;
    }
#pragma warning restore CS0067

    [Fact]
    public void NotApplied_Shows_NeedsApply_And_CannotBuild()
    {
        var state = ReadyState();
        state.CustomizationExecutionState = CustomizationExecutionState.Idle; // not applied yet
        var vm = NewVm(state, new FakeAdkToolLocator()); // ADK present, mounted, output seeded

        Assert.False(vm.HasApplied);
        Assert.False(vm.CanBuild);
        Assert.Equal("Build.Status.NeedsApply", vm.StatusMessage);
    }

    [Fact]
    public void ApplyCompleted_Live_ClearsWarning_And_EnablesBuild()
    {
        var state = ReadyState();
        state.CustomizationExecutionState = CustomizationExecutionState.Idle; // start not applied
        var vm = NewVm(state, new FakeAdkToolLocator()); // singleton VM already subscribed

        Assert.Equal("Build.Status.NeedsApply", vm.StatusMessage);
        Assert.False(vm.CanBuild);

        // Apply finishes successfully on the shared AppState — no VM recreation.
        state.CustomizationExecutionState = CustomizationExecutionState.Completed;

        // The singleton VM must react to the change live: warning cleared, gate opens.
        Assert.True(vm.HasApplied);
        Assert.True(vm.CanBuild);
        Assert.Equal(string.Empty, vm.StatusMessage); // stale "run Apply first" gone
    }

    [Fact]
    public void CompletedWithErrors_Also_Satisfies_Apply_Prerequisite()
    {
        var state = ReadyState();
        state.CustomizationExecutionState = CustomizationExecutionState.Idle;
        var vm = NewVm(state, new FakeAdkToolLocator());

        state.CustomizationExecutionState = CustomizationExecutionState.CompletedWithErrors;

        Assert.True(vm.HasApplied);
        Assert.True(vm.CanBuild);
        Assert.Equal(string.Empty, vm.StatusMessage);
    }

    [Fact]
    public void FailedApply_KeepsBuildDisabled_And_Warns()
    {
        var state = ReadyState();
        state.CustomizationExecutionState = CustomizationExecutionState.Idle;
        var vm = NewVm(state, new FakeAdkToolLocator());

        state.CustomizationExecutionState = CustomizationExecutionState.Failed;

        Assert.False(vm.HasApplied);
        Assert.False(vm.CanBuild);
        Assert.Equal("Build.Status.NeedsApply", vm.StatusMessage);
    }

    [Fact]
    public void SwitchingBackToNotApplied_Restores_Warning()
    {
        var state = ReadyState();
        state.CustomizationExecutionState = CustomizationExecutionState.Idle;
        var vm = NewVm(state, new FakeAdkToolLocator());

        state.CustomizationExecutionState = CustomizationExecutionState.Completed;
        Assert.True(vm.CanBuild);
        Assert.Equal(string.Empty, vm.StatusMessage);

        // Execution reverted (e.g. plan edited, re-apply pending): warning returns.
        state.CustomizationExecutionState = CustomizationExecutionState.Idle;
        Assert.False(vm.CanBuild);
        Assert.Equal("Build.Status.NeedsApply", vm.StatusMessage);

        // Re-complete: warning clears again — no stale/locked state.
        state.CustomizationExecutionState = CustomizationExecutionState.Completed;
        Assert.True(vm.CanBuild);
        Assert.Equal(string.Empty, vm.StatusMessage);
    }

    [Fact]
    public void AdkMissing_Shows_AdapterMissing_EvenAfterApply()
    {
        var state = ReadyState();
        state.CustomizationExecutionState = CustomizationExecutionState.Completed;
        var vm = NewVm(state, new MissingAdkToolLocator());

        Assert.True(vm.HasApplied);
        Assert.True(vm.AdkMissing);
        Assert.False(vm.CanBuild);
        Assert.Equal("Build.Status.AdapterMissing", vm.StatusMessage);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void Gating_Is_CultureIndependent(string culture)
    {
        // The gating logic depends only on AppState + ADK, never on locale; the
        // banner resolves to the SAME resource KEY in every culture, so the message
        // and gate are identical across en-US and zh-CN. `culture` is consumed by the
        // culture-aware FakeLoc to prove the key (and thus the gate) is locale-invariant.
        var state = ReadyState();
        state.CustomizationExecutionState = CustomizationExecutionState.Idle;
        var loc = new CultureAwareLoc();
        loc.SetCulture(CultureInfo.GetCultureInfo(culture));
        var vm = new BuildStepViewModel(state, new FakeBuildService(), new RecordingFileSystem(),
            new FakeFilePicker(), new FakeAdkToolLocator(), new InMemoryLoggerService(), loc);

        Assert.Equal("Build.Status.NeedsApply", vm.StatusMessage);
        Assert.False(vm.CanBuild);
        Assert.False(vm.HasApplied);

        state.CustomizationExecutionState = CustomizationExecutionState.Completed;

        Assert.Equal(string.Empty, vm.StatusMessage); // cleared in every culture
        Assert.True(vm.CanBuild);
        Assert.True(vm.HasApplied);
    }

    // ---- Phase 10 real-desktop defect: after a successful Commit the working image
    // is unmounted, so IsMounted is no longer true. The build must still be
    // retryable from the durable exported-WIM checkpoint without re-applying or
    // re-committing. HasBuildCheckpoint keeps CanBuild usable. ----

    [Fact]
    public void PostCommitFailure_BuildRemainsRetryable_ViaCheckpoint()
    {
        var state = ReadyState(); // Applied + Mounted, WorkingDirectory = C:\ws
        var fs = new RecordingFileSystem();
        // Simulate a prior post-commit run that left a durable exported WIM.
        fs.SeedFile(@"C:\ws\build\install.wim", 100);

        var vm = NewVm(state, new FakeAdkToolLocator(), fs);
        Assert.True(vm.CanBuild); // open while mounted

        // Post-commit failure: image committed & unmounted, but checkpoint survives.
        state.CurrentServicingWorkspace!.State = ServicingWorkspaceState.Prepared;
        vm.Refresh();

        Assert.False(vm.IsMounted);
        Assert.True(vm.HasBuildCheckpoint);
        Assert.True(vm.CanBuild);                 // retryable without re-apply/re-commit
        Assert.Equal(string.Empty, vm.StatusMessage); // no stale "needs mount" warning
    }

    [Fact]
    public void NoCheckpoint_And_NotMounted_BlocksBuild()
    {
        var state = ReadyState();
        state.CurrentServicingWorkspace!.State = ServicingWorkspaceState.Prepared; // not mounted
        var fs = new RecordingFileSystem(); // no durable checkpoint seeded
        var vm = NewVm(state, new FakeAdkToolLocator(), fs);

        Assert.False(vm.IsMounted);
        Assert.False(vm.HasBuildCheckpoint);
        Assert.False(vm.CanBuild);
        Assert.Equal("Build.Status.NeedsMount", vm.StatusMessage);
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
