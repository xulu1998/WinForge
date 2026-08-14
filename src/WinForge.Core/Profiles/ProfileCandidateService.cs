using System;
using System.Collections.Generic;
using System.Linq;
using WinForge.Core.ComponentIntelligence;
using WinForge.Core.Models;

namespace WinForge.Core.Profiles;

// =====================================================================
// Phase 15 Stage 15.2 — UNIFIED PROFILE CANDIDATE STREAM (ADR-095)
//
// Stage 15.1 fed ONLY deep-inventory subjects to the planner. The real 25H2
// capture exposed the consequences:
//   - 83 of 757 inventory objects never entered profile-plan accounting
//     (79 Unknown + 4 curated-but-not-deep AppX);
//   - the registry/privacy/personalization/service optimization definitions
//     that profile overrides target (Office's trims, Balanced's privacy/UI
//     trims, …) were missing entirely — leaving Office with 0 changes and
//     Balanced/Developer far weaker than their documented intent.
//
// This service builds ONE canonical candidate stream:
//
//     inventory objects (deep → curated → explicit exclusion bucket)
//     + non-inventory optimization definitions
//
// deduplicated by canonical Phase 12-style operation identity, with exact
// accounting over ALL inventory objects — the invariant
//   TotalInventory = Evaluated + CuratedOutsideDeep + ExcludedUnknown
//                  + ExcludedUnsupported + ExcludedDuplicate
//                  + ExcludedNotApplicable + ExcludedOther
// always holds. No unexplained loss, no double counting.
// =====================================================================

/// <summary>
/// One real inventory object fed into the profile planner, with its knowledge
/// already resolved (deep classification and/or curated definition).
/// </summary>
public sealed class ProfileInventoryInput
{
    public string RawIdentity { get; init; } = string.Empty;
    public ComponentCategory Category { get; init; } = ComponentCategory.Unknown;
    public DeepComponentKnowledge? Deep { get; init; }
    public ComponentDefinition? Curated { get; init; }
}

/// <summary>
/// Exact accounting over ALL inventory objects. Every object lands in exactly
/// ONE bucket — <see cref="IsBalanced"/> must always hold.
/// </summary>
public sealed class ProfileInventoryAccounting
{
    /// <summary>Real inventory objects that entered the profile stream.</summary>
    public int TotalInventory { get; init; }

    /// <summary>Deep-classified objects → evaluated as deep-knowledge subjects.</summary>
    public int EvaluatedForProfile { get; init; }

    /// <summary>Curated-only objects (no deep entry) → still evaluated with curated knowledge.</summary>
    public int CuratedOutsideDeepInventory { get; init; }

    /// <summary>No deep AND no curated knowledge — visible, honest unknown debt (ADR-093).</summary>
    public int ExcludedUnknownKnowledge { get; init; }

    /// <summary>Provider NotSupported by the pipeline (Service/Driver/…, ADR-093 scope).</summary>
    public int ExcludedUnsupportedSource { get; init; }

    /// <summary>Dropped as a duplicate of an already-evaluated canonical operation.</summary>
    public int ExcludedFilteredDuplicate { get; init; }

    /// <summary>Present in the stream but explicitly not applicable.</summary>
    public int ExcludedNotApplicable { get; init; }

    /// <summary>Any other explicit exclusion (reserved; always 0 today).</summary>
    public int ExcludedOther { get; init; }

    /// <summary>Evaluated objects (deep + curated) per component category — InventoryBySource.</summary>
    public IReadOnlyDictionary<ComponentCategory, int> BySource { get; init; } = new Dictionary<ComponentCategory, int>();

    public int Evaluated => EvaluatedForProfile + CuratedOutsideDeepInventory;

    public int Accounted => EvaluatedForProfile + CuratedOutsideDeepInventory + ExcludedUnknownKnowledge
        + ExcludedUnsupportedSource + ExcludedFilteredDuplicate + ExcludedNotApplicable + ExcludedOther;

    /// <summary>True when every inventory object is in exactly one bucket (757 = evaluated + exclusions).</summary>
    public bool IsBalanced => Accounted == TotalInventory;
}

/// <summary>Result of building the unified candidate stream.</summary>
public sealed class ProfileCandidateBuildResult
{
    public IReadOnlyList<ProfilePlanSubject> Subjects { get; init; } = new List<ProfilePlanSubject>();
    public ProfileInventoryAccounting Accounting { get; init; } = new();

    /// <summary>Non-inventory optimization definitions added to the stream (deduped).</summary>
    public int OptimizationCandidates { get; init; }

    /// <summary>Optimization definitions dropped as duplicates of a component subject or each other.</summary>
    public int OptimizationDuplicates { get; init; }
}

public static class ProfileCandidateService
{
    // Providers NotSupported by RealCapture / the production pipeline (ADR-093
    // provider scope). Their objects are never profile candidates.
    private static readonly HashSet<ComponentCategory> UnsupportedSources = new()
    {
        ComponentCategory.Service,
        ComponentCategory.ScheduledTask,
        ComponentCategory.Driver,
        ComponentCategory.Language,
        ComponentCategory.WinRecovery,
        ComponentCategory.SystemApp,
    };

