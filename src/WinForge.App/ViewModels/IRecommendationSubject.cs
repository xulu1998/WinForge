using WinForge.App.Services;
using WinForge.Core.Models;
using WinForge.Core.Profiles;

namespace WinForge.App.ViewModels;

/// <summary>
/// A unified view over one recommendation subject (a knowledge row in any
/// Customize tab) for the Stage 11.4 profile engine. Both row types —
/// <see cref="ComponentKnowledgeItem"/> (Apps / Windows Components) and
/// <see cref="OptimizationKnowledgeItem"/> (Services / Privacy / System /
/// Personalization) — implement this so the profile selector, the preview and
/// the adopt command can treat all six tabs uniformly (Part H/I/J).
/// </summary>
public interface IRecommendationSubject
{
    /// <summary>Stable logical id (component / optimization id).</summary>
    string LogicalId { get; }

    /// <summary>Which Customize tab the row lives in (Part 11 per-tab breakdown).</summary>
    OptimizationTab Tab { get; }

    string DisplayName { get; }

    /// <summary>True when the item exists in the mounted image / applies to it.</summary>
    bool IsPresent { get; }

    bool IsSelectable { get; }

    /// <summary>Plan-backed selection (manual toggle marks a user override, Part K).</summary>
    bool IsSelected { get; set; }

    bool WasOverridden { get; }

    bool HasConflict { get; }

    EffectiveRecommendation Effective { get; }

    /// <summary>Action-aware recommendation caption for the current effective level.</summary>
    string RecommendationCaption { get; }

    /// <summary>Localized deterministic "why" text (Part F — never AI prose).</summary>
    string ReasonText { get; }

    /// <summary>Localized conflict/resolution text (empty when no conflict).</summary>
    string ConflictText { get; }

    /// <summary>
    /// Localized selection-origin caption for the row (final flow): empty when
    /// the selection is untouched, "由「X」自动选择" when the active profile
    /// auto-applied it, "手动选择" when the user explicitly toggled it.
    /// </summary>
    string SelectionOriginText { get; }

    /// <summary>Recomputes the effective recommendation from the shared context.</summary>
    void RefreshRecommendation(RecommendationContextService context);

    /// <summary>Selects/deselects WITHOUT marking a user override (adopt/reapply path).</summary>
    void SetSelectedForAdoption(bool selected);
}
