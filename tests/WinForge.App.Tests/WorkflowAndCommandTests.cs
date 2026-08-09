using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinForge.App.FriendlyMetadata;
using WinForge.App.Mvvm;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.App.Workflow;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// WORKFLOW gating / navigation / safety and COMMANDS CanExecute tests for the
/// wizard refactor. Builds a full <see cref="WorkflowViewModel"/> from minimal
/// fakes so no ISO, mount, or UI is required (CI-safe).
/// </summary>
public class WorkflowAndCommandTests
{
    // ---- Minimal fakes for ImageViewModel's dependency graph ----

    internal sealed class FakeInspection : IIsoInspectionService
    {
        public Task<IsoInspectionResult> InspectAsync(string isoPath, CancellationToken ct = default)
            => Task.FromResult(IsoInspectionResult.Failed(isoPath, "unused"));
    }

    internal sealed class FakeFilePicker : IFilePicker
    {
        public string? PickIsoFile() => null;
    }

    internal sealed class FakeWorkspaceFactory : IImageWorkspaceFactory
    {
        public ImageWorkspaceBuildResult BuildWorkspace(IsoInspectionResult inspection, WindowsEditionInfo? selectedEdition)
            => new(null, ImageWorkspaceStatus.NotReady, Array.Empty<string>());
    }

    internal sealed class FakeWimService : IWimService
    {
        public ImageWorkspaceStatus ValidateWorkspace(ImageWorkspace workspace) => ImageWorkspaceStatus.NotReady;
        public SelectedImageContext? ResolveSelectedImage(ImageWorkspace workspace) => null;
    }

    internal static (WorkflowViewModel Wf, AppState State) Build()
    {
        var state = new AppState();
        var logger = new InMemoryLoggerService();
        var image = new ImageViewModel(
            state, logger,
            new FakeInspection(), new FakeFilePicker(),
            new FakeWorkspaceFactory(), new FakeWimService(),
            new FakeImageServicingService());
        var components = new ComponentsViewModel(state, logger, new FakeCustomizationDiscoveryService(), new FakeCustomizationDefinitionProvider());
        var privacy = new PrivacyViewModel(state, logger, new FakeCustomizationDefinitionProvider());
        var system = new SystemViewModel(state, logger, new FakeCustomizationDefinitionProvider());
        var comingSoon = new ComingSoonViewModel();
        var customize = new CustomizeStepViewModel(components, privacy, system, comingSoon);
        var plan = new PlanReviewViewModel(state, logger, new FakeCustomizationExecutionService());
        var build = new BuildStepViewModel(state);
        return (new WorkflowViewModel(state, image, customize, plan, build), state);
    }

    internal static CustomizationPlan SelectedPlan()
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

    // ---- WORKFLOW: gating ----

    [Fact]
    public void Workflow_Initial_State_Source_Current_Build_Available_Rest_NotAvailable()
    {
        var (wf, _) = Build();
        Assert.Equal(WorkflowStep.Source, wf.CurrentStep!.Step);
        Assert.Equal(WorkflowStepState.Current, wf.Steps[0].State);      // Source active
        Assert.Equal(WorkflowStepState.NotAvailable, wf.Steps[1].State); // Prepare
        Assert.Equal(WorkflowStepState.NotAvailable, wf.Steps[2].State); // Customize
        Assert.Equal(WorkflowStepState.NotAvailable, wf.Steps[3].State); // Review
        Assert.Equal(WorkflowStepState.NotAvailable, wf.Steps[4].State); // Apply
        Assert.Equal(WorkflowStepState.Available, wf.Steps[5].State);    // Build placeholder always reachable
        Assert.Single(wf.Steps.Where(s => s.State == WorkflowStepState.Current));
    }

    [Fact]
    public void Workflow_ImageWorkspace_Makes_Prepare_Available()
    {
        var (wf, state) = Build();
        state.CurrentImageWorkspace = new ImageWorkspace();
        // The active step is always shown as Current; setting the image unlocks
        // Prepare as Available and the user may advance.
        Assert.Equal(WorkflowStepState.Current, wf.Steps[0].State);
        Assert.Equal(WorkflowStepState.Available, wf.Steps[1].State);
        Assert.True(wf.CanGoNext);
    }

    [Fact]
    public void Workflow_Mounted_Servicing_Makes_Customize_Available()
    {
        var (wf, state) = Build();
        state.CurrentImageWorkspace = new ImageWorkspace();
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        Assert.Equal(WorkflowStepState.Available, wf.Steps[2].State); // Customize
    }

