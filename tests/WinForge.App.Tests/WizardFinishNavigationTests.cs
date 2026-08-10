using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
using WinForge.Infrastructure.ComponentIntelligence;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// PHASE 10 FINAL-STEP DEFECT — Finish was enabled but clicking it did nothing.
/// Root cause: INavigationService.CurrentPage was initialized to Home and never
/// updated while the wizard was shown (MainViewModel set ActiveView directly),
/// so Finish()'s NavigateTo(Home) was a no-op and the shell never swapped away
/// from the wizard. These higher-level tests drive a REAL MainViewModel shell
/// with a REAL NavigationService and assert the visible state actually changes
/// to Home on Finish, the ISO/logs survive, and no mount lifecycle is touched.
/// </summary>
public sealed class WizardFinishNavigationTests
{
    // ---- Recording fakes ----

    /// <summary>Counts NavigateTo calls so we can assert Finish navigates exactly once.</summary>
    private sealed class CountingNavigationService : INavigationService
    {
        private readonly INavigationService _inner;
        public CountingNavigationService(INavigationService inner) => _inner = inner;
        public int Count { get; private set; }
        public PageKey CurrentPage => _inner.CurrentPage;
        public event EventHandler<PageKey>? CurrentPageChanged
        {
            add => _inner.CurrentPageChanged += value;
            remove => _inner.CurrentPageChanged -= value;
        }
        public void NavigateTo(PageKey page) { Count++; _inner.NavigateTo(page); }
    }

    /// <summary>Records mount-lifecycle calls so we can prove Finish touches none of them.</summary>
    private sealed class RecordingServicingService : IImageServicingService
    {
        public int MountCalls { get; private set; }
        public int UnmountDiscardCalls { get; private set; }
        public int CommitCalls { get; private set; }

        public Task<ServicingResult> PrepareWorkingImageAsync(ImageWorkspace source, string workspaceId, CancellationToken ct = default)
            => Task.FromResult(ServicingResult.Ok(new ImageServicingWorkspace(), ServicingHealth.Prepared));
        public Task<ServicingResult> MountAsync(ImageServicingWorkspace workspace, CancellationToken ct = default)
        {
            MountCalls++;
            return Task.FromResult(ServicingResult.Ok(workspace, ServicingHealth.Mounted));
        }
        public Task<ServicingResult> UnmountDiscardAsync(ImageServicingWorkspace workspace, CancellationToken ct = default)
        {
            UnmountDiscardCalls++;
            return Task.FromResult(ServicingResult.Ok(workspace, ServicingHealth.Prepared));
        }
        public Task<ServicingResult> ValidateServicingWorkspaceAsync(ImageServicingWorkspace workspace, CancellationToken ct = default)
            => Task.FromResult(ServicingResult.Ok(workspace, ServicingHealth.Prepared));
        public Task<ServicingResult> CommitUnmountAsync(ImageServicingWorkspace workspace, CancellationToken ct = default)
        {
            CommitCalls++;
            return Task.FromResult(ServicingResult.Ok(workspace, ServicingHealth.Prepared));
        }
    }

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

    private sealed class NullLanguageSettingsStore : ILanguageSettingsStore
    {
        public string? LoadCulture() => null;
        public void SaveCulture(string cultureName) { }
    }

