using System;
using System.Collections.Generic;
using WinForge.Core.ComponentIntelligence;
using WinForge.Core.Models;

namespace WinForge.Core.Profiles;

// =====================================================================
// Phase 14 Stage 14.3 — GAMING PROFILE 2.0 (Part C, ADR-088..090)
//
// The gaming profiles are KNOWLEDGE-DRIVEN: the recommendation pipeline is
//
//     Inventory → Deep Knowledge → Profile Policy → Candidate Recommendation
//                → Safety Gate → Plan
//
// The policy consumes DeepComponentKnowledge (Function / Risk / Recommendation
// / Protection / ProfileTag / DependencyTags) plus the selected EXTRAS. It does
// NOT operate on raw Windows identity strings as its primary decision mechanism.
// The Safety Gate has FINAL authority and is applied BEFORE a decision may reach
// the plan layer. Known != SupportedForRemoval (ADR-086): classification alone
// never enables an execution mechanism.
//
// Two DISTINCT product concepts (ADR-089) — never aliases:
//   GamingPc        — a normal personal Windows PC optimized for gaming while
//                     staying convenient (Low-risk automatic changes only).
//   DedicatedGaming — a more minimal gaming-only machine; adds OPTIONAL
//                     recommendations but still preserves system health and
//                     mainstream gaming compatibility. NOT kiosk mode.
// =====================================================================

/// <summary>The two distinct gaming profile kinds (ADR-089).</summary>
public enum GamingProfileKind
{
    Unknown = 0,

    /// <summary>Gaming PC — convenient personal PC optimized for gaming.</summary>
    GamingPc,

    /// <summary>Dedicated Gaming — minimal gaming-only machine (optional extras only).</summary>
    DedicatedGaming,
}

/// <summary>Independent usage extras that must influence gaming decisions (Part C §9).</summary>
public enum GamingExtra
{
    None = 0,
    XboxGamePass,
    WslDocker,
    PrintScan,
    TouchPen,
    RemoteDesktop,
}

/// <summary>What the policy wants for an item (pre-gate verdict).</summary>
public enum GamingVerdict
{
    Unknown = 0,

    /// <summary>No knowledge-backed steer — the item falls through to defaults.</summary>
    NoOpinion,

    /// <summary>Must be kept (infrastructure / extras-required / dependency-preserving).</summary>
    KeepForCompatibility,

    /// <summary>Safe to remove automatically (Low risk + curated knowledge support).</summary>
    AutoRemoveCandidate,

    /// <summary>Removable only with explicit user intent — the profile may suggest it.</summary>
    OptionalRemoveCandidate,
}

/// <summary>Safety-gate verdict (final authority, ADR-090).</summary>
public enum GateVerdict
{
    Unknown = 0,

    /// <summary>May act automatically.</summary>
    AllowAuto,

    /// <summary>May act only when the user explicitly confirms (optional suggestion).</summary>
    AllowOptional,

    /// <summary>Never acted on by the profile (blocked / keep).</summary>
    Block,
}

/// <summary>
/// Input for one item's gaming policy evaluation. <see cref="Knowledge"/> is the
/// production deep classification; the policy never re-parses raw identity text
/// for its decisions (the catalog id and semantic fields carry the meaning).
/// </summary>
public sealed class GamingPolicyInput
{
    /// <summary>The raw discovered identity (kept for traceability/export only).</summary>
    public string RawIdentity { get; init; } = string.Empty;

    public ComponentCategory Source { get; init; } = ComponentCategory.Unknown;

    public DeepComponentKnowledge Knowledge { get; init; } = new();

    public IReadOnlySet<GamingExtra> Extras { get; init; } = new HashSet<GamingExtra>();

    /// <summary>True when the object exists in the mounted image.</summary>
    public bool IsPresent { get; init; } = true;

    /// <summary>True when an ALREADY-SUPPORTED safe action exists for this object
    /// (ADR-086: classification is not execution support).</summary>
    public bool SupportedForRemoval { get; init; }

    /// <summary>True when the user manually chose this item (override, Part K).</summary>
    public bool HasUserOverride { get; init; }
}

/// <summary>Deterministic pre-gate policy decision for one item.</summary>
public sealed class GamingPolicyDecision
{
    public GamingProfileKind Kind { get; init; } = GamingProfileKind.Unknown;
    public GamingVerdict Verdict { get; init; } = GamingVerdict.NoOpinion;

    /// <summary>Deterministic localized reason-key template (never runtime AI prose).</summary>
    public string ReasonKey { get; init; } = string.Empty;

    /// <summary>Which extra forced a keep (kept because X is enabled).</summary>
    public GamingExtra? KeptByExtra { get; init; }
}

/// <summary>Post-gate result: the FINAL decision the plan layer may see.</summary>
public sealed class GamingEvaluationResult
{
    public string RawIdentity { get; init; } = string.Empty;
    public string? CanonicalId { get; init; }
    public ComponentCategory Source { get; init; } = ComponentCategory.Unknown;
    public ComponentFunctionCategory Function { get; init; } = ComponentFunctionCategory.Unknown;
    public ComponentRiskLevel Risk { get; init; } = ComponentRiskLevel.Unknown;
    public ComponentProtectionLevel Protection { get; init; } = ComponentProtectionLevel.None;
    public ClassificationConfidence Confidence { get; init; } = ClassificationConfidence.Unknown;
    public GamingVerdict Verdict { get; init; } = GamingVerdict.NoOpinion;
    public GateVerdict Gate { get; set; } = GateVerdict.Block;
    public string ReasonKey { get; init; } = string.Empty;
    public string GateReasonKey { get; set; } = string.Empty;
    public GamingExtra? KeptByExtra { get; init; }
    public bool HasUserOverride { get; init; }

    /// <summary>Auto-actionable: gate AllowAuto AND the verdict is a change.</summary>
    public bool IsAutoRecommended => Gate == GateVerdict.AllowAuto &&
        (Verdict is GamingVerdict.AutoRemoveCandidate or GamingVerdict.OptionalRemoveCandidate);

    /// <summary>Visible optional suggestion (user-confirmed action).</summary>
    public bool IsOptionalSuggestion => Gate == GateVerdict.AllowOptional &&
        Verdict is GamingVerdict.AutoRemoveCandidate or GamingVerdict.OptionalRemoveCandidate;

    /// <summary>Kept for compatibility (policy or gate keep).</summary>
    public bool IsKeptForCompatibility => Verdict == GamingVerdict.KeepForCompatibility;
}

/// <summary>One gaming-profile evaluation (per item) + aggregated summary counts.</summary>
public sealed class GamingEvaluationItem
{
    public GamingEvaluationResult Result { get; init; } = new();

    /// <summary>Human display name (fallback-safe, deterministic).</summary>
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary>
/// User-facing profile summary (ADR-088, §13): aggregated counts + bounded
/// representative examples. Never exposes hundreds of technical identities.
/// </summary>
public sealed class GamingProfileSummary
{
    public GamingProfileKind Kind { get; init; } = GamingProfileKind.Unknown;

    /// <summary>Safe, gate-passed, automatic changes the profile recommends.</summary>
    public int RecommendedChanges { get; init; }

    /// <summary>Items kept for compatibility (infrastructure / extras / dependencies).</summary>
    public int KeptForCompatibility { get; init; }

    /// <summary>Optional, user-confirmed suggestions.</summary>
    public int OptionalChoices { get; init; }

    /// <summary>Bounded representative display names for each bucket (localized in App).</summary>
    public IReadOnlyList<GamingEvaluationItem> Items { get; init; } = new List<GamingEvaluationItem>();
}
