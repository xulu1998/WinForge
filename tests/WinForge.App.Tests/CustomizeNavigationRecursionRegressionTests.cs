using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.App.Workflow;
using WinForge.Core.Models;
using WinForge.Core.Services;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Regression tests for the real-desktop crash: entering the Customize step produced
/// an unbounded storm of error dialogs and a 0xc00000fd (STATUS_STACK_OVERFLOW)
/// process termination.
///
/// <para>
/// Root cause (disproven / mitigated): commit 28b8bb5 made
/// <see cref="AppState.CurrentServicingWorkspace"/> raise a synthetic
/// <c>PropertyChanged</c> even on a same-reference reassignment, in case a downstream
/// consumer re-observed it. Tracing proved no consumer reassigns the workspace inside
/// a <c>CurrentServicingWorkspace</c> handler (the only writers are the servicing
/// commands, which do not subscribe to AppState), so that branch could not itself form
/// a write-back loop. However it WAS a redundant synthetic event, and the in-place
/// <see cref="ImageServicingWorkspace.State"/> mutation already forwards correctly via
/// <see cref="ImageServicingWorkspace"/>'s <see cref="System.ComponentModel.INotifyPropertyChanged"/>.
/// The fix removes the synthetic same-reference notification so a same-reference
/// reassignment is a true no-op and cannot be amplified. The definitive protection
/// against the symptom is the error-dialog guard (one root cause -&gt; at most one dialog).
/// </para>
///
/// <para>
/// These tests drive the REAL <see cref="WorkflowViewModel"/> plus the full Customize
/// graph (Components / Privacy / System view models, all subscribed to the shared
/// IAppState) through the exact Source -&gt; Prepare -&gt; Mount -&gt; Next -&gt; Customize
/// sequence, counting <c>CurrentServicingWorkspace</c> notifications to prove the chain
/// stays bounded and never recurses.
/// </para>
/// </summary>
public class CustomizeNavigationRecursionRegressionTests
{
    [Fact]
    public void Customize_Navigation_FullGraph_BoundedNotifications_NoRecursiveLoop()
    {
        // Real WorkflowViewModel + full Customize graph, exactly like the running app.
        var (wf, state) = WorkflowAndCommandTests.Build();

        var notifications = 0;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAppState.CurrentServicingWorkspace))
            {
                notifications++;
            }
        };

        // 1. Source ready + edition selected -> durable image workspace.
        state.CurrentImageWorkspace = new ImageWorkspace();

        // 2-3. advance to Prepare; working image becomes Prepared.
        wf.GoToStep(WorkflowStep.Prepare);
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
        state.CurrentServicingWorkspace = ws; // initial assignment -> 1 notification

        // 4-5. In-place Mounted (same reference) must flip Next enabled WITHOUT a
        // synthetic same-reference notification.
        Assert.False(wf.NextCommand.CanExecute(null));
        ws.State = ServicingWorkspaceState.Mounted; // in-place -> nested -> 1 notification
        Assert.True(wf.NextCommand.CanExecute(null));

        // 6-7. Execute Next -> Customize becomes Current and initialization completes
        // (no exception, no stack overflow from a notification feedback loop).
        wf.GoNext();
        Assert.Equal(WorkflowStep.Customize, wf.CurrentStep!.Step);
        Assert.Equal(WorkflowStepState.Current, wf.Steps[2].State);

        // 8-9. Notification count stays bounded: initial assignment (1) + in-place
        // Mounted (1) is all the CurrentServicingWorkspace traffic this sequence
        // produces. Anything more would be a feedback loop.
        Assert.True(notifications <= 2,
            $"CurrentServicingWorkspace notifications should be bounded; got {notifications}.");
    }

    [Fact]
    public void AppState_InPlaceMounted_Produces_Single_CurrentServicingWorkspace_Notification()
    {
        var state = new AppState();
        var notifications = 0;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAppState.CurrentServicingWorkspace))
            {
                notifications++;
            }
        };

        var ws = new ImageServicingWorkspace { State = ServicingWorkspaceState.Prepared };
        state.CurrentServicingWorkspace = ws; // reference change -> 1
        Assert.Equal(1, notifications);

        ws.State = ServicingWorkspaceState.Mounted; // in-place -> nested -> 1
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void AppState_SameReferenceReassignment_DoesNotAmplifyNotifications()
    {
        var state = new AppState();
        var notifications = 0;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAppState.CurrentServicingWorkspace))
            {
                notifications++;
            }
        };

        var ws = new ImageServicingWorkspace { State = ServicingWorkspaceState.Prepared };
        state.CurrentServicingWorkspace = ws; // 1
        ws.State = ServicingWorkspaceState.Mounted; // nested -> 2

        // Reassigning the SAME reference must NOT re-raise CurrentServicingWorkspace.
        // This is the exact pattern the prior AppState fix used to force-notify; it
        // must now be a no-op so a downstream consumer cannot amplify events.
        state.CurrentServicingWorkspace = ws;
        Assert.Equal(2, notifications);

        // A subsequent genuine in-place change still propagates exactly once.
        ws.State = ServicingWorkspaceState.Prepared;
        Assert.Equal(3, notifications);
    }

    [Fact]
    public void AppState_ConsumerReassigningSameWorkspaceFromHandler_DoesNotLoop()
    {
        var state = new AppState();
        var ws = new ImageServicingWorkspace { State = ServicingWorkspaceState.Prepared };
        state.CurrentServicingWorkspace = ws;

        // Simulate a (buggy) consumer that reassigns the same workspace from inside its
        // CurrentServicingWorkspace handler -- the exact feedback loop the crash report
        // feared. With the no-op same-reference setter this cannot amplify; on the old
        // code this recursed until the stack overflowed (0xc00000fd).
        var notifications = 0;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAppState.CurrentServicingWorkspace))
            {
                notifications++;
                state.CurrentServicingWorkspace = ws; // would loop on the old code
            }
        };

        // Trigger one genuine in-place change.
        ws.State = ServicingWorkspaceState.Mounted;

        // The handler fired for the nested change; its own same-ref reassignment
        // produced no further notification, so the count stays bounded.
        Assert.True(notifications <= 2,
            $"Same-reference reassignment inside a handler must not amplify; got {notifications}.");
    }

    [Fact]
    public void ErrorDialogGuard_CoalescesRepeats_And_CapsTotal()
    {
        ErrorDialogGuard.Reset();

        Assert.True(ErrorDialogGuard.ShouldShow("A"));  // first distinct -> show (1)
        Assert.False(ErrorDialogGuard.ShouldShow("A")); // rapid repeat -> coalesce
        Assert.True(ErrorDialogGuard.ShouldShow("B"));  // distinct -> show (2)
        Assert.True(ErrorDialogGuard.ShouldShow("C"));  // distinct -> show (3, cap reached)
        Assert.False(ErrorDialogGuard.ShouldShow("D")); // cap reached -> no more

        ErrorDialogGuard.Reset();
        Assert.True(ErrorDialogGuard.ShouldShow("A"));  // reset restores capacity
    }
}