    /// <summary>Build service that always fails, for the Failed-does-not-Finish case.</summary>
    private sealed class FailingBuildService : IBuildService
    {
        public Task<BuildResult> BuildAsync(BuildRequest request, IProgress<BuildProgress>? progress = null, CancellationToken ct = default)
            => Task.FromResult(BuildResult.Fail(BuildState.Failed, "simulated build failure", Array.Empty<string>()));
        public Task<BuildRecoveryState?> DetectInterruptedBuildAsync(string dir, CancellationToken ct = default)
            => Task.FromResult<BuildRecoveryState?>(null);
        public Task<bool> CleanupInterruptedBuildAsync(string dir, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    // ---- Graph construction ----

    private static (
        MainViewModel Main,
        CountingNavigationService Nav,
        WorkflowViewModel Wf,
        AppState State,
        BuildStepViewModel Build,
        RecordingFileSystem Fs,
        RecordingServicingService Servicing,
        InMemoryLoggerService Logger)
        BuildShell(IBuildService buildService, ILocalizationService loc)
    {
        var state = new AppState();
        var logger = new InMemoryLoggerService();
        var fs = new RecordingFileSystem();
        var servicing = new RecordingServicingService();
        var innerNav = new NavigationService(logger);
        var nav = new CountingNavigationService(innerNav);

        var image = new ImageViewModel(state, logger, new NullInspection(), new NullFilePicker(),
            new NullWorkspaceFactory(), new NullWimService(), servicing);
        var components = new ComponentsViewModel(state, logger, new FakeCustomizationDiscoveryService(), new FakeCustomizationDefinitionProvider());
        var privacy = new PrivacyViewModel(state, logger, new FakeCustomizationDefinitionProvider());
        var system = new SystemViewModel(state, logger, new FakeCustomizationDefinitionProvider());
        var comingSoon = new ComingSoonViewModel();
        var customize = new CustomizeStepViewModel(components, privacy, system, comingSoon);
        var plan = new PlanReviewViewModel(state, logger, new FakeCustomizationExecutionService());
        var build = new BuildStepViewModel(state, buildService, fs, new NullFilePicker(),
            new FakeAdkToolLocator(), logger, loc);
        var wf = new WorkflowViewModel(state, image, customize, plan, build, nav);

        var home = new HomeViewModel(state, nav);
        var logs = new LogsViewModel(logger);
        var settings = new SettingsViewModel(loc, new NullLanguageSettingsStore());
        var about = new AboutViewModel();

        var ci = new ComponentIntelligenceViewModel(
            state, logger, new NotDiscoveredComponentIntelligenceService(), new CuratedComponentCatalog(), loc);

        // The real shell, wired through the single navigation coordinator.
        var main = new MainViewModel(nav, home, logs, settings, about, comingSoon, wf, ci);
        return (main, nav, wf, state, build, fs, servicing, logger);
    }

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

    private static ResourceManagerLocalizationService RealLocalizer(CultureInfo culture)
    {
        var rm = new ResourceManager("WinForge.App.Resources.Strings", typeof(HomeViewModel).Assembly);
        return new ResourceManagerLocalizationService(rm, culture);
    }

    // ---- Startup sync precondition (why Finish used to be a no-op) ----

    [Fact]
    public void Startup_Shows_Wizard_And_Syncs_Navigation_To_Workflow()
    {
        var (main, nav, _, _, _, _, _, _) = BuildShell(new SuccessBuildService(), new FakeLocalizationService());

        // The wizard is the default surface AND the navigation coordinator agrees.
        // This sync is what makes Finish()'s later NavigateTo(Home) a real transition.
        Assert.True(main.IsWorkflowActive);
        Assert.IsType<WorkflowViewModel>(main.ActiveView);
        Assert.Equal(PageKey.Workflow, nav.CurrentPage);
    }

    // ---- (1) Completed build enables Finish ----

    [Fact]
    public async Task Completed_Build_Enables_Finish_Command()
    {
        var (_, _, wf, state, build, _, _, _) = BuildShell(new SuccessBuildService(), new FakeLocalizationService());

        MakeBuildReady(state);
        wf.GoToStep(WorkflowStep.Build);
        await ExecuteCommandAsync(build.BuildCommand);

        Assert.Equal(BuildState.Completed, build.CurrentStage);
        Assert.True(wf.IsFinalStep);
        Assert.True(wf.CanFinish);
        Assert.True(wf.FinishCommand.CanExecute(null));
    }

    // ---- (2) executing Finish invokes navigation to Home exactly once ----

    [Fact]
    public async Task Finish_Invokes_Navigation_To_Home_Exactly_Once()
    {
        var (_, nav, wf, state, build, _, _, _) = BuildShell(new SuccessBuildService(), new FakeLocalizationService());

        MakeBuildReady(state);
        wf.GoToStep(WorkflowStep.Build);
        await ExecuteCommandAsync(build.BuildCommand);

        var before = nav.Count; // includes the single NavigateTo(Workflow) from startup
        wf.FinishCommand.Execute(null);

        Assert.Equal(PageKey.Home, nav.CurrentPage);
        Assert.Equal(before + 1, nav.Count); // exactly one navigation: to Home
    }

    // ---- (3) shell / current-page actually becomes Home ----

    [Fact]
    public async Task Finish_Makes_Shell_Show_HomeView()
    {
        var (main, nav, wf, state, build, _, _, _) = BuildShell(new SuccessBuildService(), new FakeLocalizationService());

        MakeBuildReady(state);
        wf.GoToStep(WorkflowStep.Build);
        await ExecuteCommandAsync(build.BuildCommand);

        wf.FinishCommand.Execute(null);

        Assert.Equal(PageKey.Home, nav.CurrentPage);
        Assert.False(main.IsWorkflowActive);
        Assert.IsType<HomeViewModel>(main.ActiveView);
    }

    // ---- (4) Wizard content is no longer current ----

    [Fact]
    public async Task Finish_Hides_Wizard_Content()
    {
        var (main, _, wf, state, build, _, _, _) = BuildShell(new SuccessBuildService(), new FakeLocalizationService());

        MakeBuildReady(state);
        wf.GoToStep(WorkflowStep.Build);
        await ExecuteCommandAsync(build.BuildCommand);

        wf.FinishCommand.Execute(null);

        Assert.False(main.IsWorkflowActive);
        Assert.IsNotType<WorkflowViewModel>(main.ActiveView);
    }

    // ---- (5) output ISO still exists ----

    [Fact]
    public async Task Finish_Preserves_Output_Iso()
    {
        var (_, _, wf, state, build, fs, _, _) = BuildShell(new SuccessBuildService(), new FakeLocalizationService());

        MakeBuildReady(state);
        wf.GoToStep(WorkflowStep.Build);
        await ExecuteCommandAsync(build.BuildCommand);

        // Materialize the produced ISO in the fake filesystem (the real pipeline
        // writes it through IFileSystem; tests seed it to make "still exists" observable).
        Assert.False(string.IsNullOrEmpty(build.OutputPath));
        var isoPath = build.OutputPath!;
        fs.SeedFile(isoPath, 4_000_000);

        wf.FinishCommand.Execute(null);

        // Finish never deletes the ISO or its path.
        Assert.True(fs.FileExists(isoPath));
        Assert.Equal(isoPath, build.OutputPath);
    }

    // ---- (6) logs remain ----

    [Fact]
    public async Task Finish_Preserves_Logs()
    {
        var (_, _, wf, state, build, _, _, logger) = BuildShell(new SuccessBuildService(), new FakeLocalizationService());

        MakeBuildReady(state);
        wf.GoToStep(WorkflowStep.Build);
        await ExecuteCommandAsync(build.BuildCommand);

        // Snapshot the log lines produced by the build + navigation.
        var before = logger.Entries.Select(e => e.Message).ToList();
        Assert.NotEmpty(before); // build/servicing produced log output

        wf.FinishCommand.Execute(null);

        // No log entry was dropped; the pre-Finish history is intact.
        foreach (var line in before)
        {
            Assert.Contains(logger.Entries, e => e.Message == line);
        }
    }

    // ---- (7) no dismount / remount call occurs ----

    [Fact]
    public async Task Finish_Performs_No_Dismount_Or_Remount()
    {
        var (_, _, wf, state, build, _, servicing, _) = BuildShell(new SuccessBuildService(), new FakeLocalizationService());

        MakeBuildReady(state);
        wf.GoToStep(WorkflowStep.Build);
        await ExecuteCommandAsync(build.BuildCommand);

        var mountBefore = servicing.MountCalls;
        var unmountBefore = servicing.UnmountDiscardCalls;
        var commitBefore = servicing.CommitCalls;

        wf.FinishCommand.Execute(null);

        // Finish must not touch the mount lifecycle.
        Assert.Equal(mountBefore, servicing.MountCalls);
        Assert.Equal(unmountBefore, servicing.UnmountDiscardCalls);
        Assert.Equal(commitBefore, servicing.CommitCalls);
    }

    // ---- (8) failed / cancelled build cannot Finish as a successful workflow ----

    [Fact]
    public async Task Failed_Build_Cannot_Finish_And_Stays_On_Wizard()
    {
        var (main, nav, wf, state, build, _, _, _) = BuildShell(new FailingBuildService(), new FakeLocalizationService());

        MakeBuildReady(state);
        wf.GoToStep(WorkflowStep.Build);
        await ExecuteCommandAsync(build.BuildCommand);

        Assert.Equal(BuildState.Failed, build.CurrentStage);
        Assert.False(wf.CanFinish);
        Assert.False(wf.FinishCommand.CanExecute(null));

        // Even if invoked, nothing navigates away from the wizard.
        wf.FinishCommand.Execute(null);
        Assert.Equal(PageKey.Workflow, nav.CurrentPage);
        Assert.IsType<WorkflowViewModel>(main.ActiveView);
    }

    [Fact]
    public async Task Cancelled_Build_Cannot_Finish()
    {
        var slow = new SlowCancellableBuildService();
        var (_, _, wf, state, build, _, _, _) = BuildShell(slow, new FakeLocalizationService());

        MakeBuildReady(state);
        wf.GoToStep(WorkflowStep.Build);
        Assert.True(wf.IsFinalStep);

        var task = ExecuteCommandAsync(build.BuildCommand);
        await slow.Started.Task;                 // build is in flight
        build.CancelCommand.Execute(null);
        await task;

        Assert.Equal(BuildState.Cancelled, build.CurrentStage);
        Assert.False(wf.CanFinish);              // Cancelled must NOT enable Finish
        Assert.False(wf.FinishCommand.CanExecute(null));
    }

    // ---- (9) zh-CN / en-US button behavior identical ----

    [Fact]
    public void Finish_Button_Labels_Are_Localized_And_Identical_In_Behavior()
    {
        var svc = RealLocalizer(CultureInfo.GetCultureInfo("en"));
        Assert.Equal("Finish", svc["Nav.Finish"]);
        Assert.Equal("Open output folder", svc["Build.OpenOutputFolder"]);

        svc.SetCulture(CultureInfo.GetCultureInfo("zh-CN"));
        Assert.Equal("完成", svc["Nav.Finish"]);
        Assert.Equal("打开输出文件夹", svc["Build.OpenOutputFolder"]);
    }

    [Fact]
    public async Task Finish_Works_Identically_Under_ZhCn()
    {
        // The same completion→Finish→Home behavior must hold under a Chinese locale,
        // proving the gating is culture-independent (no hard-coded language checks).
        var loc = RealLocalizer(CultureInfo.GetCultureInfo("zh-CN"));
        var (main, nav, wf, state, build, _, _, _) = BuildShell(new SuccessBuildService(), loc);

        MakeBuildReady(state);
        wf.GoToStep(WorkflowStep.Build);
        await ExecuteCommandAsync(build.BuildCommand);

        Assert.True(wf.CanFinish);
        wf.FinishCommand.Execute(null);

        Assert.Equal(PageKey.Home, nav.CurrentPage);
        Assert.IsType<HomeViewModel>(main.ActiveView);
    }

    // ---- App stays running (no process exit) ----

    [Fact]
    public async Task Finish_Leaves_App_Runnable_For_New_Workflow()
    {
        var (main, _, wf, state, build, _, _, _) = BuildShell(new SuccessBuildService(), new FakeLocalizationService());

        MakeBuildReady(state);
        wf.GoToStep(WorkflowStep.Build);
        await ExecuteCommandAsync(build.BuildCommand);

        wf.FinishCommand.Execute(null);
        Assert.IsType<HomeViewModel>(main.ActiveView);

        // The app is still alive: the user can re-enter the workflow from the rail.
        main.ShowWorkflowCommand.Execute(null);
        Assert.True(main.IsWorkflowActive);
        Assert.IsType<WorkflowViewModel>(main.ActiveView);
    }
}

/// <summary>Minimal <see cref="IComponentIntelligenceService"/> stub for shell wiring tests.</summary>
internal sealed class NotDiscoveredComponentIntelligenceService : IComponentIntelligenceService
{
    public Task<ComponentInventory> DiscoverAsync(
        ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
        => Task.FromResult(new ComponentInventory { Discovered = false });
}
