using System.Globalization;
using System.Resources;
using Microsoft.Extensions.DependencyInjection;
using WinForge.App.FriendlyMetadata;
using WinForge.App.Localization;
using WinForge.App.Services;
using WinForge.App.Workflow;
using WinForge.App.ViewModels;
using WinForge.Core.Models;
using WinForge.Core.Profiles;
using WinForge.Core.Services;
using WinForge.Core.WorkspaceLifecycle;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.ImageMetadata;
using WinForge.Infrastructure.WimEngine;
using WinForge.Infrastructure.IsoInspection;
using WinForge.Infrastructure.Execution;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.Servicing;
using WinForge.Infrastructure.WorkspaceLifecycle;
using WinForge.Infrastructure.Build;
using WinForge.Infrastructure.ComponentIntelligence;
using WinForge.Infrastructure.Profiles;

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
        services.AddSingleton<IWorkspaceLifecycleManager, WorkspaceLifecycleManager>();
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

        // Phase 11 — Component Intelligence (Stage 11.1, read-only discovery + catalog)
        // Stage 11.3: the shared catalog composes the AppX catalog with the Windows
        // Features catalog so one discovery classifies both (Apps tab = AppX;
        // Windows Components tab = capabilities/optional features).
        services.AddSingleton<IComponentCatalogProvider>(_ =>
            new CompositeComponentCatalog(new CuratedComponentCatalog(), new WindowsFeaturesCatalog()));
        services.AddSingleton<IComponentIntelligenceService, WindowsComponentIntelligenceService>();

        // Stage 11.3 — reviewed optimization catalog (Services / Privacy / System / Personalization).
        services.AddSingleton<IOptimizationCatalogProvider, OptimizationCatalog>();

        // Stage 11.4 — scenario profile engine (recommended configuration).
        services.AddSingleton<IProfileCatalogProvider, ProfileCatalog>();
        services.AddSingleton<IRecommendationEngine, RecommendationEngine>();
        services.AddSingleton<RecommendationContextService>();

        // View models (singletons, shared across navigation)
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<ImageViewModel>();
        services.AddSingleton<ComponentsViewModel>();
        services.AddSingleton<ComponentIntelligenceViewModel>();
        services.AddSingleton<ComponentKnowledgeViewModel>();
        services.AddSingleton<PrivacyViewModel>();
        services.AddSingleton<SystemViewModel>();
        services.AddSingleton<PlanReviewViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<ComingSoonViewModel>();
        services.AddSingleton<StorageViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AboutViewModel>();

        // Wizard / Stepper workflow (singletons; the coordinator reuses the page VMs above)
        // Stage 11.3: the Customize step owns six knowledge-backed tabs. The four
        // catalog-driven tabs (Services / Privacy / System / Personalization) share one
        // OptimizationKnowledgeViewModel engine and are constructed here so each gets
        // its own catalog slice; the Windows Components tab reuses ComponentKnowledgeViewModel
        // over the shared classified inventory with a capability/feature category filter.
        services.AddSingleton(sp =>
        {
            var components = sp.GetRequiredService<ComponentsViewModel>();
            var appState = sp.GetRequiredService<IAppState>();
            var logger = sp.GetRequiredService<ILoggerService>();
            var loc = sp.GetRequiredService<ILocalizationService>();
            var catalog = sp.GetRequiredService<IOptimizationCatalogProvider>();

            var knowledge = sp.GetRequiredService<ComponentKnowledgeViewModel>();

            var ciVm = sp.GetRequiredService<ComponentIntelligenceViewModel>();
            var profileCtx = sp.GetRequiredService<RecommendationContextService>();
            var componentsKnowledge = new ComponentKnowledgeViewModel(ciVm, appState, logger, loc,
                new[] { ComponentCategory.OptionalFeature, ComponentCategory.Capability }, profileCtx);

            OptimizationKnowledgeViewModel KnowledgeFor(OptimizationTab tab)
                => new(appState, logger, loc, catalog, tab, profileCtx);

            return new CustomizeStepViewModel(
                components,
                knowledge,
                componentsKnowledge,
                KnowledgeFor(OptimizationTab.Services),
                KnowledgeFor(OptimizationTab.Privacy),
                KnowledgeFor(OptimizationTab.System),
                KnowledgeFor(OptimizationTab.Personalization),
                profileCtx,
                loc);
        });
        services.AddSingleton<BuildStepViewModel>();
        services.AddSingleton<WorkflowViewModel>();

        return services.BuildServiceProvider();
    }
}
