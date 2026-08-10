using Microsoft.Extensions.DependencyInjection;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.App.Workflow;
using WinForge.Core.Services;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Headless verification that the application starts and its runtime wiring works
/// without requiring a physical WPF window (the CI/sandbox has no display). It
/// builds the real DI container, resolves the shell, and confirms the wizard is
/// the default surface while the utility rail (Home / Logs / Settings / About)
/// switches the active view. It also confirms the legacy deep link from Home's
/// "Select image" still jumps the workflow to the Source step.
/// </summary>
public class AppBootTests
{
    [Fact]
    public void Application_Boots_With_Workflow_As_Default_Surface()
    {
        var provider = Bootstrapper.Build();
        var main = provider.GetRequiredService<MainViewModel>();
        var logger = provider.GetRequiredService<ILoggerService>();

        // The wizard is the primary surface on startup.
        Assert.True(main.IsWorkflowActive);
        Assert.IsType<WorkflowViewModel>(main.ActiveView);
        Assert.IsType<WorkflowViewModel>(main.Workflow);

        // Startup shows the wizard by navigating through the single coordinator,
        // which is logged. The logger is therefore no longer empty at startup — it
        // reflects the initial navigation to the workflow surface.
        Assert.Contains(logger.Entries, e => e.Message.Contains("Navigation changed"));
    }

    [Fact]
    public void Utility_Rail_Switches_Active_View_And_Back()
    {
        var provider = Bootstrapper.Build();
        var main = provider.GetRequiredService<MainViewModel>();

        main.ShowUtilityCommand.Execute(PageKey.Home);
        Assert.False(main.IsWorkflowActive);
        Assert.IsType<HomeViewModel>(main.ActiveView);

        main.ShowUtilityCommand.Execute(PageKey.Settings);
        Assert.IsType<SettingsViewModel>(main.ActiveView);

        main.ShowUtilityCommand.Execute(PageKey.About);
        Assert.IsType<AboutViewModel>(main.ActiveView);

        main.ShowUtilityCommand.Execute(PageKey.Logs);
        Assert.IsType<LogsViewModel>(main.ActiveView);

        // Returning to the workflow restores the wizard surface.
        main.ShowWorkflowCommand.Execute(null);
        Assert.True(main.IsWorkflowActive);
        Assert.IsType<WorkflowViewModel>(main.ActiveView);
    }

    [Fact]
    public void Home_SelectImage_DeepLink_JumpTo_Workflow_Source()
    {
        var provider = Bootstrapper.Build();
        var main = provider.GetRequiredService<MainViewModel>();
        var logger = provider.GetRequiredService<ILoggerService>();
        var home = provider.GetRequiredService<HomeViewModel>();
        var workflow = provider.GetRequiredService<WorkflowViewModel>();

        // Simulate the Browse button: Home navigates to the Image page, which the
        // shell translates onto the workflow Source step.
        home.SelectImageCommand.Execute(null);

        Assert.True(main.IsWorkflowActive);
        Assert.Equal(WorkflowStep.Source, workflow.CurrentStep?.Step);
        Assert.Contains(logger.Entries, e => e.Message.Contains("Navigation changed"));
    }

    [Fact]
    public void All_Workflow_And_Utility_ViewModels_Resolve()
    {
        var provider = Bootstrapper.Build();

        // Workflow coordinator + step view models.
        Assert.NotNull(provider.GetRequiredService<WorkflowViewModel>());
        Assert.NotNull(provider.GetRequiredService<CustomizeStepViewModel>());
        Assert.NotNull(provider.GetRequiredService<BuildStepViewModel>());

        // Utility pages.
        Assert.NotNull(provider.GetRequiredService<HomeViewModel>());
        Assert.NotNull(provider.GetRequiredService<LogsViewModel>());
        Assert.NotNull(provider.GetRequiredService<SettingsViewModel>());
        Assert.NotNull(provider.GetRequiredService<AboutViewModel>());

        // Shared customization view models reused by the wizard.
        Assert.NotNull(provider.GetRequiredService<ComponentsViewModel>());
        Assert.NotNull(provider.GetRequiredService<PrivacyViewModel>());
        Assert.NotNull(provider.GetRequiredService<SystemViewModel>());
        Assert.NotNull(provider.GetRequiredService<PlanReviewViewModel>());
    }
}
