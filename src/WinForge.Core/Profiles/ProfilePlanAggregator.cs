using System;
using System.Collections.Generic;
using System.Linq;
using WinForge.Core.Models;

namespace WinForge.Core.Profiles;

// =====================================================================
// Phase 15 Stage 15.3b — CANONICAL EXECUTABLE AGGREGATION (ADR-096 addendum)
//
// Real structural validation exposed OptionalFeature "duplicate change plans":
//   DedicatedGaming: 'Containers' (4 change entries)
//   Lightweight:     'HyperV' (9 change entries)
//   DedicatedMinimal:'MediaPlayer' (2 change entries), 'HyperV' (9)
//
// ROOT CAUSE (verified against the real 25H2 capture): the DEEP CATALOG maps
// MULTIPLE genuinely distinct Windows OptionalFeature names to ONE profile-facing
// family id (HyperV -> HyperV-Guest-KernelInt, HyperV-KernelInt-VirtualDevice,
// Microsoft-Hyper-V, Microsoft-Hyper-V-All, Microsoft-Hyper-V-Hypervisor,
// Microsoft-Hyper-V-Management-Clients, Microsoft-Hyper-V-Management-PowerShell,
// Microsoft-Hyper-V-Services, Microsoft-Hyper-V-Tools-All; Containers ->
// Containers, Containers-HNS, Containers-SDN, Containers-Server-For-Application-
// Guard; MediaPlayer -> Microsoft.ZuneMusic AppX + WindowsMediaPlayer feature).
// The real inventory has ZERO duplicate raw identities. The PlanValidator's
// duplicate-change check grouped by the SEMANTIC family id (LogicalId) and
// reported distinct real features as duplicates.
//
// THE FIX (this layer + ProfileExecutionItem.ExecutableIdentity):
//   - Semantic identity (family id) drives PROFILE INTENT (overrides, keeps,
//     gaming policy, preview, delta keys) — unchanged.
//   - EXECUTABLE identity (the actual DISM FeatureName / package / service
//     identity) drives the PLAN: distinct real features stay distinct
//     (HyperV x9 -> 9 executable operations), while multiple semantic
//     candidates resolving to the SAME executable operation collapse into
//     ONE operation with provenance retained.
//   - Aggregation runs BEFORE final plan validation (spec: the duplicate must
//     be resolved before validation — the validator is NOT weakened).
//
// CONFLICT PRECEDENCE (documented, deterministic, §5):
//   1. Keep wins over removal at the SEMANTIC level: if any item for a
//      LogicalId is kept, every change candidate for that LogicalId is dropped
//      (RequiredKeep / Protected / explicit user override / profile keep all
//      take precedence over removal — mirrors the existing Safety Gate).
//   2. Within one executable target, disposition precedence is
//      AutoApply > Recommend (an automatic intent is the deterministic superset).
//   3. Different requested executable STATES (Remove vs Disable vs Configure)
//      for the same target are NEVER silently merged — an explicit conflict
//      issue fails validation (fail-safe).
// =====================================================================

/// <summary>Diagnostic record of one canonical merge (N semantic candidates → 1 executable operation).</summary>
public sealed class ProfilePlanMergeGroup
{
    /// <summary>The executable canonical key of the merged operation (e.g. "OptionalFeature|Microsoft-Hyper-V-Services").</summary>
    public string CanonicalKey { get; init; } = string.Empty;

    /// <summary>How many semantic candidates merged into this operation (>= 2).</summary>
    public int SourceCount { get; init; }

    /// <summary>Semantic change keys of the merged candidates ("OpType|LogicalId|Disposition") — traceable to the delta report.</summary>
    public IReadOnlyList<string> SourceIds { get; init; } = new List<string>();

    /// <summary>Executable/source identities of the merged candidates (raw feature names / package ids / definition ids).</summary>
    public IReadOnlyList<string> SourceIdentities { get; init; } = new List<string>();
}

/// <summary>Result of aggregating a delta report's items into canonical executable items.</summary>
public sealed class ProfilePlanAggregateResult
{
    /// <summary>All items (non-change + merged change items) in deterministic order — what BuildPlan executes/validates.</summary>
    public IReadOnlyList<ProfileExecutionItem> Items { get; init; } = new List<ProfileExecutionItem>();

    /// <summary>Canonical merges performed (diagnostics; empty when no true duplicates existed).</summary>
    public IReadOnlyList<ProfilePlanMergeGroup> MergeGroups { get; init; } = new List<ProfilePlanMergeGroup>();

    /// <summary>Total source candidates absorbed into merges (sum of SourceCount-1 over groups) — count reconciliation.</summary>
    public int MergedDuplicateCount { get; init; }

