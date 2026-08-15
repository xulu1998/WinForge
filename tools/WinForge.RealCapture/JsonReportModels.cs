using System;
using System.Collections.Generic;

namespace WinForge.RealCapture;

// Deterministic JSON report shapes for the elevated real-inventory capture
// (Phase 14 Stage 14.3, Part A §2/§3/§4/§5). Serialized with
// System.Text.Json (camelCase, indented). No host paths / temp mount paths are
// ever written into these files.

public sealed class InventorySummaryJson
{
    public string? TargetIso { get; init; }
    public string? IsoFileName { get; init; }
    public int SelectedIndex { get; init; }
    public string? EditionName { get; init; }
    public string? Architecture { get; init; }
    public string? Build { get; init; }
    public string GeneratedUtc { get; init; } = DateTime.UtcNow.ToString("O");
    public string WinForgeVersion { get; init; } = string.Empty;

    /// <summary>Per-category discovery status (Success/Failed/NotSupported + error).</summary>
    public IReadOnlyDictionary<string, string> CategoryStatus { get; init; } =
        new Dictionary<string, string>();

    public TotalsJson Totals { get; init; } = new();

    /// <summary>Matcher-level logical curated components (collapsed), informational.</summary>
    public int CuratedLogicalComponents { get; init; }
}

public sealed class TotalsJson
{
    public int TotalInventory { get; init; }
    public int Curated { get; init; }
    public int Protected { get; init; }
    public int KnownDeep { get; init; }
    public int Heuristic { get; init; }
    public int Unknown { get; init; }
    public int MatcherProtected { get; init; }

    /// <summary>Knowledge-backed coverage: (Curated + KnownDeep) / Total. EXACT, not estimated.</summary>
    public double CoverageRatio { get; init; }

    /// <summary>Fully classified: (Curated + KnownDeep + Heuristic) / Total.</summary>
    public double TotalClassifiedRatio { get; init; }
}

public sealed class InventoryItemJson
{
    public string Id { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;

    /// <summary>Exclusive bucket: Curated | KnownDeep | Heuristic | Unknown.</summary>
    public string Classification { get; init; } = string.Empty;

    public string? CanonicalId { get; init; }
    public string? Function { get; init; }
    public string? Risk { get; init; }
    public string? Protection { get; init; }
    public string? Recommendation { get; init; }
    public string? Confidence { get; init; }
}

public sealed class SourceCoverageJson
{
    public string Source { get; init; } = string.Empty;
    public int Total { get; init; }
    public int Curated { get; init; }
    public int Protected { get; init; }
    public int Known { get; init; }
    public int Heuristic { get; init; }
    public int Unknown { get; init; }
}

public sealed class CoverageBySourceJson
{
    public TotalsJson Totals { get; init; } = new();
    public IReadOnlyList<SourceCoverageJson> Sources { get; init; } = new List<SourceCoverageJson>();
}

public sealed class UnknownItemsJson
{
    public int Count { get; init; }
    public IReadOnlyList<UnknownItemJson> Items { get; init; } = new List<UnknownItemJson>();
}

public sealed class UnknownItemJson
{
    public string Id { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Normalized { get; init; } = string.Empty;
}

public sealed class UnknownFamiliesJson
{
    public int Count { get; init; }
    public IReadOnlyList<UnknownFamilyJson> Families { get; init; } = new List<UnknownFamilyJson>();
}

public sealed class UnknownFamilyJson
{
    public int Rank { get; init; }
    public string Family { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public int Count { get; init; }
    public IReadOnlyList<string> RepresentativeIdentifiers { get; init; } = new List<string>();
    public string NormalizedKey { get; init; } = string.Empty;

    /// <summary>Deterministic reason the family is currently Unknown (template, never AI prose).</summary>
    public string Reason { get; init; } = string.Empty;
}

public sealed class GamingCandidateJson
{
    public string Id { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string? CanonicalId { get; init; }
    public string? DisplayName { get; init; }
    public string? ProfileTag { get; init; }
    public string? Function { get; init; }
    public string? Risk { get; init; }
    public string? Recommendation { get; init; }
}

public sealed class GamingCandidatesJson
{
    public int Count { get; init; }
    public IReadOnlyList<GamingCandidateJson> Items { get; init; } = new List<GamingCandidateJson>();
}

/// <summary>
/// Compact, stable, real-derived regression fixture shape (Part A §5): unique
/// (source, family) representatives with VERSION/ARCH/LANGUAGE/host-path tokens
/// stripped. Intended to be copied to tests/fixtures/ after the elevated run.
/// </summary>
public sealed class RealDerivedFamiliesJson
{
    public string Media { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public string GeneratedUtc { get; init; } = DateTime.UtcNow.ToString("O");
    public IReadOnlyList<RealDerivedFamilyEntryJson> Entries { get; init; } = new List<RealDerivedFamilyEntryJson>();
}

public sealed class RealDerivedFamilyEntryJson
{
    public string Source { get; init; } = string.Empty;
    public string Family { get; init; } = string.Empty;

    /// <summary>One stable representative identity (stripped of versions/paths).</summary>
    public string Representative { get; init; } = string.Empty;

    /// <summary>Expected classification bucket for the representative.</summary>
    public string Classification { get; init; } = string.Empty;

