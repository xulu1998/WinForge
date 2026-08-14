using System;
using System.Collections.Generic;
using System.Linq;
using WinForge.Core.ComponentIntelligence;
using WinForge.Core.Models;

namespace WinForge.Core.Profiles;

// =====================================================================
// Phase 15 Stage 15.1 — PROFILE EXECUTION & SAFE EXECUTION MATRIX (ADR-094)
//
// Converts profile KNOWLEDGE into meaningful, supported EXECUTION plans.
// The execution matrix consumes EffectiveRecommendation + knowledge fields
// (risk / protection / confidence) + execution support — NEVER raw Windows
// identity strings as the primary decision mechanism. Every item gets an
// explicit disposition and a deterministic localized reason.
//
// Dispositions: AutoApply | Recommend | Optional | Keep | Blocked | NotApplicable.
// =====================================================================

/// <summary>
/// What the profile wants done with an item, converted into an EXECUTABLE
/// disposition (Stage 15.1). AutoApply is the only disposition that changes the
/// image without user confirmation; everything else is a recommendation.
/// </summary>
public enum ProfileDisposition
{
    Unknown = 0,

    /// <summary>Absent / unknown / no steer — nothing to plan.</summary>
    NotApplicable,

    /// <summary>Applied automatically (safe, curated, Low-risk, supported, profile-driven).</summary>
    AutoApply,

    /// <summary>Recommended change — user confirms via 采用推荐/adopt, or explicit choice.</summary>
    Recommend,

    /// <summary>Optional suggestion — user-confirmed only (ManualReview items the profile steers).</summary>
    Optional,

    /// <summary>Kept for compatibility (requirement / dependency / extras / protection / runtime).</summary>
    Keep,

    /// <summary>Blocked by the safety gate or execution support (never in an executable plan).</summary>
    Blocked,
}

/// <summary>
/// Auditable operation-type taxonomy for the execution support matrix and the
/// profile delta report's per-type breakdown (Stage 15.1 §4/§6).
/// </summary>
public enum ExecutionOperationType
{
    Unknown = 0,
    AppX,
    Capability,
    OptionalFeature,
    CbsPackage,
    Driver,
    Service,
    RegistryPolicy,
    Privacy,
    Personalization,
    Other,
}

/// <summary>
/// One item's profile-execution decision. Deterministic, fully explainable.
/// </summary>
public sealed class ProfileExecutionItem
{
    public string LogicalId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public ExecutionOperationType OperationType { get; init; } = ExecutionOperationType.Unknown;
    public ProfileDisposition Disposition { get; init; } = ProfileDisposition.Unknown;
    public string ReasonKey { get; init; } = string.Empty;
    public string ProfileId { get; init; } = string.Empty;
    public bool IsPresent { get; init; } = true;
    public bool IsUserOverride { get; init; }
    public bool WasProfileDriven { get; init; }
    public bool IsExecutableChange => Disposition is ProfileDisposition.AutoApply or ProfileDisposition.Recommend;
}

/// <summary>
/// Per-profile plan summary: exact counts by disposition + operation-type
/// breakdown + the items themselves. This is the deterministic PROOF that
/// profiles produce different images (§6/§7).
/// </summary>
public sealed class ProfileDeltaReport
{
    public string ProfileId { get; init; } = string.Empty;

    public int AutoApply { get; init; }
    public int Recommended { get; init; }
    public int Optional { get; init; }
    public int Kept { get; init; }
    public int Blocked { get; init; }
    public int NotApplicable { get; init; }

    /// <summary>Profile-driven changes (AutoApply + Recommended).</summary>
    public int ChangeCount => AutoApply + Recommended;

    /// <summary>Per-operation-type counts over ALL present items (incl. kept/blocked).</summary>
    public IReadOnlyDictionary<ExecutionOperationType, int> ByOperationType { get; init; } =
        new Dictionary<ExecutionOperationType, int>();

    public IReadOnlyList<ProfileExecutionItem> Items { get; init; } = new List<ProfileExecutionItem>();

    /// <summary>
    /// Semantic change-key set ("AppX|PhoneLink|AutoApply") — the identity used to
    /// prove two profiles differ by OPERATIONS, not by display strings (§7).
    /// </summary>
    public IReadOnlySet<string> ChangeKeys { get; init; } = new HashSet<string>();

    public int TotalPresent => AutoApply + Recommended + Optional + Kept + Blocked;
}