    /// <summary>Change candidates dropped because a Keep intent won at the semantic level (keep-wins precedence).</summary>
    public int DroppedKeepWins { get; init; }

    /// <summary>Conflict issues — when non-empty the plan MUST fail validation (never a silent merge).</summary>
    public IReadOnlyList<string> Issues { get; init; } = new List<string>();

    public bool IsValid => Issues.Count == 0;
}

public static class ProfilePlanAggregator
{
    /// <summary>
    /// Aggregates a delta report's items by EXECUTABLE canonical identity,
    /// resolving same-target candidates into one operation (provenance retained)
    /// and applying the documented keep-wins / conflict precedence. Items that
    /// are not executable changes pass through unchanged.
    /// </summary>
    public static ProfilePlanAggregateResult Aggregate(IReadOnlyList<ProfileExecutionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return new ProfilePlanAggregateResult();
        }

        // ---- 1. Keep-wins at the semantic level ----
        // A Keep disposition for a LogicalId protects the WHOLE family: every
        // change candidate sharing that LogicalId is dropped (conservative, the
        // safe direction — required keep / protected / profile keep > removal).
        var keptSemantic = items
            .Where(i => i.Disposition == ProfileDisposition.Keep)
            .Select(i => i.LogicalId)
            .ToHashSet(StringComparer.Ordinal);

        var remaining = new List<ProfileExecutionItem>();
        var droppedKeepWins = 0;
        foreach (var item in items)
        {
            if (item.IsExecutableChange && keptSemantic.Contains(item.LogicalId))
            {
                droppedKeepWins++;
                continue;
            }

            remaining.Add(item);
        }

        // ---- 2. Group change candidates by executable canonical identity ----
        var nonChanges = remaining.Where(i => !i.IsExecutableChange).ToList();
        var changeGroups = remaining
            .Where(i => i.IsExecutableChange)
            .GroupBy(i => i.ExecutableCanonicalKey, StringComparer.Ordinal)
            .ToList();

        var issues = new List<string>();
        var merged = new List<ProfileExecutionItem>();
        var mergeGroups = new List<ProfilePlanMergeGroup>();
        var mergedDuplicates = 0;

        foreach (var group in changeGroups)
        {
            if (group.Count() == 1)
            {
                merged.Add(group.Single());
                continue;
            }

            var candidates = group.ToList();

            // Conflicting requested executable states are NEVER silently merged.
            var actions = candidates.Select(c => c.ActionKind).Distinct().ToList();
            if (actions.Count > 1)
            {
                issues.Add($"Conflicting executable intents for '{group.Key}': "
                    + string.Join(" vs ", actions.OrderBy(a => a.ToString(), StringComparer.Ordinal))
                    + ". Refusing to merge different requested states.");
                // Deterministic representative stays (validation fails via Issues);
                // the conflicting candidates are NOT folded in silently.
                merged.Add(candidates[0]);
                continue;
            }

            // Disposition precedence: AutoApply > Recommend (deterministic superset).
            var disposition = candidates.Any(c => c.Disposition == ProfileDisposition.AutoApply)
                ? ProfileDisposition.AutoApply
                : ProfileDisposition.Recommend;

            var sourceIds = candidates
                .Select(c => $"{c.OperationType}|{c.LogicalId}|{c.Disposition}")
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var sourceIdentities = candidates
                .SelectMany(c => c.SourceDefinitionIds)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var representative = candidates[0];
            merged.Add(new ProfileExecutionItem
            {
                LogicalId = representative.LogicalId,
                DisplayName = representative.DisplayName,
                OperationType = representative.OperationType,
                Disposition = disposition,
                ReasonKey = representative.ReasonKey,
                ProfileId = representative.ProfileId,
                IsPresent = representative.IsPresent,
                IsUserOverride = representative.IsUserOverride,
                WasProfileDriven = representative.WasProfileDriven,
                ExecutableIdentity = representative.ExecutableIdentity,
                ActionKind = representative.ActionKind,
                SourceDefinitionIds = sourceIdentities,
                MergedSourceCount = candidates.Count,
            });

            mergedDuplicates += candidates.Count - 1;
            mergeGroups.Add(new ProfilePlanMergeGroup
            {
                CanonicalKey = group.Key,
                SourceCount = candidates.Count,
                SourceIds = sourceIds,
                SourceIdentities = sourceIdentities,
            });
        }

        // Deterministic order: non-changes first, then merged change items in
        // first-occurrence order (original relative order preserved).
        var ordered = nonChanges.Concat(merged).ToList();

        return new ProfilePlanAggregateResult
        {
            Items = ordered,
            MergeGroups = mergeGroups,
            MergedDuplicateCount = mergedDuplicates,
            DroppedKeepWins = droppedKeepWins,
            Issues = issues,
        };
    }
}