    public string? CanonicalId { get; init; }
}

/// <summary>Phase 15 Stage 15.2 — per-profile plan summaries over the captured inventory (v2 schema, ADR-095).</summary>
public sealed class ProfilePlansJson
{
    public string Media { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public string GeneratedUtc { get; init; } = DateTime.UtcNow.ToString("O");

    /// <summary>Global inventory accounting — identical for every profile (invariant: total = evaluated + exclusions).</summary>
    public ProfileInventoryAccountingJson Inventory { get; init; } = new();

    /// <summary>Non-inventory optimization definitions added to the candidate stream (deduped).</summary>
    public int OptimizationCandidates { get; init; }

    public int OptimizationDuplicates { get; init; }

    public List<ProfilePlanJson> Profiles { get; init; } = new();
}

/// <summary>Exact bucket accounting over the real inventory objects (no double counting).</summary>
public sealed class ProfileInventoryAccountingJson
{
    public int TotalInventory { get; init; }
    public int EvaluatedForProfile { get; init; }
    public int CuratedOutsideDeepInventory { get; init; }
    public int ExcludedUnknownKnowledge { get; init; }
    public int ExcludedUnsupportedSource { get; init; }
    public int ExcludedFilteredDuplicate { get; init; }
    public int ExcludedNotApplicable { get; init; }
    public int ExcludedOther { get; init; }

    /// <summary>Evaluated objects (deep + curated) per component category — InventoryBySource.</summary>
    public Dictionary<string, int> BySource { get; init; } = new();

    public int Evaluated => EvaluatedForProfile + CuratedOutsideDeepInventory;
}

/// <summary>One primary profile's v2 plan summary.</summary>
public sealed class ProfilePlanJson
{
    public string ProfileId { get; init; } = string.Empty;

    /// <summary>Same global accounting (per-profile copy for schema completeness).</summary>
    public ProfileInventoryAccountingJson InventoryAccounting { get; init; } = new();

    public ProfileDecisionCountsJson DecisionCounts { get; init; } = new();

    /// <summary>Actual EXECUTABLE changes (AutoApply + Recommend) by operation type.</summary>
    public ProfilePlanChangesJson PlanChanges { get; init; } = new();

    /// <summary>Actual Auto/Recommend semantic action keys ("AppX|PhoneLink|AutoApply").</summary>
    public List<string> SemanticActionKeys { get; init; } = new();

    /// <summary>Bounded kept-for-compatibility display names (≤6).</summary>
    public List<string> KeptHighlights { get; init; } = new();

    /// <summary>Bounded blocked display names (≤4).</summary>
    public List<string> BlockedHighlights { get; init; } = new();
}

public sealed class ProfileDecisionCountsJson
{
    public int AutoApply { get; init; }
    public int Recommended { get; init; }
    public int Optional { get; init; }
    public int Kept { get; init; }
    public int Blocked { get; init; }
    public int NotApplicable { get; init; }
}

/// <summary>Executable profile changes (changeCount = AutoApply + Recommend only).</summary>
public sealed class ProfilePlanChangesJson
{
    public int Total { get; init; }
    public Dictionary<string, int> ByOperationType { get; init; } = new();
}

/// <summary>Phase 15 Stage 15.3 — structural BuildPlan validation over the real
/// captured inventory (ADR-096). PLAN VALIDATION ONLY: nothing is applied.</summary>
public sealed class ProfileBuildPlansJson
{
    public string Media { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public string GeneratedUtc { get; init; } = DateTime.UtcNow.ToString("O");
    public List<ProfileBuildPlanJson> Profiles { get; init; } = new();
}

/// <summary>One primary profile's validated BuildPlan summary (structural only).</summary>
public sealed class ProfileBuildPlanJson
{
    public string ProfileId { get; init; } = string.Empty;

    /// <summary>ProfileDeltaReport changeCount (AutoApply + Recommend) — SEMANTIC change entries.</summary>
    public int DeltaCount { get; init; }

    /// <summary>Explicit alias of <see cref="DeltaCount"/> (ADR-096 addendum §8 count reconciliation).</summary>
    public int SemanticChangeCount => DeltaCount;

    /// <summary>Total plan operations (AutoApply selected + Recommend present-unselected) — EXECUTABLE operations after canonical aggregation.</summary>
    public int BuildPlanOperationCount { get; init; }

    /// <summary>Selected (AutoApply) operations — what Apply would execute.</summary>
    public int SelectedOperationCount { get; init; }

    /// <summary>Semantic change candidates absorbed into canonical merges (0 = no true duplicates on this media).</summary>
    public int MergedDuplicateCount { get; init; }

    /// <summary>Count of merge groups (N semantic candidates → 1 executable operation).</summary>
    public int MergeGroupCount { get; init; }

    /// <summary>Change candidates dropped because a Keep intent won at the semantic level (keep-wins precedence).</summary>
    public int DroppedKeepWins { get; init; }

    public bool ValidationPassed { get; init; }
    public List<string> ValidationErrors { get; init; } = new();
    public Dictionary<string, int> OperationsByType { get; init; } = new();
    public List<string> CanonicalOperationKeys { get; init; } = new();

    /// <summary>Diagnostic canonical merges (ADR-096 addendum §9) — empty when no true duplicates existed.</summary>
    public List<ProfileBuildPlanMergeGroupJson> MergeGroups { get; init; } = new();
}

/// <summary>One canonical executable merge — diagnostic only (ADR-096 addendum §9).</summary>
public sealed class ProfileBuildPlanMergeGroupJson
{
    /// <summary>The executable canonical key of the merged operation (e.g. "OptionalFeature|Microsoft-Hyper-V-Services").</summary>
    public string CanonicalKey { get; init; } = string.Empty;

    /// <summary>How many semantic candidates merged into this operation (>= 2).</summary>
    public int SourceCount { get; init; }

    /// <summary>Semantic change keys ("OpType|LogicalId|Disposition") of the merged candidates.</summary>
    public List<string> SourceIds { get; init; } = new();

    /// <summary>Executable/source identities of the merged candidates.</summary>
    public List<string> SourceIdentities { get; init; } = new();
}
