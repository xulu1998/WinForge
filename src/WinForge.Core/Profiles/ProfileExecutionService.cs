using System;
using System.Collections.Generic;
using System.Linq;
using WinForge.Core.ComponentIntelligence;
using WinForge.Core.Models;

namespace WinForge.Core.Profiles;

// =====================================================================
// Phase 15 Stage 15.1 — PROFILE EXECUTION SERVICE (ADR-094)
//
// Orchestrates: Inventory → Profile Knowledge → EffectiveRecommendation →
// ProfileExecutionMatrix (disposition) → ProfileDeltaReport (and optionally a
// validated CustomizationPlan). Pure, deterministic, platform-agnostic; feeds
// the user-facing preview, the fixture-based profile comparison, and the CLI.
// =====================================================================

public sealed class ProfileExecutionService
{
    private readonly IRecommendationEngine _engine;

    public ProfileExecutionService()
        : this(new RecommendationEngine())
    {
    }

    public ProfileExecutionService(IRecommendationEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    /// <summary>
    /// Generates the deterministic delta report for ONE primary profile over the
    /// given subjects (present items only contribute). Extras and overrides flow
    /// through the engine exactly like the live workflow.
    /// </summary>
    public ProfileDeltaReport GenerateDelta(
        ProfileDefinition profile,
        IReadOnlyList<ProfilePlanSubject> subjects,
        IReadOnlySet<GamingExtra> extras,
        IReadOnlyCollection<string> userOverrides,
        IReadOnlyCollection<string> presentIds)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(subjects);
        extras ??= new HashSet<GamingExtra>();
        userOverrides ??= new HashSet<string>();
        presentIds ??= new HashSet<string>();

        var isGaming = profile.GamingKind is { } kind;
        var items = new List<ProfileExecutionItem>();
        var auto = 0;
        var recommended = 0;
        var optional = 0;
        var kept = 0;
        var blocked = 0;
        var notApplicable = 0;
        var byType = new Dictionary<ExecutionOperationType, int>();
        var changeKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var subject in subjects)
        {
            if (!subject.IsPresent)
            {
                notApplicable++;
                continue;
            }

            // Gaming pipeline: use the POLICY verdict (pre-gate) so KEEPS surface
            // (EvaluateItem returns null for keep verdicts by design — that would
            // silently drop the keep intent here). The execution matrix below
            // re-applies the full safety gate (Protected/Critical/High/unsupported/
            // heuristic) as the final authority for this planner layer.
            var gamingKind = profile.GamingKind;
            GamingPolicyDecision? gamingDecision = null;
            if (gamingKind is not null && subject.DeepKnowledge is not null)
            {
                var policy = gamingKind.Value == GamingProfileKind.DedicatedGaming
                    ? (IGamingProfilePolicy)new DedicatedGamingPolicy()
                    : new GamingPcPolicy();
                var policyDecision = policy.Evaluate(new GamingPolicyInput
                {
                    RawIdentity = subject.RawIdentity,
                    Source = subject.Category,
                    Knowledge = subject.DeepKnowledge,
                    Extras = extras,
                    IsPresent = true,
                    SupportedForRemoval = subject.ExecutionSupported,
                    HasUserOverride = userOverrides.Contains(subject.LogicalId),
                });
                if (policyDecision.Verdict != GamingVerdict.NoOpinion)
                {
                    gamingDecision = policyDecision;
                }
            }

            var input = new RecommendationInput
            {
                LogicalId = subject.LogicalId,
                Action = subject.Action,
                DefaultRecommendation = subject.DefaultRecommendation,
                Risk = subject.Risk,
                Removal = subject.Removal,
                IsPresent = true,
                IsApplySupported = subject.IsApplySupported,
                Dependencies = subject.Dependencies,
                GamingDecision = gamingDecision,
            };

            var effective = _engine.Evaluate(input, new RecommendationContext
            {
                SelectedProfiles = new[] { profile },
                UserOverrides = userOverrides,
                PresentIds = presentIds,
            });

            var isHeuristic = subject.Confidence == ClassificationConfidence.Heuristic;
            var (disposition, reasonKey) = ProfileExecutionMatrix.Evaluate(
                profile.Id, effective, subject.Protection, subject.Confidence,
                subject.ExecutionSupported, isHeuristic);

            var item = new ProfileExecutionItem
            {
                LogicalId = subject.LogicalId,
                DisplayName = subject.DisplayName,
                OperationType = subject.OperationType,
                Disposition = disposition,
                ReasonKey = reasonKey,
                ProfileId = profile.Id,
                IsPresent = true,
                IsUserOverride = effective.WasOverridden,
                WasProfileDriven = effective.WasProfileDriven,
            };
            items.Add(item);

            byType[subject.OperationType] = byType.TryGetValue(subject.OperationType, out var n) ? n + 1 : 1;

            switch (disposition)
            {
                case ProfileDisposition.AutoApply:
                    auto++;
                    changeKeys.Add($"{subject.OperationType}|{subject.LogicalId}|AutoApply");
                    break;
                case ProfileDisposition.Recommend:
                    recommended++;
                    changeKeys.Add($"{subject.OperationType}|{subject.LogicalId}|Recommend");
                    break;
                case ProfileDisposition.Optional:
                    optional++;
                    break;
                case ProfileDisposition.Keep:
                    kept++;
                    break;
                case ProfileDisposition.Blocked:
                    blocked++;
                    break;
                default:
                    notApplicable++;
                    break;
            }
        }

