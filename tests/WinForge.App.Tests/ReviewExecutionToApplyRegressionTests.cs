using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using WinForge.App.Mvvm;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.App.Workflow;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// REGRESSION for the real-desktop defect: the customization plan executes
/// successfully ("Execution completed successfully.") but the wizard never
/// advances to Apply — Review stays "In Progress", Apply stays Not Available,
/// Next stays disabled.
///
/// <para>Root cause: <see cref="WorkflowViewModel.RecomputeStates"/> gated the
/// Apply (commit) step and Review completion on <c>plan.Status == Validated</c>.
/// But execution flips the LIVE plan to <see cref="CustomizationPlanStatus.Completed"/>
/// (or CompletedWithErrors/Failed) — so after a successful run the plan is no
/// longer Validated and the gate collapsed Apply to NotAvailable. The fix keys
/// the gate on execution SUCCESS (<see cref="CustomizationExecutionState"/>
/// Completed / CompletedWithErrors) instead of on Validated. The execution state
/// itself was always notified correctly (hypothesis #2 was false) and the plan's
/// in-place status change was already forwarded (hypothesis #3 was false) — the
/// predicate simply ignored both.</para>
///
/// <para>These tests drive ONE persistent AppState + WorkflowViewModel + the real
/// customization VMs, run the real <see cref="PlanReviewViewModel.ApplyAsync"/>,
/// and prove the live chain: execution success -> plan.MarkCompleted (in-place,
/// observable) + CustomizationExecutionState -> WorkflowViewModel.RecomputeStates
/// -> Review Completed, Apply Available, NextCommand.CanExecuteChanged fired.</para>
/// </summary>
public class ReviewExecutionToApplyRegressionTests
{
    private sealed class Harness
    {
        public AppState State { get; }
        public WorkflowViewModel Wf { get; }
        public ComponentsViewModel Components { get; }
        public PrivacyViewModel Privacy { get; }
        public SystemViewModel System { get; }
        public PlanReviewViewModel Plan { get; }
        public FakeCustomizationExecutionService Execution { get; }

        public Harness()
        {
            State = new AppState();
            var logger = new InMemoryLoggerService();
            Execution = new FakeCustomizationExecutionService();
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
                State, logger,
                new WorkflowAndCommandTests.FakeInspection(),
                new WorkflowAndCommandTests.FakeFilePicker(),
                new WorkflowAndCommandTests.FakeWorkspaceFactory(),
                new WorkflowAndCommandTests.FakeWimService(),
                new FakeImageServicingService());
            Components = new ComponentsViewModel(State, logger, discovery, defs);
            Privacy = new PrivacyViewModel(State, logger, defs);
            System = new SystemViewModel(State, logger, defs);
            var comingSoon = new ComingSoonViewModel();
            var knowledge = ComponentKnowledgeTestFactory.Make(State, logger);
            var customize = new CustomizeStepViewModel(Components, knowledge,
                ComponentKnowledgeTestFactory.MakeComponentsKnowledge(State, logger),
                ComponentKnowledgeTestFactory.MakeOptimization(State, logger, OptimizationTab.Services),
                ComponentKnowledgeTestFactory.MakeOptimization(State, logger, OptimizationTab.Privacy),
                ComponentKnowledgeTestFactory.MakeOptimization(State, logger, OptimizationTab.System),
                ComponentKnowledgeTestFactory.MakeOptimization(State, logger, OptimizationTab.Personalization));
            Plan = new PlanReviewViewModel(State, logger, Execution);
            var build = new BuildStepViewModel(
                State, new FakeBuildService(), new FakeFileSystem(), new WorkflowAndCommandTests.FakeFilePicker(),
                new FakeAdkToolLocator(), logger, new FakeLocalizationService());
            Wf = new WorkflowViewModel(State, image, customize, Plan, build);
        }

        /// <summary>
        /// Drives Source complete -> Prepare mounted -> Customize (1 valid op) ->
        /// Review -> validated, but execution NOT yet started. The expected
        /// pre-execution state: Review Current, Apply NotAvailable, Next disabled.
        /// </summary>
        public async Task SetupValidatedReviewAsync()
        {
            State.CurrentImageWorkspace = new ImageWorkspace();
            State.CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                WorkingDirectory = @"C:\wf\ws",
                MountDirectory = @"C:\wf\ws\mount",
                WorkingImagePath = @"C:\wf\ws\image\install.wim",
                State = ServicingWorkspaceState.Mounted
            };
            Wf.GoToStep(WorkflowStep.Customize);
            await Components.DiscoverAsync();
            Components.AppxPackages[0].IsSelected = true; // exactly one valid operation
            Wf.GoToStep(WorkflowStep.Review);
            Plan.ValidatePlan(); // plan -> Validated, exec -> Ready

