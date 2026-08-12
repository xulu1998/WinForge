using System.Collections.Generic;
using WinForge.Core.Models;

namespace WinForge.Core.Profiles;

/// <summary>
/// A visible profile conflict: one selected profile wants to keep an item while
/// another wants to trim it. The engine NEVER silently picks one side — the
/// resolution (KEEP wins when a selected scenario requires the component) is
/// computed deterministically AND surfaced with a reason (Part G).
/// </summary>
public sealed class RecommendationConflict
{
    public string TargetId { get; init; } = string.Empty;

    /// <summary>The profile whose requirement/dependency/keep rule won.</summary>
    public string KeepProfileId { get; init; } = string.Empty;

    /// <summary>The profile whose trim rule lost.</summary>
    public string TrimProfileId { get; init; } = string.Empty;

    /// <summary>Deterministic localization key (e.g. <c>Profile.Reason.Conflict.KeepWins</c>).</summary>
    public string ReasonKey { get; init; } = string.Empty;
}

/// <summary>
/// The output of the recommendation engine for ONE item. Computed SEPARATELY from
/// the definition's default recommendation — the definition is never mutated
/// (Part E). Deterministic reason keys, never runtime AI prose (Part F).
/// </summary>
public sealed class EffectiveRecommendation
{
    public EffectiveRecommendationLevel Level { get; init; } = EffectiveRecommendationLevel.Unknown;

    /// <summary>True when the item actually exists in the mounted image / applies to it.</summary>
    public bool IsPresent { get; init; } = true;

    /// <summary>True when the item's change can actually be applied to the offline image.</summary>
    public bool IsApplySupported { get; init; } = true;

    /// <summary>Risk carried through from the definition — Part J gates auto-selection on Risk == Low.</summary>
    public RiskLevel Risk { get; init; } = RiskLevel.Unknown;

    /// <summary>True when the user manually changed this item — recalc must not overwrite it (Part K).</summary>
    public bool WasOverridden { get; init; }

    /// <summary>True when at least one profile rule changed the outcome vs the component default.</summary>
    public bool WasProfileDriven { get; init; }

    public bool HasConflict { get; init; }

    /// <summary>Deterministic localized reason keys ("为什么") in evaluation order.</summary>
    public IReadOnlyList<string> ReasonKeys { get; init; } = new List<string>();

    /// <summary>Rule ids that fired (for debugging / tests), e.g. <c>Gaming|keep:XboxApp</c>.</summary>
    public IReadOnlyList<string> SourceRuleIds { get; init; } = new List<string>();

    public IReadOnlyList<RecommendationConflict> Conflicts { get; init; } = new List<RecommendationConflict>();

    /// <summary>
    /// Neutral mapping of a definition's curated recommendation — the engine's
    /// "component default" tier (level 6 in the documented precedence).
    /// </summary>
    public static EffectiveRecommendation FromDefault(
        RecommendationLevel recommendation,
        bool isPresent = true,
        bool isApplySupported = true)
        => new()
        {
            Level = recommendation switch
            {
                RecommendationLevel.RecommendedRemove => EffectiveRecommendationLevel.RecommendRemove,
                RecommendationLevel.NeverRemove => EffectiveRecommendationLevel.Blocked,
                RecommendationLevel.UsuallyKeep => EffectiveRecommendationLevel.RecommendKeep,
                _ => EffectiveRecommendationLevel.ManualReview
            },
            IsPresent = isPresent,
            IsApplySupported = isApplySupported
        };
}
