using System.Collections.Generic;
using WinForge.Core.Models;

namespace WinForge.Core.Profiles;

/// <summary>
/// The engine's per-item input. Both knowledge row types (component and
/// optimization) map their definition here — the engine itself is fully generic
/// and never touches view models or catalogs.
/// </summary>
public sealed class RecommendationInput
{
    /// <summary>Stable logical id (component / optimization id).</summary>
    public string LogicalId { get; init; } = string.Empty;

    public OptimizationAction Action { get; init; } = OptimizationAction.Remove;

    public RecommendationLevel DefaultRecommendation { get; init; } = RecommendationLevel.Unknown;

    public RiskLevel Risk { get; init; } = RiskLevel.Unknown;

    public RemovalSupport Removal { get; init; } = RemovalSupport.Unknown;

    /// <summary>True when the item exists in the mounted image (RawItems&gt;0 / applicable).</summary>
    public bool IsPresent { get; init; } = true;

    /// <summary>True when the change can actually be applied to the offline image.</summary>
    public bool IsApplySupported { get; init; } = true;

    public IReadOnlyList<ComponentDependency> Dependencies { get; init; } = new List<ComponentDependency>();

    /// <summary>
    /// Phase 14.3 (ADR-088/090): post-safety-gate knowledge-driven decision from the
    /// gaming pipeline. Null when no gaming policy is active. When present it is a
    /// PROFILE INTENT — evaluated AFTER requirement/dependency tiers and BEFORE the
    /// legacy scenario overrides, so explicit extra-scenario rules still win. The
    /// Safety Gate has already run; the engine never re-derives it.
    /// </summary>
    public GamingPolicyDecision? GamingDecision { get; init; }
}

/// <summary>
/// The engine's evaluation context: which profiles are selected, which items the
/// user has explicitly chosen (overrides), and which logical ids are actually
/// present in the image (Part O — real image state).
/// </summary>
public sealed class RecommendationContext
{
    public IReadOnlyList<ProfileDefinition> SelectedProfiles { get; init; } = new List<ProfileDefinition>();

    /// <summary>Logical ids the user manually toggled at least once (Part K).</summary>
    public IReadOnlyCollection<string> UserOverrides { get; init; } = new HashSet<string>();

    /// <summary>Logical ids present in the mounted image (or applicable for catalog tabs).</summary>
    public IReadOnlyCollection<string> PresentIds { get; init; } = new HashSet<string>();
}

/// <summary>
/// Level↔<see cref="RecommendationLevel"/> mapping used for display captions and
/// default evaluation. The engine reasons in shared levels; views render
/// action-aware language (Part L).
/// </summary>
public static class EffectiveRecommendationMappings
{
    /// <summary>Shared level used for display captions (action-aware key suffix).</summary>
    public static RecommendationLevel ToRecommendationLevel(this EffectiveRecommendationLevel level) => level switch
    {
        EffectiveRecommendationLevel.RecommendRemove => RecommendationLevel.RecommendedRemove,
        EffectiveRecommendationLevel.RecommendDisable => RecommendationLevel.RecommendedRemove,
        EffectiveRecommendationLevel.RecommendSet => RecommendationLevel.RecommendedRemove,
        EffectiveRecommendationLevel.RecommendKeep => RecommendationLevel.UsuallyKeep,
        EffectiveRecommendationLevel.ManualReview => RecommendationLevel.OptionalRemove,
        EffectiveRecommendationLevel.Blocked => RecommendationLevel.NeverRemove,
        _ => RecommendationLevel.Unknown
    };

    /// <summary>Neutral default evaluation of a curated recommendation (tier 6).</summary>
    public static EffectiveRecommendationLevel FromDefault(RecommendationLevel recommendation) => recommendation switch
    {
        RecommendationLevel.RecommendedRemove => EffectiveRecommendationLevel.RecommendRemove,
        RecommendationLevel.NeverRemove => EffectiveRecommendationLevel.Blocked,
        RecommendationLevel.UsuallyKeep => EffectiveRecommendationLevel.RecommendKeep,
        _ => EffectiveRecommendationLevel.ManualReview
    };

    /// <summary>The effective level a TRIM intent maps to for a given action type.</summary>
    public static EffectiveRecommendationLevel TrimForAction(OptimizationAction action) => action switch
    {
        OptimizationAction.Remove => EffectiveRecommendationLevel.RecommendRemove,
        OptimizationAction.Disable or OptimizationAction.Feature => EffectiveRecommendationLevel.RecommendDisable,
        OptimizationAction.Configure or OptimizationAction.Service => EffectiveRecommendationLevel.RecommendSet,
        _ => EffectiveRecommendationLevel.RecommendRemove
    };
}
