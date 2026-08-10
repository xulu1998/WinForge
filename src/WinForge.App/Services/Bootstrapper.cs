using System.Globalization;
using System.Resources;
using Microsoft.Extensions.DependencyInjection;
using WinForge.App.FriendlyMetadata;
using WinForge.App.Localization;
using WinForge.App.Services;
using WinForge.App.Workflow;
using WinForge.App.ViewModels;
using WinForge.Core.Services;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.ImageMetadata;
using WinForge.Infrastructure.WimEngine;
using WinForge.Infrastructure.IsoInspection;
using WinForge.Infrastructure.Execution;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.Servicing;
using WinForge.Infrastructure.Build;

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

        // Localization foundation (en-US / zh-CN, runtime switch + persisted choice).
        // The ResourceManager pulls the invariant Strings.resx plus the zh-CN
        // satellite; the service falls back to English when a key is missing.
        services.AddSingleton<ILocalizationService>(_ =>
        {
            var resourceManager = new ResourceManager("WinForge.App.Resources.Strings", typeof(Bootstrapper).Assembly);
            return new ResourceManagerLocalizationService(resourceManager, CultureInfo.GetCultureInfo("en"));
        });
        services.AddSingleton<ILanguageSettingsStore, FileLanguageSettingsStore>();
        services.AddSingleton<IFriendlyMetadataProvider, FriendlyMetadataProvider>();

        // Phase 2 — ISO Inspection (read-only)
        services.AddSingleton<IIsoMountService, WindowsIsoMountService>();
        services.AddSingleton<IWindowsImageMetadataService, WindowsImageMetadataService>();
        services.AddSingleton<IProcessRunner, WindowsProcessRunner>();
        services.AddSingleton<IIsoInspectionService, WindowsIsoInspectionService>();
        services.AddSingleton<IFilePicker, WindowsFilePicker>();
        services.AddSingleton<IFileLauncher, WindowsFileLauncher>();

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

        // Phase 10 — Build / ISO export pipeline (orchestrator + fakeable sub-services).
        services.AddSingleton<IFileSystem, WindowsFileSystem>();
        services.AddSingleton<IAdkToolLocator, AdkToolLocator>();
        services.AddSingleton<IWimExporter, DismWimExporter>();
        services.AddSingleton<IIsoMediaPreparer, IsoMediaPreparer>();
        services.AddSingleton<IBootableIsoBuilder, OscdimgIsoBuilder>();
        services.AddSingleton<IBuildVerifier, BuildVerifier>();
        services.AddSingleton<IBuildService, ImageBuildService>();

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
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AboutViewModel>();

        // Wizard / Stepper workflow (singletons; the coordinator reuses the page VMs above)
        services.AddSingleton<CustomizeStepViewModel>();
        services.AddSingleton<BuildStepViewModel>();
        services.AddSingleton<WorkflowViewModel>();

        return services.BuildServiceProvider();
    }
}