            // Precondition: validated but not executed => Review Current, Apply hidden.
            Assert.Equal(WorkflowStep.Review, Wf.CurrentStep!.Step);
            Assert.Equal(WorkflowStepState.Current, Wf.Steps[3].State);
            Assert.Equal(WorkflowStepState.NotAvailable, Wf.Steps[4].State);
            Assert.False(Wf.NextCommand.CanExecute(null));
            Assert.Equal(CustomizationPlanStatus.Validated, State.CurrentCustomizationPlan!.Status);
        }
    }

    // ---- Primary success scenario (en-US / zh-CN identical gating). ----

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public async Task ExecutionSuccess_CompletesReview_AndUnlocksApply_AndNext(string culture)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

        var h = new Harness();
        await h.SetupValidatedReviewAsync();

        // Track CanExecuteChanged around the execution.
        var nextChanged = 0;
        h.Wf.NextCommand.CanExecuteChanged += (_, _) => nextChanged++;

        // Execute successfully.
        h.Execution.Result = new CustomizationResult
        {
            TotalOperations = 1,
            Succeeded = 1,
            FailedOperations = 0
        };
        await h.Plan.ApplyAsync();

        // Assertions (per the defect's required post-conditions):
        Assert.Equal(CustomizationExecutionState.Completed, h.State.CustomizationExecutionState);
        Assert.Equal(CustomizationPlanStatus.Completed, h.State.CurrentCustomizationPlan!.Status);
        Assert.Equal(WorkflowStepState.Completed, h.Wf.Steps[3].State);   // Review Completed
        Assert.Equal(WorkflowStepState.Available, h.Wf.Steps[4].State);  // Apply Available
        Assert.True(h.Wf.NextCommand.CanExecute(null));                  // Next enabled
        Assert.True(nextChanged > 0);                                    // CanExecuteChanged fired

        // Execute Next -> Apply becomes Current, Review stays Completed.
        h.Wf.GoNext();
        Assert.Equal(WorkflowStep.Apply, h.Wf.CurrentStep!.Step);
        Assert.Equal(WorkflowStepState.Current, h.Wf.Steps[4].State);
        Assert.Equal(WorkflowStepState.Completed, h.Wf.Steps[3].State);
    }

    // ---- Failure path: execution fails => Review stays Current, Apply hidden,
    // Next disabled (and the plan is left untouched, still Validated). ----

    [Fact]
    public async Task ExecutionFailure_KeepsReviewCurrent_ApplyHidden_NextDisabled()
    {
        var h = new Harness();
        await h.SetupValidatedReviewAsync();

        var nextChanged = 0;
        h.Wf.NextCommand.CanExecuteChanged += (_, _) => nextChanged++;

        h.Execution.Result = new CustomizationResult { CriticalFailure = true };
        await h.Plan.ApplyAsync();

        Assert.Equal(CustomizationExecutionState.Failed, h.State.CustomizationExecutionState);
        // Guard failure leaves the live plan Validated (not frozen/marked).
        Assert.Equal(CustomizationPlanStatus.Validated, h.State.CurrentCustomizationPlan!.Status);
        Assert.Equal(WorkflowStepState.Current, h.Wf.Steps[3].State);     // Review remains Current
        Assert.Equal(WorkflowStepState.NotAvailable, h.Wf.Steps[4].State); // Apply hidden
        Assert.False(h.Wf.NextCommand.CanExecute(null));                  // Next disabled
        Assert.True(nextChanged > 0);
    }

    // ---- CompletedWithErrors is still a success-terminal state: Apply unlocks,
    // Review completes, Next enables (no repeated execution required). ----

    [Fact]
    public async Task ExecutionCompletedWithErrors_StillUnlocksApply()
    {
        var h = new Harness();
        await h.SetupValidatedReviewAsync();

        h.Execution.Result = new CustomizationResult
        {
            TotalOperations = 1,
            Succeeded = 0,
            FailedOperations = 1
        };
        await h.Plan.ApplyAsync();

        Assert.Equal(CustomizationExecutionState.CompletedWithErrors, h.State.CustomizationExecutionState);
        Assert.Equal(CustomizationPlanStatus.CompletedWithErrors, h.State.CurrentCustomizationPlan!.Status);
        Assert.Equal(WorkflowStepState.Completed, h.Wf.Steps[3].State);
        Assert.Equal(WorkflowStepState.Available, h.Wf.Steps[4].State);
        Assert.True(h.Wf.NextCommand.CanExecute(null));
    }

    // ---- Re-entrancy / convergence: the in-place plan status change (MarkCompleted)
    // forwards through AppState WITHOUT creating a notification loop, and gating
    // settles on Available once. ----

    [Fact]
    public async Task ExecutionSuccess_NoReentrancyLoop_PlanStatusForwarded()
    {
        var h = new Harness();
        await h.SetupValidatedReviewAsync();

        var reviewChanged = 0;
        var applyChanged = 0;
        h.Wf.Steps[3].PropertyChanged += (_, _) => reviewChanged++;
        h.Wf.Steps[4].PropertyChanged += (_, _) => applyChanged++;

        h.Execution.Result = new CustomizationResult { TotalOperations = 1, Succeeded = 1, FailedOperations = 0 };
        await h.Plan.ApplyAsync();

        // Both steps settled on their post-execution states without spinning.
        Assert.Equal(WorkflowStepState.Completed, h.Wf.Steps[3].State);
        Assert.Equal(WorkflowStepState.Available, h.Wf.Steps[4].State);
        Assert.True(reviewChanged > 0);
        Assert.True(applyChanged > 0);
    }
}
