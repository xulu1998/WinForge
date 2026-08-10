using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;

namespace WinForge.Core.Services;

/// <summary>
/// Supplies the curated, trusted set of component knowledge entries. Definitions
/// are generated only by WinForge — never from arbitrary UI input — so every
/// logical component has a known, documented Windows mapping and human guidance.
/// </summary>
public interface IComponentCatalogProvider
{
    /// <summary>The curated component definitions known to WinForge.</summary>
    IReadOnlyList<ComponentDefinition> GetDefinitions();
}

/// <summary>
/// Inspects the mounted offline working image and returns structured component
/// inventory (never raw DISM text). Must tolerate missing / renamed / edition /
/// build differences, operate ONLY against the mounted workspace, and handle
/// cancellation and per-source errors. Stage 11.1 implements read-only discovery
/// for AppX / Capabilities / Optional Features / CBS packages; later categories
/// are designed but not yet enumerated.
/// </summary>
public interface IComponentIntelligenceService
{
    Task<ComponentInventory> DiscoverAsync(
        ImageServicingWorkspace workspace, CancellationToken cancellationToken = default);
}
