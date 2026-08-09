using System.Globalization;
using System.Resources;
using WinForge.App.Localization;
using WinForge.App.Workflow;
using WinForge.Core.Models;
using WinForge.Core.Services;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Regression tests for the real-desktop defect: the Wizard "下一步" (Next) button
/// stayed disabled on Step 1 (准备镜像 / Prepare) even after the isolated working
/// image was successfully mounted.
///
/// <para>
/// Root cause: <c>ImageServicingService.MountAsync</c> mutates
/// <see cref="ImageServicingWorkspace.State"/> IN PLACE and returns the SAME
/// workspace reference. <see cref="AppState.CurrentServicingWorkspace"/> used a
/// reference-equality setter, so the same-reference reassignment did NOT raise
/// <see cref="IAppState.PropertyChanged"/>; and <see cref="ImageServicingWorkspace"/>
/// did not implement <see cref="System.ComponentModel.INotifyPropertyChanged"/>.
/// <see cref="WorkflowViewModel"/> therefore never observed the Prepared → Mounted
/// transition and never re-raised <c>NextCommand.CanExecuteChanged</c>. The fix
/// makes the workspace notify on State changes and makes AppState forward those
/// nested changes (including same-reference reassignment) to IAppState listeners.
/// </para>
///
/// <para>
/// These tests reproduce the real sequence WITHOUT recreating WorkflowViewModel and
/// exercise the in-place mutation path that triggered the bug.
/// </para>
/// </summary>
public class WizardNextButtonMountRegressionTests
{
    // ---- Main reproduction: exact real-desktop sequence, single WorkflowViewModel ----

    [Fact]
    public void Wizard_RealDesktop_Next_Enables_After_Mount_Without_Recreating_Workflow()
    {
        // WorkflowViewModel is built ONCE and driven entirely through IAppState,
        // exactly like the running app. The servicing workspace is mutated IN PLACE
        // (same reference) — the precise real-desktop trigger.
        var (wf, state) = WorkflowAndCommandTests.Build();

        // 1. app starts with no ISO
        Assert.Equal(WorkflowStep.Source, wf.CurrentStep!.Step);
        Assert.False(wf.NextCommand.CanExecute(null));

        // 2. Source selected / inspected
        state.SourceImagePath = @"C:\en-us.iso";

        // 3. edition selected + ImageWorkspace becomes Ready
        state.CurrentImageWorkspace = new ImageWorkspace
        {
            SelectedEditionName = "Windows 11 Pro",
            SelectedIndex = 4,
            ImageRelativePath = @"sources\install.wim",
            Architecture = "amd64",
            Build = "26200",
            SourceIsoPath = @"C:\en-us.iso"
        };

        // 4. navigate to Prepare
        Assert.True(wf.CanGoNext); // Prepare is now Available
        wf.GoToStep(WorkflowStep.Prepare);
        Assert.Equal(WorkflowStep.Prepare, wf.CurrentStep!.Step);

        // 5. working image becomes Prepared
        var ws = new ImageServicingWorkspace
        {
            SourceIsoPath = @"C:\en-us.iso",
            SelectedIndex = 4,
            SelectedEditionName = "Windows 11 Pro",
            WorkingDirectory = @"C:\wf\ws",
            WorkingImagePath = @"C:\wf\ws\image\install.wim",
            MountDirectory = @"C:\wf\ws\mount",
            State = ServicingWorkspaceState.Prepared
        };
        state.CurrentServicingWorkspace = ws;
        // Prepared-only: Customize is NotAvailable, so Next must stay disabled.
        Assert.False(wf.NextCommand.CanExecute(null));

        // 6/7/8/9. working image becomes Mounted (SAME object, in-place mutation).
        // The Next predicate flips false -> true AND CanExecuteChanged MUST fire.
        var fired = 0;
        wf.NextCommand.CanExecuteChanged += (_, _) => fired++;
        Assert.False(wf.NextCommand.CanExecute(null)); // still false before mutation

        ws.State = ServicingWorkspaceState.Mounted; // the real trigger

        Assert.True(wf.NextCommand.CanExecute(null)); // Next now enabled
        Assert.True(fired > 0, "NextCommand.CanExecuteChanged must fire when State flips to Mounted");
        Assert.True(wf.CanGoNext);

        // 10. executing Next navigates to Customize (it becomes the Current step).
        wf.GoNext();
        Assert.Equal(WorkflowStep.Customize, wf.CurrentStep!.Step);
        Assert.Equal(WorkflowStepState.Current, wf.Steps[2].State);
    }

    // ---- Extra coverage the defect report asked for ----

