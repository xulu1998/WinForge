using System;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using WinForge.App.Localization;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.App.Workflow;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// PHASE 10 UX POLISH — final Build step used to show a disabled "Next" after a
/// successful build. These regression tests lock in the corrected final-step UX:
/// the wizard hides "Next" on the last step and shows a completion-gated,
/// localized "Finish" button; a Failed/Cancelled build must never present a
/// successful Finish; Finish ends the wizard WITHOUT deleting the produced ISO;
/// and the "Open output folder" affordance is enabled only when the ISO exists.
/// </summary>
public sealed class WizardFinishButtonTests
{
    // ---- Minimal no-op fakes for the ImageViewModel dependency graph ----
    // (Distinct names avoid colliding with the multiple FakeFilePicker /
    // FakeInspection / etc. already declared elsewhere in this namespace.)

    private sealed class NullInspection : IIsoInspectionService
    {
        public Task<IsoInspectionResult> InspectAsync(string isoPath, CancellationToken ct = default)
            => Task.FromResult(IsoInspectionResult.Failed(isoPath, "unused"));
    }

    private sealed class NullFilePicker : IFilePicker
    {
        public string? PickIsoFile() => null;
        public string? PickFolder() => null;
    }

    private sealed class NullWorkspaceFactory : IImageWorkspaceFactory
    {
        public ImageWorkspaceBuildResult BuildWorkspace(IsoInspectionResult inspection, WindowsEditionInfo? selectedEdition)
            => new(null, ImageWorkspaceStatus.NotReady, Array.Empty<string>());
    }

    private sealed class NullWimService : IWimService
    {
        public ImageWorkspaceStatus ValidateWorkspace(ImageWorkspace workspace) => ImageWorkspaceStatus.NotReady;
        public SelectedImageContext? ResolveSelectedImage(ImageWorkspace workspace) => null;
    }

    /// <summary>Records navigation so Finish's clean-end behaviour is observable.</summary>
    private sealed class RecordingNavigationService : INavigationService
    {
        public PageKey CurrentPage { get; private set; } = PageKey.Home;
        public PageKey? LastKey { get; private set; }
        public int NavigateCalls { get; private set; }
        public event EventHandler<PageKey>? CurrentPageChanged;
        public void NavigateTo(PageKey page)
        {
            LastKey = page;
            CurrentPage = page;
            NavigateCalls++;
            CurrentPageChanged?.Invoke(this, page);
        }
    }

    /// <summary>Build service that always fails, for the Failed-does-not-Finish case.</summary>
    private sealed class FailingBuildService : IBuildService
    {
        public Task<BuildResult> BuildAsync(
            BuildRequest request, IProgress<BuildProgress>? progress = null, CancellationToken ct = default)
            => Task.FromResult(BuildResult.Fail(
                BuildState.Failed, "simulated build failure", Array.Empty<string>()));

        public Task<BuildRecoveryState?> DetectInterruptedBuildAsync(string dir, CancellationToken ct = default)
            => Task.FromResult<BuildRecoveryState?>(null);

        public Task<bool> CleanupInterruptedBuildAsync(string dir, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    // ---- Graph construction ----

    private static (WorkflowViewModel Wf, AppState State, BuildStepViewModel Build) BuildGraph(
        IBuildService buildService, RecordingFileSystem fs, RecordingNavigationService nav)
    {
        var state = new AppState();
        var logger = new InMemoryLoggerService();
        var image = new ImageViewModel(state, logger, new NullInspection(), new NullFilePicker(),
            new NullWorkspaceFactory(), new NullWimService(), new FakeImageServicingService());
        var components = new ComponentsViewModel(state, logger, new FakeCustomizationDiscoveryService(), new FakeCustomizationDefinitionProvider());
        var customize = ComponentKnowledgeTestFactory.MakeCustomize(state, logger);
        var plan = new PlanReviewViewModel(state, logger, new FakeCustomizationExecutionService());
        var build = new BuildStepViewModel(state, buildService, fs, new NullFilePicker(),
            new FakeAdkToolLocator(), logger, new FakeLocalizationService());
        var wf = new WorkflowViewModel(state, image, customize, plan, build, nav);
        return (wf, state, build);
    }

    /// <summary>Sets shared state so the Build step is reachable and CanBuild is true.</summary>
    private static void MakeBuildReady(AppState state)
    {
        state.CurrentImageWorkspace = new ImageWorkspace();
        state.CurrentServicingWorkspace = new ImageServicingWorkspace
        {
            SelectedEditionName = "Windows 11 Pro",
            State = ServicingWorkspaceState.Mounted
        };
        state.CustomizationExecutionState = CustomizationExecutionState.Completed;
        state.CurrentCustomizationPlan = MakePlan();
    }

    private static CustomizationPlan MakePlan()
    {
        var plan = new CustomizationPlan();
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "appx|A",
            OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            TargetIdentifier = "A",
            Risk = RiskClass.Removable,
            IsSelected = true
        });
        return plan;
    }

