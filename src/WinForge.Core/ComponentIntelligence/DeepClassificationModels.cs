using System.Collections.Generic;

namespace WinForge.Core.ComponentIntelligence;

// =====================================================================
// Phase 14 — DEEP COMPONENT COVERAGE & CLASSIFICATION (Stage 14.1)
//
// LAYER SEPARATION (must not collapse):
//   Discovery  = WHAT EXISTS (ComponentInventoryEntry / IRawInventoryItem)
//   Knowledge  = WHAT IT MEANS (DeepComponentKnowledge below)
//   Planning   = WHAT TO DO (OptimizationDefinition / plan compiler)
//
// Safety > removal count. Heuristic classification NEVER silently becomes a
// removal rule; Protected/Critical stays protected regardless of confidence.
// =====================================================================

/// <summary>
/// FUNCTIONAL category of a component (user-meaningful). Distinct from
/// <see cref="ComponentCategory"/> which describes the discovery SOURCE
/// (AppX / Capability / OptionalFeature / CbsPackage / Service …).
/// </summary>
public enum ComponentFunctionCategory
{
    Unknown = 0,
    Communication,
    Gaming,
    Media,
    Productivity,
    Developer,
    Networking,
    RemoteAccess,
    PrintingScanning,
    Input,
    Accessibility,
    Security,
    Servicing,
    DiagnosticsTelemetry,
    CloudIntegration,
    Search,
    ShellExperience,
    HardwareSupport,
    Virtualization,
    Enterprise,
    LegacyCompatibility,
    Language,
    Recovery,
    StoreInfrastructure,
    RuntimeDependency,
    SystemCore,
}

/// <summary>Removal/impact risk. LOW is never assigned to protected infrastructure.</summary>
public enum ComponentRiskLevel
{
    Unknown = 0,
    Low,
    Moderate,
    High,
    Critical,
}

/// <summary>Product recommendation semantics (localized in UI, stable in model).</summary>
public enum ComponentRecommendationKind
{
    Unknown = 0,
    RecommendedRemove,
    OptionalRemove,
    RecommendedKeep,
    RequiredKeep,
    ProfileDependent,
}

/// <summary>Protection status — trumps recommendation for planning safety.</summary>
public enum ComponentProtectionLevel
{
    None = 0,
    Sensitive,   // dependency-sensitive: removal only with explicit user intent
    Protected,   // system/security/servicing/runtime: never auto-removed
}

/// <summary>How confident we are in a classification (ADR-085).</summary>
public enum ClassificationConfidence
{
    Unknown = 0,

    /// <summary>Hand-maintained, reviewed catalog entry.</summary>
    Curated,

    /// <summary>Exact known identifier/alias match against a curated entry.</summary>
    KnownPattern,

    /// <summary>Normalized family pattern match (e.g. Microsoft-Windows-Printing-*).</summary>
    KnownFamily,

    /// <summary>Inferred from heuristics — never a removal rule by itself.</summary>
    Heuristic,
}

/// <summary>Stable profile tag used for profile relevance (Gaming etc.).</summary>
public enum ComponentProfileTag
{
    None = 0,
    GamingRelevant,
    ConsumerContent,
    PhoneIntegration,
    CloudStorage,
    PrintScan,
    Virtualization,
    DeveloperTool,
    RemoteAccess,
    MediaPlayback,
    AccessibilityTool,
    SecurityEssential,
    ServicingEssential,
    StoreInfrastructure,
    RuntimeDependency,
}

/// <summary>
/// Canonical KNOWLEDGE about a component family (or single component). The
/// classifier maps discovered raw identities onto these entries.
/// </summary>
public sealed class DeepComponentKnowledge
{
    public string CanonicalId { get; init; } = string.Empty;
    public string DisplayNameKey { get; init; } = string.Empty;
    public string DescriptionKey { get; init; } = string.Empty;

    /// <summary>English fallback display name (used when the resx key is absent).</summary>
    public string DisplayNameFallback { get; init; } = string.Empty;

    /// <summary>English fallback purpose text (used when the resx key is absent).</summary>
    public string DescriptionFallback { get; init; } = string.Empty;

    public ComponentFunctionCategory Function { get; init; } = ComponentFunctionCategory.Unknown;
    public string? Subcategory { get; init; }

    public ComponentRiskLevel Risk { get; init; } = ComponentRiskLevel.Unknown;
    public ComponentRecommendationKind Recommendation { get; init; } = ComponentRecommendationKind.Unknown;
    public ComponentProtectionLevel Protection { get; init; } = ComponentProtectionLevel.None;
    public ComponentProfileTag ProfileTag { get; init; } = ComponentProfileTag.None;

    public ClassificationConfidence Confidence { get; init; } = ClassificationConfidence.Unknown;
    public string? NotesKey { get; init; }

    /// <summary>Comma-separated dependency/relationship hints (stable ids).</summary>
    public IReadOnlyList<string> DependencyTags { get; init; } = new List<string>();
}

/// <summary>Catalog row — how a family is MATCHED (patterns) and WHAT IT MEANS.</summary>
public sealed class DeepCatalogEntry
{
    public string Id { get; init; } = string.Empty;
    public string DisplayNameKey { get; init; } = string.Empty;
    public string DescriptionKey { get; init; } = string.Empty;

    /// <summary>English fallback display name (UI when resx key absent).</summary>
    public string DisplayNameFallback { get; init; } = string.Empty;

    /// <summary>English fallback purpose text (UI when resx key absent).</summary>
    public string DescriptionFallback { get; init; } = string.Empty;

    /// <summary>Normalized patterns matched against normalized identities (contains).</summary>
    public IReadOnlyList<string> Patterns { get; init; } = new List<string>();

    /// <summary>Exact alias identifiers (normalized equality).</summary>
    public IReadOnlyList<string> Aliases { get; init; } = new List<string>();

    public ComponentFunctionCategory Function { get; init; } = ComponentFunctionCategory.Unknown;
    public string? Subcategory { get; init; }
    public ComponentRiskLevel Risk { get; init; } = ComponentRiskLevel.Unknown;
    public ComponentRecommendationKind Recommendation { get; init; } = ComponentRecommendationKind.Unknown;
    public ComponentProtectionLevel Protection { get; init; } = ComponentProtectionLevel.None;
    public ComponentProfileTag ProfileTag { get; init; } = ComponentProfileTag.None;
    public ClassificationConfidence Confidence { get; init; } = ClassificationConfidence.KnownPattern;
    public string? NotesKey { get; init; }
    public IReadOnlyList<string> DependencyTags { get; init; } = new List<string>();
}
