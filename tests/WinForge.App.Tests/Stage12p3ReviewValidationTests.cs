using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// REGRESSION for the Phase 12 real-desktop blocker: on the Review page
/// 「校验计划」appeared to do nothing, 「应用到已挂载镜像」stayed disabled, and
/// Next stayed disabled. Investigation found:
/// (1) the successful path DID set the plan Validated, but the UI gave no
///     visible feedback at all (no success message), so the run looked dead;
/// (2) a FAILED validation (blocking issues such as Duplicate/Conflict) kept
///     showing 「没有校验警告」 because <see cref="PlanReviewViewModel.Warnings"/>
///     was replaced without notifying the derived <c>HasWarnings</c> — so the
///     real blocking reason was invisible while Apply (which keys on
///     <c>Plan.Status == Validated</c>) stayed disabled; and
/// (3) a throwing validator was swallowed silently.
///
/// The fix: ValidatePlan now sets explicit, localized outcome state
/// (<c>ValidationPassed</c> / <c>ValidationMessage</c> / <c>HasValidationFailure</c>),
/// Warnings notifies HasWarnings, exceptions surface a localized error and are
/// logged (never silent), and ApplyCommand/NextCommand re-evaluate immediately
/// (no timing hacks). Next staying disabled until Apply is EXECUTED is the
/// intended contract (Apply step is NotAvailable before execution succeeds).
/// </summary>
public class Stage12p3ReviewValidationTests
{
    private sealed class Harness
    {
        public AppState State { get; }
        public InMemoryLoggerService Logger { get; }
        public FakeCustomizationExecutionService Execution { get; }
        public PlanReviewViewModel Plan { get; }
        public WorkflowViewModel Wf { get; }
        public ComponentsViewModel Components { get; }

        public Harness(
            ILocalizationService? loc = null,
            Func<CustomizationPlan, IReadOnlyList<string>>? validate = null)
        {
            State = new AppState();
            Logger = new InMemoryLoggerService();
            Execution = new FakeCustomizationExecutionService();
            var discovery = new FakeCustomizationDiscoveryService
            {
                Inventory = new DiscoveryInventory
                {
                    Discovered = true,
                    AppxPackages = new[]
                    {
                        new DiscoveredAppxPackage { PackageName = "AppA", DisplayName = "App A", Risk = RiskClass.Removable },
                        new DiscoveredAppxPackage { PackageName = "AppB", DisplayName = "App B", Risk = RiskClass.Removable },
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
            Components = new ComponentsViewModel(State, Logger, discovery, defs);
            var knowledge = ComponentKnowledgeTestFactory.Make(State, Logger);
            var customize = new CustomizeStepViewModel(Components, knowledge,
                ComponentKnowledgeTestFactory.MakeComponentsKnowledge(State, Logger),
                ComponentKnowledgeTestFactory.MakeOptimization(State, Logger, OptimizationTab.Services),
                ComponentKnowledgeTestFactory.MakeOptimization(State, Logger, OptimizationTab.Privacy),
                ComponentKnowledgeTestFactory.MakeOptimization(State, Logger, OptimizationTab.System),
                ComponentKnowledgeTestFactory.MakeOptimization(State, Logger, OptimizationTab.Personalization));
            Plan = new PlanReviewViewModel(State, Logger, Execution, loc, validate);
            var build = new BuildStepViewModel(
                State, new FakeBuildService(), new FakeFileSystem(), new WorkflowAndCommandTests.FakeFilePicker(),
                new FakeAdkToolLocator(), Logger, new FakeLocalizationService());
            Wf = new WorkflowViewModel(State, image, customize, Plan, build);
        }

        public void MountWorkspace()
        {
            State.CurrentImageWorkspace = new ImageWorkspace();
            State.CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                WorkingDirectory = @"C:\wf\ws",
                MountDirectory = @"C:\wf\ws\mount",
                WorkingImagePath = @"C:\wf\ws\image\install.wim",
                State = ServicingWorkspaceState.Mounted
            };
        }

        /// <summary>Creates the shared plan with one selected AppX operation.</summary>
        public CustomizationPlan CreatePlanWithOneSelected()
        {
            var plan = PlanSync.EnsureDraftPlan(State);
            plan.AddOperation(new CustomizationOperation
            {
                OperationId = "op-1",
                OperationType = CustomizationOperationType.RemoveProvisionedAppx,
                TargetIdentifier = "AppA",
                DisplayName = "App A",
                Risk = RiskClass.Removable,
                IsSelected = true,
            });
            return plan;
        }

        public CustomizationOperation Operation(string id, string type, string target, bool selected,
            RiskClass risk = RiskClass.Removable, string? display = null)
            => new()
            {
                OperationId = id,
                OperationType = (CustomizationOperationType)Enum.Parse(typeof(CustomizationOperationType), type),
                TargetIdentifier = target,
                DisplayName = display ?? target,
                Risk = risk,
                IsSelected = selected,
            };
    }

    private static ResourceManagerLocalizationService RealLoc(string culture)
    {
        var rm = new System.Resources.ResourceManager(
            "WinForge.App.Resources.Strings", typeof(PlanReviewViewModel).Assembly);
        var loc = new ResourceManagerLocalizationService(rm, CultureInfo.GetCultureInfo("en"));
        loc.SetCulture(CultureInfo.GetCultureInfo(culture));
        return loc;
    }

    private static void EnterReview(Harness h)
    {
        h.MountWorkspace();
        h.CreatePlanWithOneSelected();
        h.Wf.GoToStep(WorkflowStep.Customize);
        h.Wf.GoToStep(WorkflowStep.Review);
    }

    // 1. plan exists before validation
    [Fact]
    public void Plan_Exists_Before_Validation()
    {
        var h = new Harness();
        EnterReview(h);
        Assert.NotNull(h.Plan.Plan);
        Assert.Single(h.Plan.Plan!.SelectedOperations);
        Assert.Equal(WorkflowStep.Review, h.Wf.CurrentStep!.Step);
    }

    // 2 + 3. Validate command executes and sets validated state (through the ICommand surface)
    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void ValidateCommand_Executes_And_Sets_Validated(string culture)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

        var h = new Harness();
        EnterReview(h);

        Assert.True(h.Plan.ValidateCommand.CanExecute(null));
        h.Plan.ValidateCommand.Execute(null); // the exact click path

        Assert.Equal(CustomizationPlanStatus.Validated, h.State.CurrentCustomizationPlan!.Status);
        Assert.True(h.Plan.ValidationPassed);
    }

