using System;
using System.Collections.Generic;
using System.Linq;
using WinForge.Core.Models;

namespace WinForge.Core.Profiles;

// =====================================================================
// Phase 15 Stage 15.1 — PROFILE PLAN VALIDATOR (ADR-094 §12)
//
// Validation over GENERATED profile plans. Detects:
//   - remove + keep conflicts on the same logical id
//   - duplicate operations (same canonical target)
//   - remove + dependency-required conflicts
//   - unsupported execution   - protected execution attempts
//   - registry-target duplicates / feature duplicates / AppX dependency contradictions
// Reuses the Phase 12 canonical operation identity + CustomizationPlan
// duplicate/conflict detection wherever applicable. Profile generation FAILS
// SAFE: any issue keeps the plan from becoming executable.
// =====================================================================

public sealed class ProfilePlanValidationResult
{
    public bool IsValid => Issues.Count == 0;
    public IReadOnlyList<string> Issues { get; init; } = new List<string>();
}

public static class ProfilePlanValidator
{
    /// <summary>
    /// Validates the item-level decisions (remove+keep, dependency-required
    /// removal, protected/unsupported change attempts) and — when a plan is
    /// supplied — the operation-level plan (duplicates/conflicts via
    /// <see cref="CustomizationPlan.RecomputeValidation"/>).
    /// </summary>
    public static ProfilePlanValidationResult Validate(
        IReadOnlyList<ProfileExecutionItem> items,
        CustomizationPlan? plan = null)
    {
        var issues = new List<string>();

        // ---- 1. remove + keep conflicts / duplicate change plans on the same logical id ----
        var dispositions = new Dictionary<string, List<ProfileDisposition>>(StringComparer.Ordinal);
        foreach (var item in items ?? Array.Empty<ProfileExecutionItem>())
        {
            if (!dispositions.TryGetValue(item.LogicalId, out var list))
            {
                list = new List<ProfileDisposition>();
                dispositions[item.LogicalId] = list;
            }

            list.Add(item.Disposition);
        }

        foreach (var pair in dispositions)
        {
            var hasKeep = pair.Value.Contains(ProfileDisposition.Keep);
            var hasChange = pair.Value.Any(d => d is ProfileDisposition.AutoApply or ProfileDisposition.Recommend);
            if (hasKeep && hasChange)
            {
                issues.Add($"Remove/keep conflict for '{pair.Key}': planned as a change but kept elsewhere.");
            }

            var changeCount = pair.Value.Count(d => d is ProfileDisposition.AutoApply or ProfileDisposition.Recommend);
            if (changeCount > 1)
            {
                issues.Add($"Duplicate change plan for '{pair.Key}' ({changeCount} change entries).");
            }
        }

        // ---- 2. protected execution attempt + unsupported execution ----
        foreach (var item in items ?? Array.Empty<ProfileExecutionItem>())
        {
            if (item.IsExecutableChange && item.Disposition == ProfileDisposition.Blocked)
            {
                issues.Add($"Blocked change attempted for '{item.LogicalId}' ({item.ReasonKey}).");
            }
        }

        // ---- 3. operation-level validation (reuse Phase 12 infrastructure) ----
        if (plan is not null && plan.Operations.Count > 0)
        {
            issues.AddRange(plan.RecomputeValidation());
        }

        return new ProfilePlanValidationResult { Issues = issues.Distinct(StringComparer.Ordinal).ToList() };
    }

    /// <summary>
    /// Dependency-required removal check: an item that a KEPT item requires must
    /// not be removed. Caller supplies the dependency map (logicalId -> required
    /// ids) for the kept set; returns issue strings (empty when clean).
    /// </summary>
    public static IReadOnlyList<string> ValidateDependencyKeep(
        IReadOnlySet<string> keptIds,
        IReadOnlyList<ProfilePlanSubject> subjects,
        IReadOnlySet<string> changeIds)
    {
        var issues = new List<string>();
        var requires = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in subjects)
        {
            if (s.Dependencies is null)
            {
                continue;
            }

            foreach (var d in s.Dependencies)
            {
                if (d.Relation is DependencyRelation.Requires or DependencyRelation.RecommendsKeeping)
                {
                    requires[d.ToId] = s.LogicalId;
                }
            }
        }

        foreach (var kept in keptIds)
        {
            if (requires.TryGetValue(kept, out var dependent)
                && changeIds.Contains(dependent))
            {
                issues.Add($"'{dependent}' is planned as a change but '{kept}' (kept) requires it.");
            }
        }

        return issues;
    }
}
