using WinForge.Core.Models;
using WinForge.Core.Services;
using Xunit;

namespace WinForge.Core.Tests;

/// <summary>
/// Phase 3 Step 3.2 — durable servicing model and AppState wiring. Verifies the
/// source→working index mapping, relative-path normalization, the invariant that
/// the working image is always a WIM, and that <see cref="AppState"/> raises
/// change notifications for the current servicing workspace.
/// </summary>
public class ServicingWorkspaceModelTests
{
    [Fact]
    public void WorkingImageFileName_IsAlwaysWim_ForWimAndEsdSources()
    {
        Assert.Equal("install.wim", ImageServicingWorkspace.WorkingImageFileName(WindowsImageType.Wim));
        Assert.Equal("install.wim", ImageServicingWorkspace.WorkingImageFileName(WindowsImageType.Esd));
    }

    [Fact]
    public void NormalizeRelativePath_CollapsesSeparators()
    {
        Assert.Equal(@"sources\install.wim",
            ImageServicingWorkspace.NormalizeRelativePath("sources/install.wim"));
        Assert.Equal(@"sources\install.wim",
            ImageServicingWorkspace.NormalizeRelativePath("sources\\install.wim"));
        Assert.Equal(@"sources\install.wim",
            ImageServicingWorkspace.NormalizeRelativePath("\\sources\\install.wim\\"));
    }

    [Fact]
    public void NewWorkspace_Defaults_ToWim_And_SingleWorkingIndex()
    {
        var ws = new ImageServicingWorkspace();

        Assert.Equal(WindowsImageType.Wim, ws.WorkingImageType);
        Assert.Equal(1, ws.WorkingIndex);
        Assert.Equal(ServicingWorkspaceState.NotPrepared, ws.State);
        Assert.False(ws.HasError);
    }

    [Fact]
    public void HasError_Mirrors_FailedState()
    {
        var ws = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        Assert.False(ws.HasError);

        ws.State = ServicingWorkspaceState.Failed;
        Assert.True(ws.HasError);
    }

    [Fact]
    public void SourceIndex_Preserved_WhileWorkingIndex_IsOne_AfterExport()
    {
        // A selected source index N inside install.wim/install.esd maps to a
        // standalone working image whose own index is always 1.
        var ws = new ImageServicingWorkspace
        {
            SelectedIndex = 4,
            WorkingIndex = 1,
            SourceImageType = WindowsImageType.Esd
        };

        Assert.Equal(4, ws.SelectedIndex);
        Assert.Equal(1, ws.WorkingIndex);
        Assert.Equal(WindowsImageType.Esd, ws.SourceImageType);
        Assert.Equal(WindowsImageType.Wim, ws.WorkingImageType);
    }

    [Fact]
    public void AppState_CurrentServicingWorkspace_Set_RaisesPropertyChanged()
    {
        IAppState state = new AppState();
        var raised = false;

        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAppState.CurrentServicingWorkspace))
            {
                raised = true;
            }
        };

        state.CurrentServicingWorkspace = new ImageServicingWorkspace();

        Assert.True(raised);
        Assert.NotNull(state.CurrentServicingWorkspace);
    }

    [Fact]
    public void AppState_CurrentServicingWorkspace_Clear_DoesNotRaise_WhenAlreadyNull()
    {
        var state = new AppState();
        var raised = false;

        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAppState.CurrentServicingWorkspace))
            {
                raised = true;
            }
        };

        state.CurrentServicingWorkspace = null;

        Assert.False(raised);
    }

    [Fact]
    public void ServicingResult_Ok_And_Fail_Factory_Shape()
    {
        var ws = new ImageServicingWorkspace();

        var ok = ServicingResult.Ok(ws, ServicingHealth.Prepared);
        Assert.True(ok.Success);
        Assert.Equal(ServicingHealth.Prepared, ok.Health);
        Assert.Same(ws, ok.Workspace);

        var fail = ServicingResult.Fail(ws, "boom", ServicingHealth.Failed);
        Assert.False(fail.Success);
        Assert.Equal("boom", fail.ErrorMessage);
        Assert.Equal(ServicingHealth.Failed, fail.Health);
    }

    [Fact]
    public void ServicingHealth_Distinguishes_Prepared_Mounted_Stale_Failed()
    {
        // Ensure all health states exist and are distinct values (contract surface).
        Assert.NotEqual(ServicingHealth.Prepared, ServicingHealth.Mounted);
        Assert.NotEqual(ServicingHealth.Mounted, ServicingHealth.Stale);
        Assert.NotEqual(ServicingHealth.Stale, ServicingHealth.Failed);
        Assert.NotEqual(ServicingHealth.Failed, ServicingHealth.Invalid);
    }
}
