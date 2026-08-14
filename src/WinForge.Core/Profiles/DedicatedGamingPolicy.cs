using System.Collections.Generic;
using WinForge.Core.ComponentIntelligence;

namespace WinForge.Core.Profiles;

/// <summary>
/// DEDICATED GAMING policy (ADR-089): a more minimal gaming-only machine. It is
/// NOT a kiosk mode and NOT "remove everything not needed for gaming": it keeps
/// the full Gaming PC keep list (servicing, Windows Update, Defender, Store /
/// winget, Gaming Services, Xbox ecosystem when enabled, runtimes, DirectX,
/// audio, networking, GPU/display, input, USB, storage, shell/login, WinRE,
/// boot) and only adds OPTIONAL/RECOMMENDED suggestions — nothing is forced.
///
/// Difference vs <see cref="GamingPcPolicy"/> (Stage 15.2, ADR-095 §3 — real
/// profile differentiation on real media, NOT cosmetic):
///   - Low-risk cloud integration (OneDrive): Gaming PC keeps it optional
///     (convenience); Dedicated Gaming AUTO-removes (still Low risk, curated,
///     supported AppX removal).
///   - Moderate productivity/communication apps: Gaming PC leaves them at the
///     default (optional); Dedicated Gaming RECOMMENDS removal (user confirms —
///     Moderate never auto-applies).
///   - Moderate media: Dedicated suggests OPTIONAL removal (Gaming PC keeps the
///     default convenience).
/// </summary>
public sealed class DedicatedGamingPolicy : GamingPcPolicy
{
    public override GamingProfileKind Kind => GamingProfileKind.DedicatedGaming;

    /// <summary>
    /// Dedicated Gaming's wider minimal steer (never automatic beyond Low-risk
    /// curated support; health/compatibility keep list fully inherited).
    /// </summary>
    protected override GamingPolicyDecision? WiderMinimalSteer(DeepComponentKnowledge k)
    {
        // Low-risk cloud integration (OneDrive): Gaming PC keeps convenience
        // (optional); Dedicated Gaming may auto-remove — Low risk + curated +
        // supported AppX removal, safety gate unchanged.
        if (k.Risk == ComponentRiskLevel.Low && k.ProfileTag == ComponentProfileTag.CloudStorage
            && k.Recommendation is ComponentRecommendationKind.OptionalRemove
                or ComponentRecommendationKind.RecommendedRemove
                or ComponentRecommendationKind.ProfileDependent)
        {
            return new GamingPolicyDecision
            {
                Kind = Kind,
                Verdict = GamingVerdict.AutoRemoveCandidate,
                ReasonKey = "Profile.Reason.Gaming.Dedicated.Optional.Cloud",
            };
        }

        // Moderate productivity/communication: Gaming PC leaves at the default
        // (optional); Dedicated Gaming RECOMMENDS removal — Moderate maps to
        // Recommend in the execution matrix, never automatic.
        if (k.Risk == ComponentRiskLevel.Moderate && k.Protection != ComponentProtectionLevel.Protected
            && (k.Function is ComponentFunctionCategory.Productivity or ComponentFunctionCategory.Communication)
            && k.Recommendation is ComponentRecommendationKind.OptionalRemove
                or ComponentRecommendationKind.ProfileDependent)
        {
            var family = k.Function == ComponentFunctionCategory.Productivity ? "Productivity" : "Communication";
            return new GamingPolicyDecision
            {
                Kind = Kind,
                Verdict = GamingVerdict.AutoRemoveCandidate,
                ReasonKey = "Profile.Reason.Gaming.Dedicated.Optional." + family,
            };
        }

        // Moderate media: Dedicated suggests OPTIONAL removal (never automatic).
        if (k.Risk == ComponentRiskLevel.Moderate && k.Function == ComponentFunctionCategory.Media
            && k.Recommendation is ComponentRecommendationKind.OptionalRemove
                or ComponentRecommendationKind.ProfileDependent)
        {
            return new GamingPolicyDecision
            {
                Kind = Kind,
                Verdict = GamingVerdict.OptionalRemoveCandidate,
                ReasonKey = "Profile.Reason.Gaming.Optional.Media",
            };
        }

        return null;
    }
}
