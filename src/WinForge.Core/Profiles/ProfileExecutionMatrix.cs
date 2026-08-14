using System;
using System.Collections.Generic;
using System.Linq;
using WinForge.Core.ComponentIntelligence;
using WinForge.Core.Models;

namespace WinForge.Core.Profiles;

// =====================================================================
// Phase 15 Stage 15.1 — PROFILE EXECUTION MATRIX (ADR-094 §1)
//
// The canonical disposition policy. PURE and deterministic: given the engine's
// profile-aware EffectiveRecommendation + knowledge fields + execution support,
// it returns (ProfileDisposition, reasonKey). The PRIMARY decision mechanism is
// knowledge/risk/support — never raw Windows identity strings.
//
// Safety (unchanged from Phase 14, re-verified here):
//   Protected -> Keep/Blocked        Critical -> Blocked
//   High -> never AutoApply          Heuristic -> never AutoApply
//   Unsupported execution -> Blocked Manual override -> authoritative
// =====================================================================

public static class ProfileExecutionMatrix
{
    /// <summary>
    /// Profiles that allow LOW-risk profile-driven changes to auto-apply.
    /// All six primaries may auto-apply low-risk curated changes; none may
    /// auto-apply Moderate+ risk or heuristic knowledge.
    /// </summary>
    private static readonly HashSet<string> AutoApplyProfiles = new(StringComparer.Ordinal)
    {
        "Balanced", "Gaming", "DedicatedGaming", "Developer", "Office", "Lightweight",
    };

    public static (ProfileDisposition Disposition, string ReasonKey) Evaluate(
        string profileId,
        EffectiveRecommendation effective,
        ComponentProtectionLevel protection,
        ClassificationConfidence confidence,
        bool executionSupported,
        bool isHeuristic)
    {
        ArgumentNullException.ThrowIfNull(effective);

        // Absent items are never planned (Part O).
        if (!effective.IsPresent)
        {
            return (ProfileDisposition.NotApplicable, string.Empty);
        }

        // Manual override: the user's explicit choice is authoritative. The item
        // is listed (so the preview stays honest) but NEVER auto-applied, and the
        // planner never touches it when the profile changes (Part K).
        if (effective.WasOverridden)
        {
            return OverriddenDisposition(effective);
        }

        // Tier 1 — safety: blocked beats everything. Critical is never acted on;
        // High is never AUTOMATIC (it still surfaces as a Recommend, §11).
        if (effective.Level == EffectiveRecommendationLevel.Blocked
            || effective.Risk == RiskLevel.Critical)
        {
            return (ProfileDisposition.Blocked, "Profile.Reason.Execution.KeepProtected");
        }

        // Protected infrastructure: kept, never acted on.
        if (protection == ComponentProtectionLevel.Protected)
        {
            return (ProfileDisposition.Keep, "Profile.Reason.Execution.KeepProtected");
        }

        // Required/runtime keep semantics.
        if (effective.Level == EffectiveRecommendationLevel.RecommendKeep)
        {
            var reason = effective.ReasonKeys?.FirstOrDefault()
                ?? "Profile.Reason.Execution.KeepRuntime";
            return (ProfileDisposition.Keep, reason);
        }

        // Change levels (Remove / Disable / Set).
        if (effective.Level is EffectiveRecommendationLevel.RecommendRemove
            or EffectiveRecommendationLevel.RecommendDisable
            or EffectiveRecommendationLevel.RecommendSet)
        {
            if (!executionSupported)
            {
                // Known != Removable: no supported mechanism -> blocked from the plan.
                return (ProfileDisposition.Blocked, ExecutionSupportMatrix.BlockReasonKey);
            }

            if (effective.Risk == RiskLevel.High)
            {
                // High: never automatic; at most a recommended (user-confirmed) change.
                return (ProfileDisposition.Recommend, ReasonFor(effective));
            }

            var canAuto = AutoApplyProfiles.Contains(profileId)
                && effective.Risk == RiskLevel.Low
                && !isHeuristic            // heuristic knowledge NEVER auto-applies
                && effective.WasProfileDriven; // profile intent / gaming policy ONLY — curated defaults stay Recommend
            return canAuto
                ? (ProfileDisposition.AutoApply, ReasonFor(effective))
                : (ProfileDisposition.Recommend, ReasonFor(effective));
        }

        // ManualReview -> optional, user-confirmed suggestion. The engine's own
        // deterministic reason (e.g. Dedicated Gaming's media suggestion) is kept.
        // Stage 15.2: an unsupported suggestion is BLOCKED, not "optional" — an
        // optional item must always be executable (ADR-095 §4/§11).
        if (effective.Level == EffectiveRecommendationLevel.ManualReview)
        {
            if (!executionSupported)
            {
                return (ProfileDisposition.Blocked, ExecutionSupportMatrix.BlockReasonKey);
            }

            return (ProfileDisposition.Optional, ReasonFor(effective, "Profile.Reason.Execution.Optional"));
        }

        return (ProfileDisposition.NotApplicable, string.Empty);
    }

    private static string ReasonFor(EffectiveRecommendation effective, string fallback = "Profile.Reason.Execution.Recommend")
    {
        var key = effective.ReasonKeys?.FirstOrDefault();
        return string.IsNullOrWhiteSpace(key) ? fallback : key;
    }

    private static (ProfileDisposition, string) OverriddenDisposition(EffectiveRecommendation effective)
        => effective.Level switch
        {
            EffectiveRecommendationLevel.RecommendKeep => (ProfileDisposition.Keep, "Profile.Reason.UserOverride"),
            EffectiveRecommendationLevel.Blocked => (ProfileDisposition.Blocked, "Profile.Reason.Safety"),
            _ => (ProfileDisposition.Recommend, "Profile.Reason.UserOverride"),
        };
}