    private static Task ExecuteCommandAsync(ICommand command)
    {
        var method = command.GetType().GetMethod("ExecuteAsync", new[] { typeof(object) })
                    ?? throw new InvalidOperationException("ExecuteAsync not found on command type.");
        return (Task)method.Invoke(command, new object?[] { null })!;
    }

    // ---- (1) Final step has no forward navigation ----

    [Fact]
    public void Final_Step_Has_No_Forward_Navigation()
    {
        var fs = new RecordingFileSystem();
        var nav = new RecordingNavigationService();
        var (wf, state, _) = BuildGraph(new SuccessBuildService(), fs, nav);

        MakeBuildReady(state);
        wf.GoToStep(WorkflowStep.Build);

        Assert.True(wf.IsFinalStep);                 // Build is the last step
        Assert.False(wf.CanGoNext);                  // no meaningless "Next"
        Assert.False(wf.NextCommand.CanExecute(null));
        Assert.False(wf.CanFinish);                  // not completed yet
    }

    // ---- (2) Completed build enables Finish ----

    [Fact]
    public async Task Completed_Build_Enables_Finish()
    {
        var fs = new RecordingFileSystem();
        var nav = new RecordingNavigationService();
        var (wf, state, build) = BuildGraph(new SuccessBuildService(), fs, nav);

        MakeBuildReady(state);
        wf.GoToStep(WorkflowStep.Build);
        Assert.True(wf.IsFinalStep);
        Assert.False(wf.CanFinish);                  // still building / not done

        await ExecuteCommandAsync(build.BuildCommand);

        Assert.Equal(BuildState.Completed, build.CurrentStage);
        Assert.True(wf.IsFinalStep);
        Assert.True(wf.CanFinish);                   // Completed + final step
        Assert.True(wf.FinishCommand.CanExecute(null));
    }

    // ---- (3) Finish / Open-output-folder text localized zh-CN and en-US ----

    [Fact]
    public void Finish_And_OpenFolder_Text_Localized_ZhCn_And_En()
    {
        var rm = new ResourceManager("WinForge.App.Resources.Strings", typeof(HomeViewModel).Assembly);
        var svc = new ResourceManagerLocalizationService(rm, CultureInfo.GetCultureInfo("en"));

        // en-US
        svc.SetCulture(CultureInfo.GetCultureInfo("en"));
        Assert.Equal("Finish", svc["Nav.Finish"]);
        Assert.Equal("Open output folder", svc["Build.OpenOutputFolder"]);

        // zh-CN
        svc.SetCulture(CultureInfo.GetCultureInfo("zh-CN"));
        Assert.Equal("完成", svc["Nav.Finish"]);
        Assert.Equal("打开输出文件夹", svc["Build.OpenOutputFolder"]);
    }

    // ---- (4) Failed build does not enable a successful Finish ----

