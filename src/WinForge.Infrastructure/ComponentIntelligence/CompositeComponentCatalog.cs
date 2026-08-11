using System.Collections.Generic;
using System.Linq;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.ComponentIntelligence;

/// <summary>
/// Merges multiple <see cref="IComponentCatalogProvider"/>s into one catalog.
/// Stage 11.3 composes the Stage 11.2 AppX catalog with the Windows Features
/// catalog so a single Component Intelligence discovery pass classifies BOTH
/// provisioned AppX packages and optional features/capabilities (the Customize
/// Apps tab filters to AppX; the Windows Components tab filters to features).
/// </summary>
public sealed class CompositeComponentCatalog : IComponentCatalogProvider
{
    private readonly IReadOnlyList<ComponentDefinition> _definitions;

    public CompositeComponentCatalog(params IComponentCatalogProvider[] providers)
    {
        _definitions = providers
            .SelectMany(p => p.GetDefinitions())
            .ToList();
    }

    public IReadOnlyList<ComponentDefinition> GetDefinitions() => _definitions;
}