    // 4. success feedback visible
    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void Successful_Validation_Sets_Visible_Success_Feedback(string culture)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

        var h = new Harness(RealLoc(culture));
        EnterReview(h);

        h.Plan.ValidatePlan();

        Assert.True(h.Plan.ValidationPassed);
        Assert.True(h.Plan.HasValidationMessage);
        Assert.False(h.Plan.HasValidationFailure);
        Assert.Equal(RealLoc(culture)["Review.ValidatePassed"], h.Plan.ValidationMessage);
    }

    // 5. Apply disabled before validation
    [Fact]
    public void Apply_Disabled_Before_Validation()
    {
        var h = new Harness();
        EnterReview(h);
        Assert.False(h.Plan.ApplyCommand.CanExecute(null));
        Assert.Equal(CustomizationPlanStatus.Draft, h.State.CurrentCustomizationPlan!.Status);
    }

    // 6. Apply enabled immediately after successful validation (no reload/navigation)
    [Fact]
    public void Apply_Enabled_Immediately_After_Validation()
    {
        var h = new Harness();
        EnterReview(h);
        Assert.False(h.Plan.ApplyCommand.CanExecute(null));

        h.Plan.ValidatePlan();

        Assert.True(h.Plan.ApplyCommand.CanExecute(null));
        // Next stays disabled until Apply is EXECUTED (Apply step NotAvailable) —
        // this is the intended contract, not the bug.
        Assert.False(h.Wf.NextCommand.CanExecute(null));
    }

    // 7. an issue on an UNSELECTED operation is non-blocking
    [Fact]
    public void Unselected_Issue_Does_Not_Block_Validation()
    {
        var h = new Harness();
        h.MountWorkspace();
        var plan = h.CreatePlanWithOneSelected();
        // Unsupported + unselected: classified Unsupported but excluded from gating.
        plan.AddOperation(h.Operation("op-bad", "RemoveProvisionedAppx", "NeverRemove", selected: false, risk: RiskClass.Protected));
        h.Wf.GoToStep(WorkflowStep.Customize);
        h.Wf.GoToStep(WorkflowStep.Review);

        h.Plan.ValidatePlan();

        Assert.Equal(CustomizationPlanStatus.Validated, h.State.CurrentCustomizationPlan!.Status);
        Assert.True(h.Plan.ValidationPassed);
        Assert.True(h.Plan.ApplyCommand.CanExecute(null));
    }

