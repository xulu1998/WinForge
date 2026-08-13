using System;
using System.Collections.Generic;
using System.Linq;
using WinForge.Core.ComponentIntelligence;

namespace WinForge.Core.Profiles;

/// <summary>
/// Orchestrates the knowledge-driven gaming recommendation pipeline (ADR-088):
/// Inventory → Deep Knowledge → Profile Policy → Candidate Recommendation →
/// Safety Gate → Plan. Pure, deterministic, platform-agnostic.
///
/// <para>The <see cref="ProfileSafetyGate"/> is applied HERE, before anything
/// reaches the plan layer — a blocked candidate never becomes an action. The
/// engine-level <see cref="RecommendationEngine"/> additionally treats any
/// supplied <c>GamingDecision</c> as a profile intent, but only after this
/// service has already enforced the gate.</para>
/// </summary>
public sealed class GamingProfileEvaluationService
{
    private readonly IReadOnlyDictionary<GamingProfileKind, IGamingProfilePolicy> _policies;

    public GamingProfileEvaluationService()
        : this(new IGamingProfilePolicy[]
        {
            new GamingPcPolicy(),
            new DedicatedGamingPolicy(),
        })
    {
    }

    public GamingProfileEvaluationService(IEnumerable<IGamingProfilePolicy> policies)
    {
        _policies = (policies ?? throw new ArgumentNullException(nameof(policies)))
            .ToDictionary(p => p.Kind, p => p);
    }

    /// <summary>
    /// Single-item evaluation → POST-GATE decision the plan layer may consume.
    /// Returns null when the policy has no steer (item falls through to defaults)
    /// or the gate blocked the change (the item keeps its default behavior).
    /// </summary>
    public GamingPolicyDecision? EvaluateItem(GamingPolicyInput input, GamingProfileKind kind)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (kind == GamingProfileKind.Unknown || input.Knowledge is null || !input.IsPresent)
        {
            return null;
        }

        if (!_policies.TryGetValue(kind, out var policy))
        {
            return null;
        }

        var decision = policy.Evaluate(input);
        if (decision.Verdict == GamingVerdict.NoOpinion)
        {
            return null;
        }

        var result = ProfileSafetyGate.Evaluate(decision, input);
        return result.Gate switch
        {
            GateVerdict.AllowAuto when result.Verdict is GamingVerdict.AutoRemoveCandidate
                or GamingVerdict.OptionalRemoveCandidate
                => new GamingPolicyDecision
                {
                    Kind = kind,
                    Verdict = GamingVerdict.AutoRemoveCandidate,
                    ReasonKey = decision.ReasonKey,
                    KeptByExtra = decision.KeptByExtra,
                },
            GateVerdict.AllowOptional => new GamingPolicyDecision
            {
                Kind = kind,
                Verdict = GamingVerdict.OptionalRemoveCandidate,
                ReasonKey = decision.ReasonKey,
                KeptByExtra = decision.KeptByExtra,
            },
            _ => null, // Blocked or keep → the plan layer uses defaults (keep).
        };
    }

    /// <summary>
    /// Full evaluation over a present inventory → aggregated summary + results.
    /// Exact, deterministic, no estimation. Items without knowledge or without a
    /// policy steer are excluded from the counts (they fall through to defaults).
    /// </summary>
    public GamingProfileSummary Evaluate(
        IEnumerable<GamingPolicyInput> inputs,
        GamingProfileKind kind,
        IReadOnlySet<GamingExtra> extras)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(extras);

        if (kind == GamingProfileKind.Unknown || !_policies.TryGetValue(kind, out var policy))
        {
            return new GamingProfileSummary { Kind = kind };
        }

        var results = new List<GamingEvaluationResult>();
        foreach (var input in inputs)
        {
            if (input is null || !input.IsPresent || input.Knowledge is null)
            {
                continue;
            }

            var withExtras = new GamingPolicyInput
            {
                RawIdentity = input.RawIdentity,
                Source = input.Source,
                Knowledge = input.Knowledge,
                Extras = extras,
                IsPresent = true,
                SupportedForRemoval = input.SupportedForRemoval,
                HasUserOverride = input.HasUserOverride,
            };

            var decision = policy.Evaluate(withExtras);
            if (decision.Verdict == GamingVerdict.NoOpinion)
            {
                continue;
            }

            results.Add(ProfileSafetyGate.Evaluate(decision, withExtras));
        }

        results.Sort(static (a, b) =>
        {
            var bySource = a.Source.CompareTo(b.Source);
            return bySource != 0
                ? bySource
                : string.CompareOrdinal(a.RawIdentity, b.RawIdentity);
        });

        var items = results
            .Select(r => new GamingEvaluationItem
            {
                Result = r,
                DisplayName = DisplayNameOf(r),
            })
            .ToList();

        return new GamingProfileSummary
        {
            Kind = kind,
            RecommendedChanges = items.Count(i =>
                i.Result.IsAutoRecommended && !i.Result.HasUserOverride),
            OptionalChoices = items.Count(i =>
                i.Result.IsOptionalSuggestion && !i.Result.HasUserOverride),
            KeptForCompatibility = items.Count(i => i.Result.IsKeptForCompatibility),
            Items = items,
        };
    }

    private static string DisplayNameOf(GamingEvaluationResult r)
    {
        var name = string.IsNullOrWhiteSpace(r.CanonicalId) ? r.RawIdentity : r.CanonicalId!;
        return name;
    }
}
