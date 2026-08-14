using System;
using System.Collections.Generic;
using WinForge.Core.ComponentIntelligence;

namespace WinForge.Core.Profiles;

/// <summary>
/// Deterministic knowledge-driven gaming policy contract (ADR-088). Consumes
/// <see cref="GamingPolicyInput.Knowledge"/> (deep classification: function, risk,
/// recommendation, protection, profile tag, dependency tags) plus the selected
/// <see cref="GamingExtra"/> set. Never re-parses raw identity strings for its
/// decisions. Returns a PRE-GATE verdict — the <see cref="ProfileSafetyGate"/>
/// has final authority.
/// </summary>
public interface IGamingProfilePolicy
{
    GamingProfileKind Kind { get; }

    GamingPolicyDecision Evaluate(GamingPolicyInput input);
}

/// <summary>
/// GAMING PC policy (ADR-089): a normal personal Windows PC optimized for gaming
/// while remaining convenient. Automatic changes are strictly LOW-RISK consumer
/// content (Phone Link, Solitaire, Get Help, Feedback Hub, Tips/suggestions,
/// weather, Spotlight consumer content, advertising/tailored experiences,
/// widgets/news, Bing/web search integration where supported). Everything else
/// is either kept (infrastructure, §8) or optional ("never assume": Paint,
/// Photos, OneDrive, printing, Remote Desktop, developer tools, Hyper-V, WSL).
/// NEVER: placebo tweaks, Defender/Windows Update disabling, servicing-stack
/// removal (Part C §12).
/// </summary>
public class GamingPcPolicy : IGamingProfilePolicy
{
    public virtual GamingProfileKind Kind => GamingProfileKind.GamingPc;

    // §8 keep list — expressed as SEMANTIC function families (knowledge fields),
    // not raw identities: servicing/update/security/installer/Store ecosystem/
    // Gaming Services/Xbox/runtimes/DirectX/audio/networking/GPU/USB/storage/
    // shell/login/WinRE/boot. Protection and RequiredKeep/RecommendedKeep also
    // keep (Defender, servicing stack, codecs, Xbox app, WebView2, VC/.NET, …).
    private static readonly HashSet<ComponentFunctionCategory> InfrastructureFunctions = new()
    {
        ComponentFunctionCategory.Servicing,
        ComponentFunctionCategory.Security,
        ComponentFunctionCategory.SystemCore,
        ComponentFunctionCategory.RuntimeDependency,
        ComponentFunctionCategory.Recovery,
        ComponentFunctionCategory.StoreInfrastructure,
        ComponentFunctionCategory.Gaming,
        ComponentFunctionCategory.HardwareSupport,
        ComponentFunctionCategory.Networking,
        ComponentFunctionCategory.Input,
        // Stage 14.3b (ADR-091): language capabilities stay KEPT by Gaming
        // profiles — they affect display/input/OCR/speech/handwriting/fonts/TTS
        // and users may intentionally need several. Never mass-remove "foreign"
        // languages merely because they are Gaming-irrelevant.
        ComponentFunctionCategory.Language,
    };

    // §7 potential automatic LOW-RISK consumer removals.
    private static readonly HashSet<ComponentProfileTag> AutoRemoveTags = new()
    {
        ComponentProfileTag.ConsumerContent,
        ComponentProfileTag.PhoneIntegration,
    };

    // §7 "Optional, NEVER assume" — suggestions only, never automatic.
    private static readonly HashSet<ComponentProfileTag> OptionalTags = new()
    {
        ComponentProfileTag.CloudStorage,
        ComponentProfileTag.PrintScan,
        ComponentProfileTag.RemoteAccess,
        ComponentProfileTag.DeveloperTool,
        ComponentProfileTag.Virtualization,
    };

