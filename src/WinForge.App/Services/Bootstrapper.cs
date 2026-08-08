using Microsoft.Extensions.DependencyInjection;
using WinForge.App.ViewModels;
using WinForge.Core.Services;
using WinForge.Infrastructure.Logging;

namespace WinForge.App.Services;

/// <summary>
/// Composes the application's dependency graph. View models are registered as
/// singletons so navigation state (current page, selected image) is shared.
/// Infrastructure implementations are bound to Core interfaces here — Core
/// itself never references Infrastructure or App.
/// </summary>
public static class Bootstrapper
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        // Core interfaces -> implementations
        services.AddSingleton<ILoggerService, InMemoryLoggerService>();
        services.AddSingleton<IAppState, AppState>();
        services.AddSingleton<INavigationService, NavigationService>();

        // View models (singletons, shared across navigation)
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<ImageViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<ComingSoonViewModel>();

        return services.BuildServiceProvider();
    }
}