    /// <summary>
    /// Builds the unified candidate stream (ADR-095 §5):
    /// 1. Inventory objects → deep subject, else curated subject, else an explicit
    ///    exclusion bucket (Unknown / UnsupportedSource). Deep wins over curated
    ///    for the SAME object — one subject per object, no double counting.
    /// 2. Non-inventory optimization definitions → subjects, deduplicated against
    ///    the component layer AND each other by canonical operation identity
    ///    (Service:name / OptionalFeature:name / Registry:hive:path:value / AppX:id).
    /// </summary>
    public static ProfileCandidateBuildResult BuildCandidates(
        IReadOnlyList<ProfileInventoryInput> inventory,
        IReadOnlyList<OptimizationDefinition> optimizations)
    {
        var inventoryInputs = inventory ?? Array.Empty<ProfileInventoryInput>();
        var optimizationDefs = optimizations ?? Array.Empty<OptimizationDefinition>();

        var subjects = new List<ProfilePlanSubject>();
        var bySource = new Dictionary<ComponentCategory, int>();
        var evaluatedDeep = 0;
        var curatedOutside = 0;
        var excludedUnknown = 0;
        var unsupported = 0;

        foreach (var item in inventoryInputs)
        {
            if (UnsupportedSources.Contains(item.Category))
            {
                unsupported++;
                continue;
            }

            if (item.Deep is not null)
            {
                subjects.Add(ProfilePlanSubject.FromKnowledge(item.RawIdentity, item.Category, item.Deep));
                evaluatedDeep++;
                Increment(bySource, item.Category);
                continue;
            }

            if (item.Curated is not null)
            {
                subjects.Add(ProfilePlanSubject.FromCurated(item.Curated, item.Category));
                curatedOutside++;
                Increment(bySource, item.Category);
                continue;
            }

            excludedUnknown++;
        }

        // ---- Non-inventory optimization definitions, canonical dedup ----
        var componentKeys = subjects
            .Select(s => CanonicalKey(s.OperationType, s.LogicalId))
            .ToHashSet(StringComparer.Ordinal);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var optimizationSubjects = new List<ProfilePlanSubject>();
        var optimizationDup = 0;

        foreach (var def in optimizationDefs)
        {
            var key = OptimizationCanonicalKey(def);
            if (componentKeys.Contains(key) || !seenKeys.Add(key))
            {
                optimizationDup++; // same canonical operation already in the stream
                continue;
            }

            optimizationSubjects.Add(ProfilePlanSubject.FromOptimization(def));
        }

        subjects.AddRange(optimizationSubjects);

        return new ProfileCandidateBuildResult
        {
            Subjects = subjects,
            Accounting = new ProfileInventoryAccounting
            {
                TotalInventory = inventoryInputs.Count,
                EvaluatedForProfile = evaluatedDeep,
                CuratedOutsideDeepInventory = curatedOutside,
                ExcludedUnknownKnowledge = excludedUnknown,
                ExcludedUnsupportedSource = unsupported,
                ExcludedFilteredDuplicate = 0,
                ExcludedNotApplicable = 0,
                ExcludedOther = 0,
                BySource = bySource,
            },
            OptimizationCandidates = optimizationSubjects.Count,
            OptimizationDuplicates = optimizationDup,
        };
    }

    /// <summary>
    /// Canonical Phase 12-style operation identity for a component subject
    /// (the deep canonical id / curated definition id is already canonical).
    /// </summary>
    public static string CanonicalKey(ExecutionOperationType operationType, string logicalId)
        => $"{operationType}:{logicalId}";

    /// <summary>
    /// Canonical operation identity for an optimization definition — the actual
    /// technical target (service name / feature name / registry value), NOT the
    /// catalog id, so component-vs-optimization dedup is real.
    /// </summary>
    public static string OptimizationCanonicalKey(OptimizationDefinition d)
    {
        if (d.Mechanism == OptimizationMechanism.ServiceStartup && !string.IsNullOrWhiteSpace(d.ServiceName))
        {
            return "Service:" + d.ServiceName;
        }

        if (d.Tab == OptimizationTab.WindowsComponents && !string.IsNullOrWhiteSpace(d.TargetIdentifier))
        {
            return "OptionalFeature:" + d.TargetIdentifier;
        }

        if (d.Tab == OptimizationTab.Apps)
        {
            return "AppX:" + d.Id;
        }

        if (d.RegistryTargets.Count > 0)
        {
            var t = d.RegistryTargets[0];
            return $"Registry:{t.Hive}:{t.KeyPath}:{t.ValueName}";
        }

        return d.Tab + ":" + d.Id;
    }

    private static void Increment(IDictionary<ComponentCategory, int> map, ComponentCategory category)
    {
        map[category] = map.TryGetValue(category, out var n) ? n + 1 : 1;
    }
}