    [Fact]
    public void Workflow_SelectedPlan_Makes_Review_Available()
    {
        var (wf, state) = Build();
        state.CurrentImageWorkspace = new ImageWorkspace();
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        state.CurrentCustomizationPlan = SelectedPlan();
        Assert.Equal(WorkflowStepState.Available, wf.Steps[3].State); // Review
    }

    [Fact]
    public void Workflow_ValidatedPlan_Makes_Apply_Available()
    {
        var (wf, state) = Build();
        state.CurrentImageWorkspace = new ImageWorkspace();
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        var plan = SelectedPlan();
        plan.Validate();
        state.CurrentCustomizationPlan = plan;
        Assert.Equal(CustomizationPlanStatus.Validated, plan.Status);
        Assert.Equal(WorkflowStepState.Available, wf.Steps[4].State); // Apply
    }

    [Fact]
    public void Workflow_Build_Step_Is_Always_Available_Placeholder()
    {
        var (wf, _) = Build();
        Assert.Equal(WorkflowStepState.Available, wf.Steps[5].State); // Build reachable end to end
    }

    // ---- WORKFLOW: navigation ----

    [Fact]
    public void Workflow_CanGoNext_True_Once_Prepare_Is_Available()
    {
        var (wf, state) = Build();
        Assert.False(wf.CanGoNext);
        state.CurrentImageWorkspace = new ImageWorkspace();
        Assert.True(wf.CanGoNext);
    }

    [Fact]
    public void Workflow_CanGoBack_False_At_First_Step()
    {
        var (wf, _) = Build();
        Assert.False(wf.CanGoBack);
    }

    [Fact]
    public void Workflow_GoNext_And_GoBack_Move_Current_Step()
    {
        var (wf, state) = Build();
        state.CurrentImageWorkspace = new ImageWorkspace();
        wf.GoNext();
        Assert.Equal(WorkflowStep.Prepare, wf.CurrentStep!.Step);
        wf.GoBack();
        Assert.Equal(WorkflowStep.Source, wf.CurrentStep!.Step);
    }

    [Fact]
    public void Workflow_CanGoToStep_Refuses_NotAvailable_Step()
    {
        var (wf, _) = Build();
        Assert.False(wf.CanGoToStep(WorkflowStep.Customize));
        Assert.False(wf.CanGoToStep(WorkflowStep.Review));
        // Build is Available in isolation, but an intermediate step (Prepare) is
        // NotAvailable, so the skip-guard refuses the direct jump until the
        // prerequisites are met.
        Assert.False(wf.CanGoToStep(WorkflowStep.Build));
    }

    [Fact]
    public void Workflow_CanGoToStep_Build_Open_When_Prerequisites_Met()
    {
        var (wf, state) = Build();
        state.CurrentImageWorkspace = new ImageWorkspace();
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        var plan = SelectedPlan();
        plan.Validate();
        state.CurrentCustomizationPlan = plan;
        // Every step up to Build is now Available/Completed, so a direct jump is allowed.
        Assert.True(wf.CanGoToStep(WorkflowStep.Build));
    }

    [Fact]
    public void Workflow_GoToStep_Unavailable_Is_NoOp()
    {
        var (wf, _) = Build();
        wf.GoToStep(WorkflowStep.Customize);
        Assert.Equal(WorkflowStep.Source, wf.CurrentStep!.Step);
    }

    [Fact]
    public void Workflow_GoToStep_Available_Moves_Current()
    {
        var (wf, state) = Build();
        state.CurrentImageWorkspace = new ImageWorkspace();
        wf.GoToStep(WorkflowStep.Prepare);
        Assert.Equal(WorkflowStep.Prepare, wf.CurrentStep!.Step);
    }

    // ---- WORKFLOW: safety ----

    [Fact]
    public void Workflow_SourceChange_Invalidates_Plan_When_Not_Executing()
    {
        var (wf, state) = Build();
        state.CurrentCustomizationPlan = SelectedPlan();
        state.DiscoveredInventory = new DiscoveryInventory { Discovered = true };
        state.SourceImagePath = @"C:\changed.iso";
        Assert.Null(state.CurrentCustomizationPlan);
        Assert.Null(state.DiscoveredInventory);
    }

    [Fact]
    public void Workflow_SourceChange_During_Executing_Keeps_Plan()
    {
        var (wf, state) = Build();
        state.CurrentCustomizationPlan = SelectedPlan();
        state.CustomizationExecutionState = CustomizationExecutionState.Executing;
        state.SourceImagePath = @"C:\changed.iso";
        Assert.NotNull(state.CurrentCustomizationPlan);
    }

