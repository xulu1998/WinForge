using WinForge.Core.Models;
using WinForge.Core.Services;
using Xunit;

namespace WinForge.Core.Tests;

public class AppStateTests
{
    [Fact]
    public void AppState_Defaults_AreCorrect()
    {
        IAppState state = new AppState();

        Assert.Null(state.SourceImagePath);
        Assert.Null(state.SelectedEdition);
        Assert.Equal(BuildState.NotStarted, state.BuildStatus);
        Assert.NotNull(state.Configuration);
        Assert.Equal("Default", state.ConfigurationLabel);
    }

    [Fact]
    public void AppState_SourceImagePath_Set_RaisesPropertyChanged()
    {
        var state = new AppState();
        var raised = false;

        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAppState.SourceImagePath))
            {
                raised = true;
            }
        };

        state.SourceImagePath = @"C:\images\windows.iso";

        Assert.True(raised);
        Assert.Equal(@"C:\images\windows.iso", state.SourceImagePath);
    }

    [Fact]
    public void AppState_BuildStatus_Set_RaisesPropertyChanged()
    {
        var state = new AppState();
        var raised = false;

        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAppState.BuildStatus))
            {
                raised = true;
            }
        };

        state.BuildStatus = BuildState.Preflight;

        Assert.True(raised);
        Assert.Equal(BuildState.Preflight, state.BuildStatus);
    }
}
