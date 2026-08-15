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
    /// <summary>
    /// Semantic (profile-facing) identity — the canonical family id (e.g. "HyperV",
    /// "Containers", "MediaPlayer"). Multiple REAL Windows objects can share it
    /// (family aliasing): profile intent matching, keep overrides, gaming policy,
    /// delta keys and the preview all operate at THIS level.
    /// </summary>
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

    /// <summary>
    /// Stage 15.3b (ADR-096 addendum): the EXECUTABLE technical identity — the
    /// actual name sent to DISM / the service name / the package identity — as
    /// opposed to the profile-facing <see cref="LogicalId"/> family alias. The
    /// canonical executable operation identity is built from THIS field, so
    /// distinct real features that share a family (HyperV x9, Containers x4,
    /// MediaPlayer AppX+OptionalFeature) stay distinct in the executable plan,
    /// while genuine same-target candidates (same feature from multiple sources)
    /// collapse into one operation during aggregation.
    /// </summary>
    public string ExecutableIdentity { get; init; } = string.Empty;

    /// <summary>
    /// The requested executable state of the candidate (Remove / Disable /
    /// Configure). Aggregation refuses to silently merge DIFFERENT states for
    /// the same executable target — that is an explicit conflict (ADR-096 addendum §5).
    /// </summary>
    public OptimizationAction ActionKind { get; init; } = OptimizationAction.Remove;

    /// <summary>
    /// Provenance of this operation (Stage 15.3b §4): the ordered, distinct
    /// source identities of every semantic candidate merged into one executable
    /// operation (raw feature/package identities for inventory objects, catalog
    /// definition ids for optimization definitions). Mirrors the existing
    /// registry <c>SourceDefinitionIds</c> behavior — "this operation exists
    /// because of these sources". Single-candidate items carry exactly one id.
    /// </summary>
    public IReadOnlyList<string> SourceDefinitionIds { get; init; } = new List<string>();

    /// <summary>Number of semantic candidates merged into this executable item (1 = none).</summary>
    public int MergedSourceCount { get; init; } = 1;

    /// <summary>
    /// Canonical executable operation key — the identity used to detect TRUE
    /// duplicates (two candidates → the same executable change) and to merge
    /// same-target candidates. Mirrors the plan operation ConflictKey prefixes
    /// (svc:|opt:|feat:|appx:|cap:|pkg:).
    /// </summary>
    public string ExecutableCanonicalKey => string.IsNullOrWhiteSpace(ExecutableIdentity)
        ? $"{OperationType}|{LogicalId}"
        : $"{OperationType}|{ExecutableIdentity}";
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

    /// <summary>
    /// Stage 15.2 (ADR-095 §2): profile-driven EXECUTABLE changes (AutoApply +
    /// Recommend only) by operation type. Known-but-unsupported inventory (e.g.
    /// Capability / CBS) never looks like a planned operation here. The old
    /// Stage 15.1 semantics (every present item) was misleading — it duplicated
    /// InventoryBySource; use <see cref="ProfileInventoryAccounting.BySource"/> for
    /// inventory source counts.
    /// </summary>
    public IReadOnlyDictionary<ExecutionOperationType, int> ByOperationType { get; init; } =
        new Dictionary<ExecutionOperationType, int>();

    /// <summary>Same as <see cref="ByOperationType"/> — explicit v2 schema name.</summary>
    public IReadOnlyDictionary<ExecutionOperationType, int> PlanChangesByOperationType => ByOperationType;

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

    /// <summary>
    /// Stage 15.2b (ADR-095 addendum): the curated definition behind a
    /// curated-only inventory object (null for deep/unclassified subjects).
    /// The gaming policy is dispatched for curated subjects too, using a
    /// synthesized knowledge view — curated items no longer bypass profile
    /// policy (the real-media Gaming == Dedicated wiring defect).
    /// </summary>
    public ComponentDefinition? CuratedDefinition { get; init; }

    /// <summary>
    /// Stage 15.3 (ADR-096): the optimization definition behind a non-inventory
    /// subject (Service / RegistryPolicy / Privacy / Personalization /
    /// OptionalFeature). BuildPlan maps its EXECUTION payload (service name,
    /// registry targets, feature name) into complete plan operations — the
    /// real-stream blocker where plan ops were built without payloads and the
    /// validator correctly rejected them.
    /// </summary>
    public OptimizationDefinition? OptimizationDefinition { get; init; }

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

    /// <summary>
    /// Stage 15.2 (ADR-095 §7): subject for a curated-only inventory object —
    /// curated knowledge (recommendation/risk/removal/dependencies) drives the
    /// engine exactly like deep knowledge. Curated removal is Supported, so a
    /// curated AppX trim can execute when the profile steers it.
    /// </summary>
    public static ProfilePlanSubject FromCurated(string rawIdentity, ComponentDefinition d, ComponentCategory category)
    {
        var opType = OperationTypeForCategory(category);
        return new ProfilePlanSubject
        {
            LogicalId = d.Id,
            DisplayName = d.Id,
            RawIdentity = rawIdentity,
            Category = category,
            OperationType = opType,
            Action = OptimizationAction.Remove,
            DefaultRecommendation = d.Recommendation,
            Risk = d.Risk,
            Removal = d.Removal,
            IsPresent = true,
            IsApplySupported = true,
            Dependencies = d.Dependencies,
            DeepKnowledge = null,
            CuratedDefinition = d,
            Protection = ComponentProtectionLevel.None,
            Confidence = ClassificationConfidence.Curated,
            ExecutionSupported = ExecutionSupportMatrix.IsExecutable(opType),
        };
    }

    /// <summary>
    /// Stage 15.2 (ADR-095 §7): subject for a non-inventory optimization
    /// definition (Service / RegistryPolicy / Privacy / Personalization /
    /// OptionalFeature). These participate in profile plans exactly like
    /// inventory-derived candidates — deduplicated by canonical operation
    /// identity in <see cref="ProfileCandidateService"/>.
    /// </summary>
    public static ProfilePlanSubject FromOptimization(OptimizationDefinition d)
    {
        var opType = OperationTypeForOptimization(d);
        var category = CategoryForTab(d.Tab);
        return new ProfilePlanSubject
        {
            LogicalId = d.Id,
            DisplayName = d.Id,
            RawIdentity = string.Empty,
            Category = category,
            OperationType = opType,
            Action = d.Action == OptimizationAction.Unknown ? OptimizationAction.Disable : d.Action,
            DefaultRecommendation = d.Recommendation,
            Risk = d.Risk,
            Removal = d.Removal,
            IsPresent = true,
            IsApplySupported = true,
            Dependencies = d.Dependencies,
            OptimizationDefinition = d,
            Protection = ComponentProtectionLevel.None,
            Confidence = ClassificationConfidence.Curated,
            ExecutionSupported = ExecutionSupportMatrix.IsExecutable(opType),
        };
    }

    public static ExecutionOperationType OperationTypeForCategory(ComponentCategory category) => category switch
    {
        ComponentCategory.AppX => ExecutionOperationType.AppX,
        ComponentCategory.Capability => ExecutionOperationType.Capability,
        ComponentCategory.OptionalFeature => ExecutionOperationType.OptionalFeature,
        ComponentCategory.CbsPackage => ExecutionOperationType.CbsPackage,
        ComponentCategory.Service => ExecutionOperationType.Service,
        _ => ExecutionOperationType.Other,
    };

    public static ExecutionOperationType OperationTypeForOptimization(OptimizationDefinition d)
    {
        if (d.Mechanism == OptimizationMechanism.ServiceStartup)
        {
            return ExecutionOperationType.Service;
        }

        return d.Tab switch
        {
            OptimizationTab.Apps => ExecutionOperationType.AppX,
            OptimizationTab.WindowsComponents => ExecutionOperationType.OptionalFeature,
            OptimizationTab.Services => ExecutionOperationType.Service,
            OptimizationTab.Privacy => ExecutionOperationType.Privacy,
            OptimizationTab.System => ExecutionOperationType.RegistryPolicy,
            OptimizationTab.Personalization => ExecutionOperationType.Personalization,
            _ => ExecutionOperationType.RegistryPolicy,
        };
    }

    private static ComponentCategory CategoryForTab(OptimizationTab tab) => tab switch
    {
        OptimizationTab.Apps => ComponentCategory.AppX,
        OptimizationTab.WindowsComponents => ComponentCategory.OptionalFeature,
        OptimizationTab.Services => ComponentCategory.Service,
        _ => ComponentCategory.Unknown,
    };

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