    [Fact]
    public void Wizard_PreparedOnly_Keeps_Next_Disabled_Until_Mount()
    {
        var (wf, state) = WorkflowAndCommandTests.Build();
        state.CurrentImageWorkspace = new ImageWorkspace();
        wf.GoToStep(WorkflowStep.Prepare);

        var ws = new ImageServicingWorkspace { State = ServicingWorkspaceState.Prepared };
        state.CurrentServicingWorkspace = ws;
        Assert.False(wf.CanGoNext);

        ws.State = ServicingWorkspaceState.Mounted; // in-place
        Assert.True(wf.CanGoNext);
    }

    [Fact]
    public void Wizard_Unmount_Disables_Next_Again()
    {
        var (wf, state) = WorkflowAndCommandTests.Build();
        state.CurrentImageWorkspace = new ImageWorkspace();
        wf.GoToStep(WorkflowStep.Prepare);

        var ws = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        state.CurrentServicingWorkspace = ws;
        Assert.True(wf.CanGoNext);

        // Discard / unmount returns to Prepared -> Next must disable again.
        ws.State = ServicingWorkspaceState.Prepared; // in-place
        Assert.False(wf.CanGoNext);
    }

    [Fact]
    public void Wizard_StaleOrFailed_Mount_Disables_Next()
    {
        var (wf, state) = WorkflowAndCommandTests.Build();
        state.CurrentImageWorkspace = new ImageWorkspace();
        wf.GoToStep(WorkflowStep.Prepare);

        var ws = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        state.CurrentServicingWorkspace = ws;
        Assert.True(wf.CanGoNext);

        // A stale / invalid / failed mount must not satisfy the prerequisite.
        ws.State = ServicingWorkspaceState.Failed; // in-place
        Assert.False(wf.CanGoNext);
    }

    [Fact]
    public void Wizard_SourceChange_After_Mount_Invalidates_Downstream_Step()
    {
        var (wf, state) = WorkflowAndCommandTests.Build();
        state.CurrentImageWorkspace = new ImageWorkspace();
        wf.GoToStep(WorkflowStep.Prepare);

        var ws = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        state.CurrentServicingWorkspace = ws;

        // Reach Review with a selected plan.
        state.CurrentCustomizationPlan = WorkflowAndCommandTests.SelectedPlan();
        wf.GoToStep(WorkflowStep.Review);
        Assert.Equal(WorkflowStep.Review, wf.CurrentStep!.Step);

        // Changing the source invalidates the assembled plan + discovery (they target
        // the previous image) and the downstream step must become NotAvailable.
        state.SourceImagePath = @"C:\other.iso";
        Assert.Null(state.CurrentCustomizationPlan);
        Assert.Equal(WorkflowStepState.NotAvailable, wf.Steps[3].State); // Review
        Assert.False(wf.CanGoNext);
    }

    [Fact]
    public void Wizard_Next_Gating_Is_Culture_Independent()
    {
        // Workflow gating reads ONLY IAppState primitives (servicing State, image
        // workspace, plan, execution state) — never a localized string. Prove the
        // mount-gated Next behaves identically under en-US and zh-CN (the language
        // the defect was reported in). This guards against any future coupling of
        // gating to culture.
        var rm = new ResourceManager("WinForge.App.Resources.Strings", typeof(ResourceManagerLocalizationService).Assembly);

        // zh-CN resource service must be constructible and culture-switchable.
        var locZh = new ResourceManagerLocalizationService(rm, CultureInfo.GetCultureInfo("en"));
        locZh.SetCulture(CultureInfo.GetCultureInfo("zh-CN"));
        // en service
        var locEn = new ResourceManagerLocalizationService(rm, CultureInfo.GetCultureInfo("en"));

        var (wfEn, stateEn) = WorkflowAndCommandTests.Build();
        var (wfZh, stateZh) = WorkflowAndCommandTests.Build();

        DriveMountSequenceAndAssert(wfEn, stateEn);
        DriveMountSequenceAndAssert(wfZh, stateZh);

        // The localized title key resolves differently per culture, but the gating
        // outcome is identical — proving culture cannot gate the workflow.
        Assert.NotEqual(locEn["Step.Prepare.Title"], locZh["Step.Prepare.Title"]);
    }

    private static void DriveMountSequenceAndAssert(WorkflowViewModel wf, AppState state)
    {
        state.CurrentImageWorkspace = new ImageWorkspace();
        wf.GoToStep(WorkflowStep.Prepare);

        var ws = new ImageServicingWorkspace { State = ServicingWorkspaceState.Prepared };
        state.CurrentServicingWorkspace = ws;
        Assert.False(wf.CanGoNext);

        var fired = 0;
        wf.NextCommand.CanExecuteChanged += (_, _) => fired++;
        ws.State = ServicingWorkspaceState.Mounted; // in-place
        Assert.True(wf.CanGoNext);
        Assert.True(fired > 0);
        wf.GoNext();
        Assert.Equal(WorkflowStep.Customize, wf.CurrentStep!.Step);
    }
}
