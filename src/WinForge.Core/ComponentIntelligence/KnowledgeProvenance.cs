using System;
using System.Collections.Generic;

namespace WinForge.Core.Models;

/// <summary>Where a piece of component knowledge originated.</summary>
public enum KnowledgeSourceType
{
    Unknown = 0,

    /// <summary>Microsoft documentation / official servicing identifiers.</summary>
    MicrosoftOfficial,

    /// <summary>Discovered directly from the Windows image being serviced.</summary>
    WindowsImageDiscovery,

    /// <summary>A maintained community project list (e.g. Win11Debloat).</summary>
    CommunityProject,

    /// <summary>A WinForge-maintained, human-reviewed correction.</summary>
    WinForgeCurated,

    /// <summary>Empirical validation against a real image / removal test.</summary>
    EmpiricalValidation
}

/// <summary>Confidence in a knowledge claim.</summary>
public enum ConfidenceLevel
{
    Unknown = 0,
    Low,
    Medium,
    High,
    Verified
}

/// <summary>The kind of statement a <see cref="KnowledgeClaim"/> makes.</summary>
public enum KnowledgeClaimKind
{
    Unknown = 0,

    /// <summary>An objective statement about what the component IS / does.</summary>
    Fact,

    /// <summary>A WinForge-authored recommendation for an ordinary user.</summary>
    Recommendation
}

/// <summary>
/// A single provenance source for one or more knowledge claims. A source records
/// WHERE the information came from and HOW confident WinForge is in it.
/// </summary>
public sealed class KnowledgeSource
{
    public KnowledgeSourceType SourceType { get; init; } = KnowledgeSourceType.Unknown;

    /// <summary>Human-readable source name (e.g. "Microsoft Learn", "Win11Debloat", "WinForge review").</summary>
    public string SourceName { get; init; } = string.Empty;

    /// <summary>Optional reference (doc id, script name, image build). Not a raw URL in the UI.</summary>
    public string? SourceReference { get; init; }

    public DateTime? RetrievedOrReviewedAt { get; init; }

    public ConfidenceLevel Confidence { get; init; } = ConfidenceLevel.Unknown;

    public KnowledgeSource()
    {
    }

    public KnowledgeSource(KnowledgeSourceType sourceType, string sourceName, ConfidenceLevel confidence,
        string? sourceReference = null, DateTime? retrievedOrReviewedAt = null)
    {
        SourceType = sourceType;
        SourceName = sourceName;
        Confidence = confidence;
        SourceReference = sourceReference;
        RetrievedOrReviewedAt = retrievedOrReviewedAt;
    }
}

/// <summary>
/// A single knowledge claim attached to a component. Every meaningful claim carries
/// provenance and is explicitly tagged as a <see cref="KnowledgeClaimKind.Fact"/>
/// (what the component is) or a <see cref="KnowledgeClaimKind.Recommendation"/>
/// (what WinForge advises) — the two are deliberately separated so a community
/// script that removes an item never silently becomes a WinForge "RecommendedRemove".
/// </summary>
public sealed class KnowledgeClaim
{
    public KnowledgeClaimKind Kind { get; init; } = KnowledgeClaimKind.Unknown;

    /// <summary>Localization key for the claim text (resolved via ILocalizationService).</summary>
    public string TextKey { get; init; } = string.Empty;

    public IReadOnlyList<KnowledgeSource> Sources { get; init; } = new List<KnowledgeSource>();

    public KnowledgeClaim()
    {
    }

    public KnowledgeClaim(KnowledgeClaimKind kind, string textKey, IReadOnlyList<KnowledgeSource> sources)
    {
        Kind = kind;
        TextKey = textKey;
        Sources = sources;
    }
}

/// <summary>
/// Per-scenario recommendation override (Part I — Profile foundation). A component
/// may advise a different <see cref="RecommendationLevel"/> under a specific user
/// scenario (e.g. Xbox titles usually OptionalRemove, but UsuallyKeep for a Gaming
/// profile). No automatic selection is implied — this only changes the displayed
/// recommendation when a scenario is active.
/// </summary>
public sealed class ScenarioRecommendation
{
    public ComponentScenario Scenario { get; init; } = ComponentScenario.Unknown;

    public RecommendationLevel Recommendation { get; init; } = RecommendationLevel.Unknown;

    /// <summary>Localization key explaining WHY the scenario changes the advice.</summary>
    public string ReasonKey { get; init; } = string.Empty;

    public ScenarioRecommendation()
    {
    }

    public ScenarioRecommendation(ComponentScenario scenario, RecommendationLevel recommendation, string reasonKey)
    {
        Scenario = scenario;
        Recommendation = recommendation;
        ReasonKey = reasonKey;
    }
}

public sealed partial class ComponentDefinition
{
    /// <summary>
    /// Knowledge provenance: the fact/recommendation claims and where they came
    /// from. Empty for entries that have not yet been migrated to the provenance
    /// model — Unknown is preferred over invented provenance.
    /// </summary>
    public IReadOnlyList<KnowledgeClaim> Provenance { get; init; } = new List<KnowledgeClaim>();

    /// <summary>
    /// Scenario-specific recommendation overrides (Part I). When a scenario is
    /// active, <see cref="ResolveRecommendation"/> returns the matching override
    /// instead of <see cref="Recommendation"/>. Does not trigger any selection.
    /// </summary>
    public IReadOnlyList<ScenarioRecommendation> ScenarioRecommendations { get; init; }
        = new List<ScenarioRecommendation>();

    /// <summary>
    /// Returns the recommendation that applies for the given scenario: the matching
    /// <see cref="ScenarioRecommendation"/> when present, otherwise the default
    /// <see cref="Recommendation"/>. Never mutates state.
    /// </summary>
    public RecommendationLevel ResolveRecommendation(ComponentScenario? scenario)
    {
        if (scenario is null or ComponentScenario.Unknown)
        {
            return Recommendation;
        }

        foreach (var sr in ScenarioRecommendations)
        {
            if (sr.Scenario == scenario)
            {
                return sr.Recommendation;
            }
        }

        return Recommendation;
    }
}