    // 8. blocking validation (duplicate conflict key) keeps Apply disabled and shows the reason
    [Fact]
    public void Blocking_Validation_Keeps_Apply_Disabled_And_Shows_Reason()
    {
        var h = new Harness();
        h.MountWorkspace();
        var plan = h.CreatePlanWithOneSelected();
        plan.AddOperation(h.Operation("op-dup", "RemoveProvisionedAppx", "AppA", selected: true, display: "App A (again)"));
        h.Wf.GoToStep(WorkflowStep.Customize);
        h.Wf.GoToStep(WorkflowStep.Review);

        h.Plan.ValidatePlan();

        Assert.Equal(CustomizationPlanStatus.Draft, h.State.CurrentCustomizationPlan!.Status);
        Assert.False(h.Plan.ValidationPassed);
        Assert.True(h.Plan.HasValidationFailure);
        Assert.True(h.Plan.HasWarnings);          // the warning list IS visible now
        Assert.False(h.Plan.ApplyCommand.CanExecute(null));
        Assert.Contains(h.Plan.Warnings, w => w.Contains("AppA", StringComparison.Ordinal));
    }

    // 9. validation exception surfaces a localized error (never silent)
    [Fact]
    public void Validation_Exception_Surfaces_Error()
    {
        var h = new Harness(
            RealLoc("en-US"),
            validate: _ => throw new InvalidOperationException("boom-validation"));
        h.MountWorkspace();
        h.CreatePlanWithOneSelected();
        h.Wf.GoToStep(WorkflowStep.Customize);
        h.Wf.GoToStep(WorkflowStep.Review);

        h.Plan.ValidatePlan();

        Assert.False(h.Plan.ValidationPassed);
        Assert.True(h.Plan.HasValidationFailure);
        Assert.Contains("boom-validation", h.Plan.ValidationMessage, StringComparison.Ordinal);
        Assert.False(h.Plan.ApplyCommand.CanExecute(null));
        Assert.Contains(h.Logger.Entries, e => e.Level == LogLevel.Error
            && e.Message.Contains("boom-validation", StringComparison.Ordinal));
    }

    // 10. CanExecuteChanged fires after validation (proper invalidation, no timing hacks)
    [Fact]
    public void CanExecuteChanged_Fires_After_Validation()
    {
        var h = new Harness();
        EnterReview(h);

        var applyChanged = 0;
        var validateChanged = 0;
        h.Plan.ApplyCommand.CanExecuteChanged += (_, _) => applyChanged++;
        h.Plan.ValidateCommand.CanExecuteChanged += (_, _) => validateChanged++;

        h.Plan.ValidatePlan();

        Assert.True(applyChanged > 0);
        Assert.True(validateChanged > 0);
        Assert.True(h.Plan.ApplyCommand.CanExecute(null));
    }

    // 11. mounted workspace state respected
    [Fact]
    public void Unmounted_Workspace_Disables_Validate_And_Apply()
    {
        var h = new Harness();
        // No MountWorkspace(): workspace absent => not mounted.
        h.State.CurrentServicingWorkspace = new ImageServicingWorkspace
        {
            WorkingDirectory = @"C:\wf\ws",
            MountDirectory = @"C:\wf\ws\mount",
            WorkingImagePath = @"C:\wf\ws\image\install.wim",
            State = ServicingWorkspaceState.Prepared
        };
        h.CreatePlanWithOneSelected();

        Assert.False(h.Plan.ValidateCommand.CanExecute(null));
        Assert.False(h.Plan.ApplyCommand.CanExecute(null));
        Assert.False(h.Plan.CanValidate);
        Assert.False(h.Plan.CanApply);
    }

    // 12. zh-CN / en-US outcome strings come from the real resx
    [Theory]
    [InlineData("en-US", "Plan validation passed")]
    [InlineData("zh-CN", "计划校验通过")]
    public void Validation_Strings_Are_Localized(string culture, string expectedFragment)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

        var h = new Harness(RealLoc(culture));
        EnterReview(h);
        h.Plan.ValidatePlan();

        Assert.Contains(expectedFragment, h.Plan.ValidationMessage, StringComparison.Ordinal);

        // Failure message is localized too.
        var h2 = new Harness(RealLoc(culture));
        h2.MountWorkspace();
        var plan = h2.CreatePlanWithOneSelected();
        plan.AddOperation(h2.Operation("op-dup", "RemoveProvisionedAppx", "AppA", selected: true));
        h2.Wf.GoToStep(WorkflowStep.Customize);
        h2.Wf.GoToStep(WorkflowStep.Review);
        h2.Plan.ValidatePlan();
        Assert.True(h2.Plan.HasValidationFailure);
        Assert.Contains(culture == "zh-CN" ? "阻塞问题" : "blocking", h2.Plan.ValidationMessage, StringComparison.Ordinal);
    }
}
