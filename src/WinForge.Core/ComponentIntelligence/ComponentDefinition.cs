using System.Collections.Generic;

namespace WinForge.Core.Models;

/// <summary>
/// Maps a logical, human-facing WinForge component onto one or more raw Windows
/// identities. A single logical component (e.g. "Xbox Gaming Services") may map to
/// multiple AppX packages / capabilities / features / CBS packages / services. The
/// UI depends on the logical component, never on raw package names.
/// </summary>
public sealed class TechnicalTarget
{
    public ComponentCategory Category { get; init; } = ComponentCategory.Unknown;
    public MatchMethod Match { get; init; } = MatchMethod.Prefix;
    public string Pattern { get; init; } = string.Empty;

    /// <summary>Optional human note about the mapping (e.g. "consumer build only").</summary>
    public string? Note { get; init; }
}

/// <summary>An edge in the component dependency graph.</summary>
public sealed class ComponentDependency
{
    /// <summary>Other <see cref="ComponentDefinition.Id"/> this edge references.</summary>
    public string ToId { get; init; } = string.Empty;

    public DependencyRelation Relation { get; init; } = DependencyRelation.RelatedTo;
    public string? Reason { get; init; }
}

/// <summary>
/// Per-release compatibility metadata. Catalog definitions support version
/// constraints so a match known for Windows 11 25H2 does not blind-map a future
/// or past release. A null field means "any".
/// </summary>
public sealed class CompatibilityRule
{
    public string? SupportedBuildMin { get; init; }
    public string? SupportedBuildMax { get; init; }
    public IReadOnlyList<string> KnownOnBuilds { get; init; } = new List<string>();
    public string? Edition { get; init; }
    public string? Architecture { get; init; }
    public string? Language { get; init; }
}

/// <summary>
/// A stable, human-facing WinForge knowledge entry about a Windows component.
/// Text fields are localization keys (resolved via <see cref="ILocalizationService"/>);
/// Unknown is preferred over invented information — leave an enum at Unknown or a
/// list empty rather than guessing.
/// </summary>
public sealed partial class ComponentDefinition
{
    /// <summary>Stable WinForge identifier (not a Windows package name).</summary>
    public string Id { get; init; } = string.Empty;

    public ComponentCategory Category { get; init; } = ComponentCategory.Unknown;

    public string DisplayNameKey { get; init; } = string.Empty;
    public string ShortDescriptionKey { get; init; } = string.Empty;
    public string LongDescriptionKey { get; init; } = string.Empty;

    public RecommendationLevel Recommendation { get; init; } = RecommendationLevel.Unknown;
    public RiskLevel Risk { get; init; } = RiskLevel.Unknown;
    public RemovalSupport Removal { get; init; } = RemovalSupport.Unknown;
    public RestoreSupport Restore { get; init; } = RestoreSupport.Unknown;

    public IReadOnlyList<ComponentScenario> UserScenarios { get; init; } = new List<ComponentScenario>();
    public IReadOnlyList<string> KeepIf { get; init; } = new List<string>();
    public IReadOnlyList<string> RemoveIf { get; init; } = new List<string>();
    public IReadOnlyList<string> KnownImpact { get; init; } = new List<string>();
    public IReadOnlyList<ComponentDependency> Dependencies { get; init; } = new List<ComponentDependency>();
    public IReadOnlyList<string> Conflicts { get; init; } = new List<string>();

    public IReadOnlyList<TechnicalTarget> TechnicalTargets { get; init; } = new List<TechnicalTarget>();
    public IReadOnlyList<CompatibilityRule> CompatibilityRules { get; init; } = new List<CompatibilityRule>();

    public long EstimatedSavingsBytes { get; init; }
    public SavingsConfidence SavingsConfidence { get; init; } = SavingsConfidence.None;

    public IReadOnlyList<string> Tags { get; init; } = new List<string>();
}
