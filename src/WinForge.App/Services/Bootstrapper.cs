using Microsoft.Extensions.DependencyInjection;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.Core.Services;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.ImageMetadata;
using WinForge.Infrastructure.WimEngine;
using WinForge.Infrastructure.IsoInspection;
using WinForge.Infrastructure.Execution;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.Servicing;

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

        // Phase 2 — ISO Inspection (read-only)
        services.AddSingleton<IIsoMountService, WindowsIsoMountService>();
        services.AddSingleton<IWindowsImageMetadataService, WindowsImageMetadataService>();
        services.AddSingleton<IProcessRunner, WindowsProcessRunner>();
        services.AddSingleton<IIsoInspectionService, WindowsIsoInspectionService>();
        services.AddSingleton<IFilePicker, WindowsFilePicker>();

        // Phase 3 — WIM Engine (Step 3.1, read-only durable workspace)
        services.AddSingleton<IImageWorkspaceFactory, ImageWorkspaceFactory>();
        services.AddSingleton<IWimService, WimService>();

        // Phase 3 — Step 3.2 (WIM servicing workspace & mount lifecycle)
        services.AddSingleton<IWorkspacePathProvider, WorkspacePathProvider>();
        services.AddSingleton<IWorkspaceSafeDelete, WorkspaceSafeDelete>();
        services.AddSingleton<IImageServicingService, ImageServicingService>();

        // Phase 3 — Step 3.3 (Offline customization plan & execution engine)
        services.AddSingleton<IOfflineRegistryService, OfflineRegistryService>();
        services.AddSingleton<ICustomizationDefinitionProvider, CustomizationDefinitionProvider>();
        services.AddSingleton<IMountIdentityValidator, MountIdentityValidator>();
        services.AddSingleton<ICustomizationDiscoveryService, WindowsCustomizationDiscoveryService>();
        services.AddSingleton<ICustomizationExecutionService, WindowsCustomizationExecutionService>();

        // View models (singletons, shared across navigation)
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<ImageViewModel>();
        services.AddSingleton<ComponentsViewModel>();
        services.AddSingleton<PrivacyViewModel>();
        services.AddSingleton<SystemViewModel>();
        services.AddSingleton<PlanReviewViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<ComingSoonViewModel>();

        return services.BuildServiceProvider();
    }
}
