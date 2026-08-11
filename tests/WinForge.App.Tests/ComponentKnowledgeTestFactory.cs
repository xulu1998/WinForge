using WinForge.App.ViewModels;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.ComponentIntelligence;
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
}