    [Fact]
    public async Task Failed_Build_Does_Not_Enable_Finish()
    {
        var fs = new RecordingFileSystem();
        var nav = new RecordingNavigationService();
        var (wf, state, build) = BuildGraph(new FailingBuildService(), fs, nav);

        MakeBuildReady(state);
        wf.GoToStep(WorkflowStep.Build);
        Assert.True(wf.IsFinalStep);

        await ExecuteCommandAsync(build.BuildCommand);

        Assert.Equal(BuildState.Failed, build.CurrentStage);
        Assert.True(wf.IsFinalStep);
        Assert.False(wf.CanFinish);                  // Failed must NOT enable Finish
        Assert.False(wf.FinishCommand.CanExecute(null));
    }

    // ---- (5) Cancelled build does not enable a successful Finish ----

    [Fact]
    public async Task Cancelled_Build_Does_Not_Enable_Finish()
    {
        var fs = new RecordingFileSystem();
        var nav = new RecordingNavigationService();
        var slow = new SlowCancellableBuildService();
        var (wf, state, build) = BuildGraph(slow, fs, nav);

        MakeBuildReady(state);
        wf.GoToStep(WorkflowStep.Build);
        Assert.True(wf.IsFinalStep);

        var task = ExecuteCommandAsync(build.BuildCommand);
        await slow.Started.Task;                     // build is in flight
        build.CancelCommand.Execute(null);
        await task;

        Assert.Equal(BuildState.Cancelled, build.CurrentStage);
        Assert.True(wf.IsFinalStep);
        Assert.False(wf.CanFinish);                  // Cancelled must NOT enable Finish
        Assert.False(wf.FinishCommand.CanExecute(null));
    }

    // ---- (6) Finish does not delete the produced ISO ----

    [Fact]
    public async Task Finish_Does_Not_Delete_Output_Iso()
    {
        var fs = new RecordingFileSystem();
        var nav = new RecordingNavigationService();
        var (wf, state, build) = BuildGraph(new SuccessBuildService(), fs, nav);
        var outputIso = @"C:\out\WinForge_Pro_20260810-1200.iso";

        MakeBuildReady(state);
        wf.GoToStep(WorkflowStep.Build);
        await ExecuteCommandAsync(build.BuildCommand);

        Assert.Equal(BuildState.Completed, build.CurrentStage);
        Assert.Equal(outputIso, build.OutputPath);
        Assert.True(wf.CanFinish);

        // The produced ISO exists on disk (simulated).
        fs.SeedFile(outputIso, 1_234_567);

        // Finish ends the wizard cleanly.
        wf.FinishCommand.Execute(null);

        Assert.Equal(1, nav.NavigateCalls);
        Assert.Equal(PageKey.Home, nav.LastKey);

        // The ISO and every build artifact are untouched by Finish.
        Assert.True(fs.FileExists(outputIso), "Finish must not delete the produced ISO.");
        Assert.DoesNotContain(outputIso, fs.DeletedFiles);
    }

    // ---- (7) Open output folder enabled only when the output exists ----

    [Fact]
    public async Task OpenOutputFolder_Enabled_Only_When_Output_Exists()
    {
        var fs = new RecordingFileSystem();
        var nav = new RecordingNavigationService();
        var (wf, state, build) = BuildGraph(new SuccessBuildService(), fs, nav);
        var outputIso = @"C:\out\WinForge_Pro_20260810-1200.iso";

        MakeBuildReady(state);
        wf.GoToStep(WorkflowStep.Build);

        // Before any build there is no output -> affordance disabled.
        Assert.False(build.HasOutput);
        Assert.False(build.CanOpenOutputFolder);
        Assert.False(build.OpenOutputFolderCommand.CanExecute(null));

        // After a successful build the path is known but the file must actually
        // exist on disk for the affordance to light up.
        await ExecuteCommandAsync(build.BuildCommand);
        Assert.True(build.HasOutput);
        Assert.Equal(outputIso, build.OutputPath);
        Assert.False(build.CanOpenOutputFolder);     // file not on disk yet
        Assert.False(build.OpenOutputFolderCommand.CanExecute(null));

        // Once the file exists, the affordance is enabled.
        fs.SeedFile(outputIso, 1_234_567);
        build.Refresh();
        Assert.True(build.CanOpenOutputFolder);
        Assert.True(build.OpenOutputFolderCommand.CanExecute(null));
    }
}
