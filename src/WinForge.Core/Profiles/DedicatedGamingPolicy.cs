using System.Collections.Generic;
using WinForge.Core.ComponentIntelligence;

namespace WinForge.Core.Profiles;

/// <summary>
/// DEDICATED GAMING policy (ADR-089): a more minimal gaming-only machine. It is
/// NOT a kiosk mode and NOT "remove everything not needed for gaming": it keeps
/// the full Gaming PC keep list (servicing, Windows Update, Defender, Store /
/// winget, Gaming Services, Xbox ecosystem when enabled, runtimes, DirectX,
/// audio, networking, GPU/display, input, USB, storage, shell/login, WinRE,
/// boot) and only adds OPTIONAL suggestions — every change is user-confirmed.
///
/// Difference vs <see cref="GamingPcPolicy"/>: the OPTIONAL set is wider —
/// moderate-risk consumer/media families become optional suggestions too
/// (Gaming PC leaves those at their default "review" state). Nothing is forced,
/// nothing is automatic beyond the same Low-risk consumer set.
/// </summary>
public sealed class DedicatedGamingPolicy : GamingPcPolicy
{
    public override GamingProfileKind Kind => GamingProfileKind.DedicatedGaming;

    /// <summary>
    /// Dedicated Gaming's additional OPTIONAL suggestions (never automatic):
    /// moderate-risk consumer content, phone integration, and media playback
    /// apps. The Safety Gate still downgrades everything to user-confirmed.
    /// </summary>
    protected override GamingPolicyDecision? AdditionalOptional(DeepComponentKnowledge k)
    {
        if (k.Risk != ComponentRiskLevel.Moderate)
        {
            return null;
        }

        if (k.ProfileTag is ComponentProfileTag.ConsumerContent or ComponentProfileTag.PhoneIntegration)
        {
            return new GamingPolicyDecision
            {
                Kind = Kind,
                Verdict = GamingVerdict.OptionalRemoveCandidate,
                ReasonKey = k.ProfileTag == ComponentProfileTag.PhoneIntegration
                    ? "Profile.Reason.Gaming.Remove.Phone"
                    : "Profile.Reason.Gaming.Remove.Consumer",
            };
        }

        if (k.Function == ComponentFunctionCategory.Media)
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