    public GamingPolicyDecision Evaluate(GamingPolicyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var k = input.Knowledge;

        // §9 extras first — an enabled extra REQUIRES its ecosystem to be kept.
        foreach (var extra in input.Extras)
        {
            if (ExtraRequiresKeep(extra, k))
            {
                return Keep("Profile.Reason.Gaming.Keep.Extra." + extra, extra);
            }
        }

        // §7 automatic LOW-RISK consumer removals (curated knowledge supports them).
        // These run BEFORE the infra keep so explicitly-consumer families (Phone
        // Link, Solitaire, Weather, Get Help, Feedback Hub, news/widgets, …) are
        // actually trimmed rather than hidden behind a broad function family.
        if (AutoRemoveTags.Contains(k.ProfileTag) && k.Risk == ComponentRiskLevel.Low
            && k.Recommendation is ComponentRecommendationKind.RecommendedRemove
                or ComponentRecommendationKind.OptionalRemove
                or ComponentRecommendationKind.ProfileDependent)
        {
            return new GamingPolicyDecision
            {
                Kind = Kind,
                Verdict = GamingVerdict.AutoRemoveCandidate,
                ReasonKey = k.ProfileTag == ComponentProfileTag.PhoneIntegration
                    ? "Profile.Reason.Gaming.Remove.Phone"
                    : "Profile.Reason.Gaming.Remove.Consumer",
            };
        }

        // Web / search integration (function-level, Low only).
        if (k.Function == ComponentFunctionCategory.Search && k.Risk == ComponentRiskLevel.Low
            && k.Recommendation is ComponentRecommendationKind.RecommendedRemove
                or ComponentRecommendationKind.OptionalRemove
                or ComponentRecommendationKind.ProfileDependent)
        {
            return new GamingPolicyDecision
            {
                Kind = Kind,
                Verdict = GamingVerdict.AutoRemoveCandidate,
                ReasonKey = "Profile.Reason.Gaming.Remove.Search",
            };
        }

        // §8 keep list + protection + recommendation semantics.
        if (k.Protection == ComponentProtectionLevel.Protected)
        {
            return Keep("Profile.Reason.Gaming.Keep.Protection", null);
        }

        if (k.Recommendation is ComponentRecommendationKind.RequiredKeep
            or ComponentRecommendationKind.RecommendedKeep)
        {
            return Keep("Profile.Reason.Gaming.Keep.Runtime", null);
        }

        if (InfrastructureFunctions.Contains(k.Function))
        {
            return Keep("Profile.Reason.Gaming.Keep.Infrastructure", null);
        }

        // §10 dependency preservation: a dependency on kept infrastructure keeps this item.
        if (HasKeptDependency(k))
        {
            return Keep("Profile.Reason.Gaming.Keep.Dependency", null);
        }

        // §11 "Moderate: generally optional/user-dependent" — moderate consumer
        // content becomes an OPTIONAL suggestion (never automatic).
        if (AutoRemoveTags.Contains(k.ProfileTag) && k.Risk == ComponentRiskLevel.Moderate
            && k.Recommendation is ComponentRecommendationKind.RecommendedRemove
                or ComponentRecommendationKind.OptionalRemove
                or ComponentRecommendationKind.ProfileDependent)
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

        // §7 optional "never assume" set.
        if (OptionalTags.Contains(k.ProfileTag)
            && k.Risk is ComponentRiskLevel.Low or ComponentRiskLevel.Moderate)
        {
            return Optional(k.ProfileTag);
        }

        // Media playback (Photos/Paint-style apps) — optional, never automatic.
        if (k.Function == ComponentFunctionCategory.Media && k.Risk == ComponentRiskLevel.Low
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

        // Dedicated Gaming only — additional optional suggestions.
        return AdditionalOptional(k) ?? new GamingPolicyDecision { Kind = Kind, Verdict = GamingVerdict.NoOpinion };
    }

    /// <summary>Extra optional suggestions for more minimal profiles (default: none).</summary>
    protected virtual GamingPolicyDecision? AdditionalOptional(DeepComponentKnowledge k) => null;

    protected virtual GamingPolicyDecision Optional(ComponentProfileTag tag) => new()
    {
        Kind = Kind,
        Verdict = GamingVerdict.OptionalRemoveCandidate,
        ReasonKey = "Profile.Reason.Gaming.Optional." + OptionalReasonSuffix(tag),
    };

    private static string OptionalReasonSuffix(ComponentProfileTag tag) => tag switch
    {
        ComponentProfileTag.DeveloperTool => "Developer",
        _ => tag.ToString(),
    };

    private GamingPolicyDecision Keep(string reasonKey, GamingExtra? extra) => new()
    {
        Kind = Kind,
        Verdict = GamingVerdict.KeepForCompatibility,
        ReasonKey = reasonKey,
        KeptByExtra = extra,
    };

    private static bool ExtraRequiresKeep(GamingExtra extra, DeepComponentKnowledge k) => extra switch
    {
        GamingExtra.XboxGamePass => k.Function == ComponentFunctionCategory.Gaming,
        GamingExtra.WslDocker => k.Function == ComponentFunctionCategory.Virtualization
            || k.ProfileTag == ComponentProfileTag.Virtualization,
        GamingExtra.PrintScan => k.Function == ComponentFunctionCategory.PrintingScanning
            || k.ProfileTag == ComponentProfileTag.PrintScan,
        GamingExtra.TouchPen => k.Function == ComponentFunctionCategory.Input
            || k.ProfileTag == ComponentProfileTag.AccessibilityTool,
        GamingExtra.RemoteDesktop => k.Function == ComponentFunctionCategory.RemoteAccess
            || k.ProfileTag == ComponentProfileTag.RemoteAccess,
        _ => false,
    };

    private static bool HasKeptDependency(DeepComponentKnowledge k)
    {
        if (k.DependencyTags is null || k.DependencyTags.Count == 0)
        {
            return false;
        }

        foreach (var tag in k.DependencyTags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            // A dependency on the Store ecosystem / package management (winget) /
            // gaming services must be preserved for gaming compatibility.
            if (tag.Contains("WindowsStore", StringComparison.OrdinalIgnoreCase) ||
                tag.Contains("DesktopAppInstaller", StringComparison.OrdinalIgnoreCase) ||
                tag.Contains("GamingServices", StringComparison.OrdinalIgnoreCase) ||
                tag.Contains("Xbox", StringComparison.OrdinalIgnoreCase) ||
                tag.Contains("WebView2", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