/// <summary>
/// Input for one item in the profile execution planner. Carries everything the
/// engine + matrix need; constructed from App knowledge rows, the CLI inventory,
/// or the real-derived fixture (all deterministic).
/// </summary>
public sealed class ProfilePlanSubject
{
    public string LogicalId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string RawIdentity { get; init; } = string.Empty;
    public ComponentCategory Category { get; init; } = ComponentCategory.Unknown;
    public ExecutionOperationType OperationType { get; init; } = ExecutionOperationType.Other;
    public OptimizationAction Action { get; init; } = OptimizationAction.Remove;
    public RecommendationLevel DefaultRecommendation { get; init; } = RecommendationLevel.Unknown;
    public RiskLevel Risk { get; init; } = RiskLevel.Unknown;
    public RemovalSupport Removal { get; init; } = RemovalSupport.Unknown;
    public bool IsPresent { get; init; } = true;
    public bool IsApplySupported { get; init; } = true;
    public IReadOnlyList<ComponentDependency> Dependencies { get; init; } = new List<ComponentDependency>();
    public DeepComponentKnowledge? DeepKnowledge { get; init; }

    /// <summary>Protection floor from deep knowledge (None when unknown).</summary>
    public ComponentProtectionLevel Protection { get; init; } = ComponentProtectionLevel.None;

    /// <summary>Classification confidence (Unknown for non-classified rows).</summary>
    public ClassificationConfidence Confidence { get; init; } = ClassificationConfidence.Unknown;

    /// <summary>True when a supported safe action already exists (ADR-086).</summary>
    public bool ExecutionSupported { get; init; } = true;

    public static ProfilePlanSubject FromKnowledge(string rawIdentity, ComponentCategory category, DeepComponentKnowledge k)
    {
        var opType = OperationTypeFor(k.Function, category);
        return new ProfilePlanSubject
        {
            LogicalId = k.CanonicalId,
            DisplayName = string.IsNullOrWhiteSpace(k.DisplayNameFallback) ? k.CanonicalId : k.DisplayNameFallback,
            RawIdentity = rawIdentity,
            Category = category,
            OperationType = opType,
            Action = OptimizationAction.Remove,
            DefaultRecommendation = MapRecommendation(k.Recommendation),
            Risk = MapRisk(k.Risk),
            Removal = RemovalSupport.Unknown,
            IsPresent = true,
            DeepKnowledge = k,
            Protection = k.Protection,
            Confidence = k.Confidence,
            ExecutionSupported = ExecutionSupportMatrix.IsExecutable(opType),
        };
    }

    public static RecommendationLevel MapRecommendation(ComponentRecommendationKind r) => r switch
    {
        ComponentRecommendationKind.RequiredKeep or ComponentRecommendationKind.RecommendedKeep
            => RecommendationLevel.UsuallyKeep,
        ComponentRecommendationKind.OptionalRemove => RecommendationLevel.OptionalRemove,
        ComponentRecommendationKind.RecommendedRemove => RecommendationLevel.RecommendedRemove,
        ComponentRecommendationKind.ProfileDependent => RecommendationLevel.OptionalRemove,
        _ => RecommendationLevel.Unknown,
    };

    public static RiskLevel MapRisk(ComponentRiskLevel r) => r switch
    {
        ComponentRiskLevel.Low => RiskLevel.Low,
        ComponentRiskLevel.Moderate => RiskLevel.Medium,
        ComponentRiskLevel.High => RiskLevel.High,
        ComponentRiskLevel.Critical => RiskLevel.Critical,
        _ => RiskLevel.Unknown,
    };

    public static ExecutionOperationType OperationTypeFor(ComponentFunctionCategory fn, ComponentCategory category)
    {
        if (category == ComponentCategory.Capability)
        {
            return ExecutionOperationType.Capability;
        }

        if (category == ComponentCategory.OptionalFeature)
        {
            return ExecutionOperationType.OptionalFeature;
        }

        if (category == ComponentCategory.CbsPackage)
        {
            return ExecutionOperationType.CbsPackage;
        }

        if (category == ComponentCategory.AppX || fn is ComponentFunctionCategory.Media
            or ComponentFunctionCategory.Gaming or ComponentFunctionCategory.Productivity
            or ComponentFunctionCategory.Communication or ComponentFunctionCategory.Developer)
        {
            return ExecutionOperationType.AppX;
        }

        return ExecutionOperationType.Other;
    }
}
