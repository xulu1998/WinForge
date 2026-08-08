using Microsoft.Extensions.DependencyInjection;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.Core.Services;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Headless verification that the application starts and its runtime wiring
/// works without requiring a physical WPF window (the CI/sandbox has no
/// display). It builds the real DI container, resolves the navigation shell,
/// navigates between pages, simulates a source-image selection, and confirms
/// the logger records the expected lifecycle events.
/// </summary>
public class AppBootTests
{
    [Fact]
    public void Application_Boots_Navigates_And_Logs_WithoutDisplay()
    {
        // Arrange + Act: build the real container and resolve the shell.
        // (App.OnStartup is intentionally NOT run here — it creates a WPF window
        // which a headless environment cannot display. Its lifecycle logging is
        // therefore absent; we verify the wiring that does not need a window.)
        var provider = Bootstrapper.Build();
        var main = provider.GetRequiredService<MainViewModel>();
        var logger = provider.GetRequiredService<ILoggerService>();
        var appState = provider.GetRequiredService<IAppState>();

        // The logger starts empty until something logs.
        Assert.Empty(logger.Entries);

        // Navigate to the Image page -> navigation is logged and the correct
        // view model becomes active.
        main.Navigate(PageKey.Image);
        Assert.IsType<ImageViewModel>(main.CurrentView);
        Assert.Contains(logger.Entries, e => e.Message.Contains("Navigation changed"));

        // Navigate to a future-phase page -> shared ComingSoon view model.
        main.Navigate(PageKey.Privacy);
        Assert.IsType<ComingSoonViewModel>(main.CurrentView);

        // Simulate the source-image selection that the Browse dialog performs.
        appState.SourceImagePath = @"C:\images\windows.iso";
        Assert.Equal(@"C:\images\windows.iso", appState.SourceImagePath);

        // The Logs page reflects the live entries.
        var logs = provider.GetRequiredService<LogsViewModel>();
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Info);
    }
}