        return new ProfileDeltaReport
        {
            ProfileId = profile.Id,
            AutoApply = auto,
            Recommended = recommended,
            Optional = optional,
            Kept = kept,
            Blocked = blocked,
            NotApplicable = notApplicable,
            ByOperationType = byType,
            Items = items.OrderBy(i => i.OperationType).ThenBy(i => i.LogicalId, StringComparer.Ordinal).ToList(),
            ChangeKeys = changeKeys,
        };
    }

    /// <summary>One delta report per primary profile (Custom excluded) — the deterministic profile comparison.</summary>
    public IReadOnlyList<ProfileDeltaReport> GenerateAllPrimaries(
        IReadOnlyList<ProfilePlanSubject> subjects,
        IReadOnlySet<GamingExtra> extras,
        IReadOnlyCollection<string> userOverrides,
        IReadOnlyCollection<string> presentIds,
        IReadOnlyList<ProfileDefinition> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var primaries = profiles.Where(p => p.Kind == ProfileKind.Primary && p.Id != "Custom").ToList();
        return primaries
            .Select(p => GenerateDelta(p, subjects, extras, userOverrides, presentIds))
            .ToList();
    }

    /// <summary>
    /// Builds a validated <see cref="CustomizationPlan"/> for the profile's
    /// executable changes (AutoApply + Recommend). Unsupported/blocked items are
    /// never placed in the plan. Returns the issues list (empty when valid).
    /// </summary>
    public (CustomizationPlan? Plan, IReadOnlyList<string> Issues) BuildPlan(
        ProfileDefinition profile,
        IReadOnlyList<ProfilePlanSubject> subjects,
        IReadOnlySet<GamingExtra> extras,
        IReadOnlyCollection<string> userOverrides,
        IReadOnlyCollection<string> presentIds)
    {
        var report = GenerateDelta(profile, subjects, extras, userOverrides, presentIds);
        var issues = new List<string>();
        var plan = new CustomizationPlan();

        foreach (var item in report.Items.Where(i => i.IsExecutableChange && !i.IsUserOverride))
        {
            if (!ExecutionSupportMatrix.IsExecutable(item.OperationType))
            {
                issues.Add($"Unsupported operation '{item.LogicalId}' excluded from plan.");
                continue;
            }

            plan.AddOperation(new CustomizationOperation
            {
                OperationId = $"{item.OperationType}:{item.LogicalId}",
                Category = MapCategory(item.OperationType),
                OperationType = MapOperationType(item.OperationType),
                DisplayName = item.DisplayName,
                Description = item.ReasonKey,
                TargetIdentifier = item.LogicalId,
                IsSelected = item.Disposition == ProfileDisposition.AutoApply,
                Risk = item.Disposition == ProfileDisposition.AutoApply ? RiskClass.Safe : RiskClass.Removable,
                ActionKind = OptimizationAction.Remove,
            });
        }

        var validation = ProfilePlanValidator.Validate(report.Items, plan);
        issues.AddRange(validation.Issues);
        return (validation.IsValid ? plan : null, issues);
    }

    private static CustomizationCategory MapCategory(ExecutionOperationType type) => type switch
    {
        ExecutionOperationType.AppX => CustomizationCategory.App,
        ExecutionOperationType.Capability => CustomizationCategory.Package,
        ExecutionOperationType.OptionalFeature => CustomizationCategory.Package,
        ExecutionOperationType.CbsPackage => CustomizationCategory.Package,
        ExecutionOperationType.Service => CustomizationCategory.Service,
        ExecutionOperationType.Privacy => CustomizationCategory.Privacy,
        ExecutionOperationType.Personalization => CustomizationCategory.Personalization,
        _ => CustomizationCategory.System,
    };

    private static CustomizationOperationType MapOperationType(ExecutionOperationType type) => type switch
    {
        ExecutionOperationType.AppX => CustomizationOperationType.RemoveProvisionedAppx,
        ExecutionOperationType.Capability => CustomizationOperationType.RemoveCapability,
        ExecutionOperationType.OptionalFeature => CustomizationOperationType.DisableOptionalFeature,
        ExecutionOperationType.CbsPackage => CustomizationOperationType.RemovePackage,
        ExecutionOperationType.Service => CustomizationOperationType.ConfigureOfflineService,
        _ => CustomizationOperationType.SetOfflineRegistryValue,
    };
}
