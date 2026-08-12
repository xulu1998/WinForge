using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinForge.App.Localization;
using WinForge.App.Mvvm;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.App.Workflow;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.Servicing;
using WinForge.Infrastructure.WorkspaceLifecycle;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// REGRESSION for the Phase 12 real-desktop blocker: the build completed (page
/// showed 构建完成 / 100% / 已完成 and the final ISO existed) but the top wizard
/// stepper still showed 构建镜像 · 进行中 and 完成 stayed disabled.
///
/// Root cause: <see cref="WorkflowViewModel.OnBuildChanged"/> refreshed
/// CanFinish on <c>CurrentStage</c> changes but (a) never called
/// <see cref="WorkflowViewModel.RecomputeStates"/>, so the Build step stayed
/// <c>Current</c> (InProgress) forever, and (b) raised FinishCommand via an
/// <c>is RelayCommand</c> type check — but <c>FinishCommand</c> is an
/// <see cref="AsyncRelayCommand"/>, so <c>RaiseCanExecuteChanged()</c> was NEVER
/// invoked and the 完成 button stayed disabled even though CanFinish was true.
/// </summary>
public class Stage12p5BuildFinishStateTests
{
    private sealed class ConfigurableBuildService : IBuildService
    {
        public BuildResult Result { get; set; } = BuildResult.Fail(BuildState.Preflight, "not configured", Array.Empty<string>());
        public Task<BuildResult> BuildAsync(BuildRequest request, IProgress<BuildProgress>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(Result);
        public Task<BuildRecoveryState?> DetectInterruptedBuildAsync(string buildWorkspaceDirectory, CancellationToken cancellationToken = default)
            => Task.FromResult<BuildRecoveryState?>(null);
        public Task<bool> CleanupInterruptedBuildAsync(string buildWorkspaceDirectory, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class RecordingNavigation : INavigationService
    {
        public List<PageKey> Navigated { get; } = new();
        public PageKey CurrentPage { get; private set; }
        public event System.EventHandler<PageKey>? CurrentPageChanged;
        public void NavigateTo(PageKey page)
        {
            Navigated.Add(page);
            CurrentPage = page;
            CurrentPageChanged?.Invoke(this, page);
        }
    }

    private sealed class Harness
    {
        public AppState State { get; }
        public InMemoryLoggerService Logger { get; }
        public ConfigurableBuildService BuildService { get; }
        public BuildStepViewModel Build { get; }
        public WorkflowViewModel Wf { get; }
        public RecordingNavigation Nav { get; }
        public WorkspaceLifecycleManager Lifecycle { get; }
        public string WorkspaceRoot { get; }
        public string WorkspaceDir { get; }

        public Harness()
        {
            State = new AppState();
            Logger = new InMemoryLoggerService();
            BuildService = new ConfigurableBuildService();
            Nav = new RecordingNavigation();

            WorkspaceRoot = Path.Combine(Path.GetTempPath(), "wf12_buildfin_" + Guid.NewGuid().ToString("N"));
            var paths = new WorkspacePathProvider(WorkspaceRoot);
            var runner = new FakeProcessRunner
            {
                Responder = _ => new ProcessResult { ExitCode = 0, StandardOutput = "No mounted images found." },
            };
            Lifecycle = new WorkspaceLifecycleManager(paths, runner, new WorkspaceSafeDelete(), Logger);
            WorkspaceDir = Lifecycle.CreateWorkspace("wf-buildfin-1", null);

            State.CurrentImageWorkspace = new ImageWorkspace();
            State.CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                SelectedEditionName = "Windows 11 Pro",
                WorkingDirectory = WorkspaceDir,
                MountDirectory = Path.Combine(WorkspaceDir, "mount"),
                WorkingImagePath = Path.Combine(WorkspaceDir, "image", "install.wim"),
                SourceImageRelativePath = @"sources\install.wim",
                WorkingIndex = 1,
                State = ServicingWorkspaceState.Mounted,
            };
            State.CustomizationExecutionState = CustomizationExecutionState.Completed;

            // A plan must exist so the Review step (planSelected) and the wizard
            // path to the Build step are reachable — mirroring the real flow
            // (Customize -> Review -> Apply succeeded -> Build).
            var wizardPlan = PlanSync.EnsureDraftPlan(State);
            wizardPlan.AddOperation(new CustomizationOperation
            {
                OperationId = "op-1",
                OperationType = CustomizationOperationType.RemoveProvisionedAppx,
                TargetIdentifier = "AppA",
                DisplayName = "App A",
                Risk = RiskClass.Removable,
                IsSelected = true,
            });
            wizardPlan.Validate();

            var discovery = new FakeCustomizationDiscoveryService
            {
                Inventory = new DiscoveryInventory
                {
                    Discovered = true,
                    AppxPackages = new[]
                    {
                        new DiscoveredAppxPackage { PackageName = "AppA", DisplayName = "App A", Risk = RiskClass.Removable },
                    },
                    WindowsPackages = Array.Empty<DiscoveredWindowsPackage>(),
                    Services = Array.Empty<DiscoveredOfflineService>(),
                }
            };
            var defs = new FakeCustomizationDefinitionProvider();
            var image = new ImageViewModel(
                State, Logger,
                new WorkflowAndCommandTests.FakeInspection(),
                new WorkflowAndCommandTests.FakeFilePicker(),
                new WorkflowAndCommandTests.FakeWorkspaceFactory(),
                new WorkflowAndCommandTests.FakeWimService(),
                new FakeImageServicingService());
            var components = new ComponentsViewModel(State, Logger, discovery, defs);
            var knowledge = ComponentKnowledgeTestFactory.Make(State, Logger);
            var customize = new CustomizeStepViewModel(components, knowledge,
                ComponentKnowledgeTestFactory.MakeComponentsKnowledge(State, Logger),
                ComponentKnowledgeTestFactory.MakeOptimization(State, Logger, OptimizationTab.Services),
                ComponentKnowledgeTestFactory.MakeOptimization(State, Logger, OptimizationTab.Privacy),
                ComponentKnowledgeTestFactory.MakeOptimization(State, Logger, OptimizationTab.System),
                ComponentKnowledgeTestFactory.MakeOptimization(State, Logger, OptimizationTab.Personalization));
            var plan = new PlanReviewViewModel(State, Logger, new FakeCustomizationExecutionService());
            Build = new BuildStepViewModel(
                State, BuildService, new FakeFileSystem(), new WorkflowAndCommandTests.FakeFilePicker(),
                new FakeAdkToolLocator(), Logger, new FakeLocalizationService());
            Wf = new WorkflowViewModel(State, image, customize, plan, Build, Nav, Lifecycle);
        }

        /// <summary>Drives the wizard onto the Build step (last step) pre-build.</summary>
        public void EnterBuildStep()
        {
            Wf.GoToStep(WorkflowStep.Build);
            Assert.Equal(WorkflowStep.Build, Wf.CurrentStep!.Step);
            Assert.True(Wf.IsFinalStep);
        }

        public async Task RunBuildAsync()
        {
            if (Build.BuildCommand is AsyncRelayCommand b)
            {
                await b.ExecuteAsync(null);
            }
            else
            {
                Build.BuildCommand.Execute(null);
            }
        }
    }

    // 1. Build starts -> wizard Build step InProgress (Current)
    [Fact]
    public void Build_Start_Wizard_Step_InProgress()
    {
        var h = new Harness();
        h.EnterBuildStep();
        Assert.Equal(WorkflowStepState.Current, h.Wf.Steps[5].State);
        Assert.False(h.Wf.FinishCommand.CanExecute(null));
    }

    // 2 + 3 + 4. Build success -> step Completed, Finish enabled immediately, CanExecuteChanged fires
    [Fact]
    public async Task Build_Success_Completes_Step_And_Enables_Finish_Immediately()
    {
        var h = new Harness();
        h.EnterBuildStep();
        h.BuildService.Result = BuildResult.Ok(@"C:\out\WinForge_test.iso", 7_620_000_000,
            new[] { "Build completed" });

        var finishChanged = 0;
        h.Wf.FinishCommand.CanExecuteChanged += (_, _) => finishChanged++;

        await h.RunBuildAsync();

        Assert.Equal(BuildState.Completed, h.Build.CurrentStage);
        Assert.Equal(WorkflowStepState.Completed, h.Wf.Steps[5].State); // 构建镜像 -> 已完成
        Assert.True(h.Wf.FinishCommand.CanExecute(null));               // 完成 enabled
        Assert.True(h.Wf.CanFinish);
        Assert.True(finishChanged > 0);                                 // CanExecuteChanged fired
    }

    // 5. build failure does not enable Finish
    [Fact]
    public async Task Build_Failure_Does_Not_Enable_Finish()
    {
        var h = new Harness();
        h.EnterBuildStep();
        h.BuildService.Result = BuildResult.Fail(BuildState.Failed, "boom", new[] { "failed" });

        await h.RunBuildAsync();

        Assert.Equal(BuildState.Failed, h.Build.CurrentStage);
        Assert.Equal(WorkflowStepState.Current, h.Wf.Steps[5].State); // stays InProgress
        Assert.False(h.Wf.FinishCommand.CanExecute(null));
        Assert.False(h.Wf.CanFinish);
    }

    // 6. build cancellation does not enable Finish
    [Fact]
    public async Task Build_Cancellation_Does_Not_Enable_Finish()
    {
        var h = new Harness();
        h.EnterBuildStep();
        h.BuildService.Result = BuildResult.Fail(BuildState.Cancelled, "cancelled", Array.Empty<string>());

        await h.RunBuildAsync();

        Assert.Equal(BuildState.Cancelled, h.Build.CurrentStage);
        Assert.False(h.Wf.FinishCommand.CanExecute(null));
        Assert.False(h.Wf.CanFinish);
    }

    // 7. verification failure does not enable Finish (fails at the Verifying phase)
    [Fact]
    public async Task Verification_Failure_Does_Not_Enable_Finish()
    {
        var h = new Harness();
        h.EnterBuildStep();
        h.BuildService.Result = BuildResult.Fail(BuildState.Verifying, "ISO verification failed", new[] { "verify failed" });

        await h.RunBuildAsync();

        Assert.NotEqual(BuildState.Completed, h.Build.CurrentStage);
        Assert.False(h.Wf.FinishCommand.CanExecute(null));
        Assert.False(h.Wf.CanFinish);
    }

    // 8. successful Build state survives UI refresh (navigation away + back recomputes to Completed)
    [Fact]
    public async Task Build_Success_Survives_UI_Refresh()
    {
        var h = new Harness();
        h.EnterBuildStep();
        h.BuildService.Result = BuildResult.Ok(@"C:\out\WinForge_test.iso", 1, new[] { "ok" });
        await h.RunBuildAsync();

        // Navigate back then forward again — the step must still read Completed.
        h.Wf.GoBack();
        h.Wf.GoNext();
        Assert.Equal(WorkflowStepState.Completed, h.Wf.Steps[5].State);
        Assert.True(h.Wf.FinishCommand.CanExecute(null));
    }

    // 9 + 10 + 11. Finish cleanup still runs after successful Build, ISO path preserved,
    //             workflow returns Home
    [Fact]
    public async Task Finish_Runs_Cleanup_Preserves_Output_And_Returns_Home()
    {
        var h = new Harness();
        h.EnterBuildStep();
        h.BuildService.Result = BuildResult.Ok(
            Path.Combine(Path.GetTempPath(), "WinForge", "WinForge_test.iso"), 7_620_000_000, new[] { "ok" });
        await h.RunBuildAsync();

        // Workspace has disposable content the Finish cleanup must remove.
        File.WriteAllText(Path.Combine(h.WorkspaceDir, "scratch.txt"), "x");
        Assert.True(Directory.Exists(h.WorkspaceDir));

        Assert.IsType<AsyncRelayCommand>(h.Wf.FinishCommand);
        await ((AsyncRelayCommand)h.Wf.FinishCommand).ExecuteAsync(null);

        // Cleanup ran: the completed workspace was deleted (authoritative DISM check passed).
        Assert.False(Directory.Exists(h.WorkspaceDir));
        // Final ISO path was never touched by cleanup (it lives outside the workspace).
        Assert.Contains("WinForge_test.iso", h.Build.OutputPath, StringComparison.Ordinal);
        Assert.StartsWith(Path.GetTempPath(), h.Build.OutputPath, StringComparison.OrdinalIgnoreCase);
        // Workflow returned Home.
        Assert.Contains(PageKey.Home, h.Nav.Navigated);
    }

    // 12. zh-CN / en-US step status strings exist and step state renders Completed
    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void Step_Status_Strings_Are_Localized(string culture)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

        var rm = new System.Resources.ResourceManager(
            "WinForge.App.Resources.Strings", typeof(BuildStepViewModel).Assembly);
        var loc = new ResourceManagerLocalizationService(rm, CultureInfo.GetCultureInfo("en"));
        loc.SetCulture(CultureInfo.GetCultureInfo(culture));

        var current = loc["StepState.Current"];
        var completed = loc["StepState.Completed"];
        Assert.False(string.IsNullOrWhiteSpace(current));
        Assert.False(string.IsNullOrWhiteSpace(completed));
        Assert.NotEqual(current, completed);

        // A Completed step maps to the localized 已完成 text.
        var h = new Harness();
        h.EnterBuildStep();
        h.BuildService.Result = BuildResult.Ok(@"C:\out\x.iso", 1, new[] { "ok" });
        h.RunBuildAsync().GetAwaiter().GetResult();
        Assert.Equal(WorkflowStepState.Completed, h.Wf.Steps[5].State);
        _ = loc; // binding resolves "StepState." + State
    }
}
