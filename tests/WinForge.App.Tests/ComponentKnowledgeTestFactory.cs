using WinForge.App.ViewModels;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.ComponentIntelligence;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Logging;

namespace WinForge.App.Tests;

/// <summary>
/// Shared construction helper for the Stage 11.2 "Component Knowledge" tab VM.
/// Builds a fully-wired <see cref="ComponentKnowledgeViewModel"/> backed by a
/// no-discovery <see cref="IComponentIntelligenceService"/> stub and the real
/// curated catalog, sharing the surrounding test's <see cref="AppState"/> so
/// plan selection stays consistent with the rest of the wizard shell.
/// </summary>
internal static class ComponentKnowledgeTestFactory
{
    private sealed class NoDiscoveryCiService : IComponentIntelligenceService
    {
        public Task<ComponentInventory> DiscoverAsync(
            ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
            => Task.FromResult(new ComponentInventory());
    }

    public static ComponentKnowledgeViewModel Make(AppState state, ILoggerService logger)
    {
        var ciVm = new ComponentIntelligenceViewModel(
            state, logger, new NoDiscoveryCiService(), new CuratedComponentCatalog(),
            new FakeLocalizationService());
        return new ComponentKnowledgeViewModel(ciVm, state, logger, new FakeLocalizationService());
    }

    /// <summary>
    /// Builds a fully-wired Stage 11.3 <see cref="CustomizeStepViewModel"/> (six
    /// knowledge-backed tabs): Apps + Windows components reuse
    /// <see cref="ComponentKnowledgeViewModel"/> over the composite catalog
    /// (AppX vs capabilities/optional features); Services / Privacy / System /
    /// Personalization share <see cref="OptimizationKnowledgeViewModel"/> over the
    /// real optimization catalog. The CI service stub performs no discovery.
    /// </summary>
    public static CustomizeStepViewModel MakeCustomize(AppState state, ILoggerService logger)
    {
        var loc = new FakeLocalizationService();
        var components = new ComponentsViewModel(
            state, logger, new FakeCustomizationDiscoveryService(), new FakeCustomizationDefinitionProvider());
        var ciVm = new ComponentIntelligenceViewModel(
            state, logger, new NoDiscoveryCiService(),
            new CompositeComponentCatalog(new CuratedComponentCatalog(), new WindowsFeaturesCatalog()), loc);
        var knowledge = new ComponentKnowledgeViewModel(ciVm, state, logger, loc);
        var componentsKnowledge = new ComponentKnowledgeViewModel(ciVm, state, logger, loc,
            new[] { ComponentCategory.OptionalFeature, ComponentCategory.Capability });

        var catalog = new OptimizationCatalog();
        OptimizationKnowledgeViewModel K(OptimizationTab t) => new(state, logger, loc, catalog, t);

        return new CustomizeStepViewModel(
            components,
            knowledge,
            componentsKnowledge,
            K(OptimizationTab.Services),
            K(OptimizationTab.Privacy),
            K(OptimizationTab.System),
            K(OptimizationTab.Personalization));
    }

    /// <summary>
    /// The Windows Components knowledge tab: the SAME <see cref="ComponentKnowledgeViewModel"/>
    /// engine over the composite catalog, filtered to capabilities / optional features.
    /// </summary>
    public static ComponentKnowledgeViewModel MakeComponentsKnowledge(AppState state, ILoggerService logger)
    {
        var loc = new FakeLocalizationService();
        var ciVm = new ComponentIntelligenceViewModel(state, logger, new NoDiscoveryCiService(),
            new CompositeComponentCatalog(new CuratedComponentCatalog(), new WindowsFeaturesCatalog()), loc);
        return new ComponentKnowledgeViewModel(ciVm, state, logger, loc,
            new[] { ComponentCategory.OptionalFeature, ComponentCategory.Capability });
    }

    /// <summary>One catalog-driven knowledge tab (Services / Privacy / System / Personalization).</summary>
    public static OptimizationKnowledgeViewModel MakeOptimization(AppState state, ILoggerService logger, OptimizationTab tab)
        => new(state, logger, new FakeLocalizationService(), new OptimizationCatalog(), tab);
}
