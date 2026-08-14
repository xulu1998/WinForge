using System;
using WinForge.Core.ComponentIntelligence;

namespace WinForge.Core.Profiles;

/// <summary>
/// PROFILE SAFETY GATE — final authority (ADR-090). Applied AFTER the gaming
/// policy verdict and BEFORE anything may reach the plan layer. A candidate can
/// only act automatically when the component is supported, not protected, risk is
/// acceptable, recommendation semantics permit it, and no dependency/extra
/// requires it (those already produced a Keep verdict upstream).
///
/// Rules (Part C §11):
/// <list type="bullet">
///   <item><description>Protected → never acted on (Block).</description></item>
///   <item><description>Critical → never automatic (Block).</description></item>
///   <item><description>High → never automatic in any Gaming profile (Block).</description></item>
///   <item><description>Moderate → optional / user-dependent only (AllowOptional).</description></item>
///   <item><description>Low + curated knowledge support → may auto-recommend (AllowAuto).</description></item>
///   <item><description>Heuristic classification → never auto-remove solely from
///   heuristic classification (AllowOptional at most).</description></item>
///   <item><description>Unsupported (no ALREADY-SUPPORTED safe action) → Block.</description></item>
///   <item><description>User override → Block (the manual choice stays authoritative).</description></item>
/// </list>
/// </summary>
public static class ProfileSafetyGate
{
    public static GamingEvaluationResult Evaluate(GamingPolicyDecision decision, GamingPolicyInput input)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(input);

        var k = input.Knowledge;
        var result = new GamingEvaluationResult
        {
            RawIdentity = input.RawIdentity,
            CanonicalId = k.CanonicalId,
            Source = input.Source,
            Function = k.Function,
            Risk = k.Risk,
            Protection = k.Protection,
            Confidence = k.Confidence,
            Verdict = decision.Verdict,
            ReasonKey = decision.ReasonKey,
            KeptByExtra = decision.KeptByExtra,
            HasUserOverride = input.HasUserOverride,
        };

        if (decision.Verdict == GamingVerdict.KeepForCompatibility)
        {
            // Kept — not an action; the plan layer sees a keep, never a change.
            result.Gate = GateVerdict.Block;
            return result;
        }

        if (decision.Verdict == GamingVerdict.NoOpinion)
        {
            result.Gate = GateVerdict.Block;
            result.GateReasonKey = string.Empty;
            return result;
        }

        // ---- change candidates (Auto/Optional remove) — gate has final say ----
        if (input.HasUserOverride)
        {
            return Block(result, "Profile.Reason.Gaming.Gate.UserOverride");
        }

        if (!input.SupportedForRemoval)
        {
            // ADR-086: classification != execution support.
            return Block(result, "Profile.Reason.Gaming.Gate.Unsupported");
        }

        if (k.Protection == ComponentProtectionLevel.Protected)
        {
            return Block(result, "Profile.Reason.Gaming.Gate.Protected");
        }

        if (k.Risk == ComponentRiskLevel.Critical)
        {
            return Block(result, "Profile.Reason.Gaming.Gate.Critical");
        }

        if (k.Risk == ComponentRiskLevel.High)
        {
            return Block(result, "Profile.Reason.Gaming.Gate.High");
        }

        if (k.Recommendation is ComponentRecommendationKind.RequiredKeep
            or ComponentRecommendationKind.RecommendedKeep)
        {
            return Block(result, "Profile.Reason.Gaming.Gate.Recommendation");
        }

        if (k.Risk == ComponentRiskLevel.Moderate)
        {
            result.Gate = GateVerdict.AllowOptional;
            result.GateReasonKey = "Profile.Reason.Gaming.Gate.ModerateOptional";
            return result;
        }

        if (k.Confidence == ClassificationConfidence.Heuristic)
        {
            // Never auto-remove solely from heuristic classification.
            result.Gate = GateVerdict.AllowOptional;
            result.GateReasonKey = "Profile.Reason.Gaming.Gate.Heuristic";
            return result;
        }

        if (decision.Verdict == GamingVerdict.OptionalRemoveCandidate)
        {
            result.Gate = GateVerdict.AllowOptional;
            result.GateReasonKey = "Profile.Reason.Gaming.Gate.Optional";
            return result;
        }

        // Low risk + curated knowledge + auto verdict → automatic change.
        result.Gate = GateVerdict.AllowAuto;
        result.GateReasonKey = string.Empty;
        return result;
    }

    private static GamingEvaluationResult Block(GamingEvaluationResult result, string gateReasonKey)
    {
        result.Gate = GateVerdict.Block;
        result.GateReasonKey = gateReasonKey;
        return result;
    }
}