    // ---- COMMANDS: explicit CanExecuteChanged (Step 3.2 pattern) ----

    [Fact]
    public void RelayCommand_CanExecute_Does_Not_AutoRequery_And_Needs_Explicit_Raise()
    {
        var can = false;
        var cmd = new RelayCommand(_ => { }, _ => can);
        Assert.False(cmd.CanExecute(null));

        var changed = 0;
        cmd.CanExecuteChanged += (_, _) => changed++;

        // Flipping the predicate source must NOT auto-raise CanExecuteChanged:
        // this command does not rely on CommandManager.RequerySuggested, so a
        // binding would keep seeing the stale value until an explicit raise.
        can = true;
        Assert.Equal(0, changed);
        Assert.True(cmd.CanExecute(null)); // live eval is true, but no notification fired

        cmd.RaiseCanExecuteChanged();
        Assert.Equal(1, changed);
    }

    [Fact]
    public void AsyncRelayCommand_CanExecute_Does_Not_AutoRequery_And_Needs_Explicit_Raise()
    {
        var can = false;
        var cmd = new AsyncRelayCommand(_ => Task.CompletedTask, _ => can);
        Assert.False(cmd.CanExecute(null));

        var changed = 0;
        cmd.CanExecuteChanged += (_, _) => changed++;

        can = true;
        Assert.Equal(0, changed);
        Assert.True(cmd.CanExecute(null));

        cmd.RaiseCanExecuteChanged();
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Workflow_NextCommand_CanExecute_Tracks_Gating()
    {
        var (wf, state) = Build();
        Assert.False(wf.NextCommand.CanExecute(null));
        state.CurrentImageWorkspace = new ImageWorkspace();
        Assert.True(wf.NextCommand.CanExecute(null));
    }

    [Fact]
    public void PlanReview_ValidateCommand_CanExecute_Requires_Mounted_Selected_Plan()
    {
        var state = new AppState();
        var plan = new PlanReviewViewModel(state, new InMemoryLoggerService(), new FakeCustomizationExecutionService());
        Assert.False(plan.CanValidate);
        Assert.False(plan.ValidateCommand.CanExecute(null));

        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        state.CurrentCustomizationPlan = SelectedPlan();
        Assert.True(plan.CanValidate);
        Assert.True(plan.ValidateCommand.CanExecute(null));
    }

    [Fact]
    public void PlanReview_ApplyCommand_Only_When_Validated()
    {
        var state = new AppState();
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        var planVm = new PlanReviewViewModel(state, new InMemoryLoggerService(), new FakeCustomizationExecutionService());
        var plan = SelectedPlan();
        state.CurrentCustomizationPlan = plan;

        Assert.False(planVm.CanApply);
        Assert.False(planVm.ApplyCommand.CanExecute(null));

        plan.Validate();
        Assert.True(planVm.CanApply);
        Assert.True(planVm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public void ImageViewModel_PrepareCommand_CanExecute_Tracks_Workspace_Readiness()
    {
        var state = new AppState();
        var image = new ImageViewModel(state, new InMemoryLoggerService(),
            new FakeInspection(), new FakeFilePicker(), new FakeWorkspaceFactory(),
            new ReadyWimService(), new FakeImageServicingService());

        // No durable workspace yet -> Prepare must stay disabled.
        Assert.False(image.CanPrepareWorkingImage);
        Assert.False(image.PrepareWorkingImageCommand.CanExecute(null));

        // A ready workspace unlocks Prepare (state-driven; no restart/requery needed
        // because the command reads CanPrepareWorkingImage live).
        state.CurrentImageWorkspace = new ImageWorkspace
        {
            SelectedEditionName = "Pro",
            SelectedIndex = 1,
            ImageRelativePath = @"sources\install.wim",
            Architecture = "amd64",
            Build = "22631",
            SourceIsoPath = @"C:\x.iso"
        };
        Assert.True(image.CanPrepareWorkingImage);
        Assert.True(image.PrepareWorkingImageCommand.CanExecute(null));
    }

    internal sealed class ReadyWimService : IWimService
    {
        public ImageWorkspaceStatus ValidateWorkspace(ImageWorkspace workspace) => ImageWorkspaceStatus.Ready;
        public SelectedImageContext? ResolveSelectedImage(ImageWorkspace workspace) => null;
    }
}
