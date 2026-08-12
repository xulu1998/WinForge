using System;
using System.Collections.Generic;
using System.Linq;
using WinForge.Core.Models;

namespace WinForge.Core.Profiles;

/// <summary>
/// Computes the effective recommendation for one item from: component knowledge +
/// scenario rules + actual image contents + compatibility + risk + dependency
/// constraints. Deterministic, pure, platform-agnostic. It NEVER mutates the
/// definition's default recommendation — the effective result is computed
/// separately (Part E).
/// </summary>
public interface IRecommendationEngine
{
    EffectiveRecommendation Evaluate(RecommendationInput input, RecommendationContext context);
}

/// <inheritdoc cref="IRecommendationEngine"/>
public sealed class RecommendationEngine : IRecommendationEngine
{
    public EffectiveRecommendation Evaluate(RecommendationInput input, RecommendationContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        // Absent items are never offered (Part O) — no profile rule can bring an
        // absent component into the recommendation surface or the counts.
        if (!input.IsPresent)
        {
            return EffectiveRecommendation.FromDefault(
                input.DefaultRecommendation, isPresent: false, input.IsApplySupported);
        }

        // Tier 1 — Critical safety constraint (wins over everything, even overrides).
        if (IsSafetyBlocked(input))
        {
            return new EffectiveRecommendation
            {
                Level = EffectiveRecommendationLevel.Blocked,
                IsPresent = true,
                IsApplySupported = input.IsApplySupported,
                WasOverridden = context.UserOverrides.Contains(input.LogicalId),
                ReasonKeys = new[] { "Profile.Reason.Safety" },
                SourceRuleIds = new[] { "safety:block" },
            };
        }

        // Tier 2 — explicit user override (Part K): the user's own choice survives
        // recalculation; the badge keeps showing the profile-driven level for
        // information, but adoption/reapply never touches overridden items.
        var wasOverridden = context.UserOverrides.Contains(input.LogicalId);

        // Requirement-kept ids: every selected profile's RequiredCapabilities that
        // are actually present on the image (tier 4 gate).
        var requirementKept = context.SelectedProfiles
            .SelectMany(p => p.RequiredCapabilities)
            .Where(context.PresentIds.Contains)
            .ToHashSet(StringComparer.Ordinal);

        var hasRequirement = requirementKept.Contains(input.LogicalId);
        var dependencyKeptBy = DependencyKeepSource(input, context, requirementKept);

        // Tier 5 intent collections from the selected profiles.
        var overrides = context.SelectedProfiles
            .SelectMany(p => p.RecommendationOverrides
                .Where(o => string.Equals(o.TargetId, input.LogicalId, StringComparison.Ordinal))
                .Select(o => (Profile: p, o.Intent, o.ReasonKey, o.Tier)))
            .ToList();
        var keepIntents = overrides.Where(x => x.Intent == ProfileIntent.Keep).ToList();
        var trimIntents = overrides.Where(x => x.Intent == ProfileIntent.Trim).ToList();
        var preferredKeep = context.SelectedProfiles
            .Where(p => p.PreferredCapabilities.Contains(input.LogicalId))
            .ToList();
        var avoidedTrim = context.SelectedProfiles
            .Where(p => p.AvoidedComponents.Contains(input.LogicalId))
            .ToList();

        var hasTrimIntent = trimIntents.Count > 0 || avoidedTrim.Count > 0;
        var hasKeepIntent = keepIntents.Count > 0 || preferredKeep.Count > 0;

        // ---- Determine the winning level by documented precedence ----
        var reasons = new List<string>();
        var sourceRules = new List<string>();
        var conflicts = new List<RecommendationConflict>();

        EffectiveRecommendationLevel level;
        if (dependencyKeptBy is not null)
        {
            // Tier 3 — required dependency.
            level = EffectiveRecommendationLevel.RecommendKeep;
            reasons.Add("Profile.Reason.Dependency");
            sourceRules.Add($"dependency:{input.LogicalId}");
        }
        else if (hasRequirement)
        {
            // Tier 4 — profile requirement.
            level = EffectiveRecommendationLevel.RecommendKeep;
            reasons.Add("Profile.Reason.Requirement");
            sourceRules.Add("requirement:" + input.LogicalId);
        }
        else if (hasKeepIntent || hasTrimIntent)
        {
            // Tier 5 — scenario recommendation override. When one selected profile
            // keeps and another trims the same item, KEEP wins (Part G); the
            // conflict itself is recorded below.
            level = hasKeepIntent
                ? EffectiveRecommendationLevel.RecommendKeep
                : EffectiveRecommendationMappings.TrimForAction(input.Action);
            reasons.Add(keepIntentReason(keepIntents, preferredKeep, trimIntents, avoidedTrim));
            sourceRules.Add(hasKeepIntent
                ? $"override:keep:{input.LogicalId}"
                : $"override:trim:{input.LogicalId}");
        }
        else
        {
            // Tier 6 — component default recommendation.
            level = EffectiveRecommendationMappings.FromDefault(input.DefaultRecommendation);
            sourceRules.Add("default:" + input.LogicalId);
        }

        // ---- Visible conflict resolution (Part G) ----
        // KEEP wins when a selected scenario requires/keeps the component; the
        // resolution is recorded, never silent. Covers tier-3/4 keep + trim and
        // tier-5 keep-vs-trim (level is already RecommendKeep in both cases).
        if (hasTrimIntent && level == EffectiveRecommendationLevel.RecommendKeep)
        {
            var keepProfile = dependencyKeptBy ?? FirstRequiringProfile(context, input.LogicalId)
                ?? keepIntents.FirstOrDefault().Profile ?? preferredKeep.FirstOrDefault();
            var trimProfile = trimIntents.FirstOrDefault().Profile ?? avoidedTrim.FirstOrDefault();
            if (keepProfile is not null && trimProfile is not null)
            {
                conflicts.Add(new RecommendationConflict
                {
                    TargetId = input.LogicalId,
                    KeepProfileId = keepProfile.Id,
                    TrimProfileId = trimProfile.Id,
                    ReasonKey = "Profile.Reason.Conflict.KeepWins",
                });
                reasons.Add("Profile.Reason.Conflict.KeepWins");
                sourceRules.Add($"conflict:{input.LogicalId}");
            }
        }

        // Tier 2 reason rides on top for display.
        if (wasOverridden)
        {
            reasons.Insert(0, "Profile.Reason.UserOverride");
            sourceRules.Insert(0, "user:" + input.LogicalId);
        }

        var wasProfileDriven = sourceRules.Any(s =>
            s.StartsWith("override:", StringComparison.Ordinal) ||
            s.StartsWith("requirement:", StringComparison.Ordinal) ||
            s.StartsWith("dependency:", StringComparison.Ordinal) ||
            s.StartsWith("conflict:", StringComparison.Ordinal));

        return new EffectiveRecommendation
        {
            Level = level,
            IsPresent = true,
            IsApplySupported = input.IsApplySupported,
            Risk = input.Risk,
            WasOverridden = wasOverridden,
            WasProfileDriven = wasProfileDriven,
            HasConflict = conflicts.Count > 0,
            ReasonKeys = reasons,
            SourceRuleIds = sourceRules,
            Conflicts = conflicts,
        };
    }

    private static bool IsSafetyBlocked(RecommendationInput input)
        => input.Risk == RiskLevel.Critical
           || input.Removal == RemovalSupport.Blocked
           || input.DefaultRecommendation == RecommendationLevel.NeverRemove;

    /// <summary>Deterministic reason key for a tier-5 keep/trim intent.</summary>
    private static string keepIntentReason(
        IReadOnlyList<(ProfileDefinition Profile, ProfileIntent Intent, string ReasonKey, int Tier)> keepIntents,
        IReadOnlyList<ProfileDefinition> preferredKeep,
        IReadOnlyList<(ProfileDefinition Profile, ProfileIntent Intent, string ReasonKey, int Tier)> trimIntents,
        IReadOnlyList<ProfileDefinition> avoidedTrim)
        => keepIntents.Count > 0
            ? keepIntents[0].ReasonKey
            : preferredKeep.Count > 0
                ? "Profile.Reason.Keep"
                : trimIntents.Count > 0
                    ? trimIntents[0].ReasonKey
                    : avoidedTrim.Count > 0
                        ? "Profile.Reason.Trim"
                        : "Profile.Reason.Keep";

    /// <summary>
    /// Tier 3 dependency keep: the item is required (Requires / RecommendsKeeping)
    /// by another logical id that a selected profile requires and that is present.
    /// Returns the profile that drove the keep (for conflict attribution), else null.
    /// </summary>
    private static ProfileDefinition? DependencyKeepSource(
        RecommendationInput input,
        RecommendationContext context,
        IReadOnlySet<string> requirementKept)
    {
        var targets = input.Dependencies
            .Where(d => d.Relation is DependencyRelation.Requires or DependencyRelation.RecommendsKeeping)
            .Select(d => d.ToId)
            .Where(requirementKept.Contains)
            .ToHashSet(StringComparer.Ordinal);
        if (targets.Count == 0)
        {
            return null;
        }

        return context.SelectedProfiles
            .FirstOrDefault(p => p.RequiredCapabilities.Any(targets.Contains));
    }

    private static ProfileDefinition? FirstRequiringProfile(RecommendationContext context, string logicalId)
        => context.SelectedProfiles.FirstOrDefault(p => p.RequiredCapabilities.Contains(logicalId));
}
