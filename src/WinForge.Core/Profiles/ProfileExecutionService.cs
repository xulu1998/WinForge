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
        IReadOnlyCollection<string> presentIds,
        IReadOnlyList<ProfileDefinition>? allProfiles = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(subjects);
        extras ??= new HashSet<GamingExtra>();
        userOverrides ??= new HashSet<string>();
        presentIds ??= new HashSet<string>();

        // Stage 15.2b (ADR-095 addendum): the planner's SelectedProfiles must
        // include the EXTRA SCENARIO profiles (Xbox/Game Pass, WSL/Docker, …) so
        // their data-driven Keep overrides actually reach the engine. Previously
        // the extras' keep intents were dead in the planner — Lightweight could
        // auto-disable Xbox services even with the Xbox extra enabled.
        var selectedProfiles = new List<ProfileDefinition> { profile };
        if (allProfiles is not null)
        {
            selectedProfiles.AddRange(SelectExtraProfiles(allProfiles, extras));
        }

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
            if (gamingKind is not null)
            {
                // Stage 15.2b: dispatch the policy for CURATED-ONLY subjects too,
                // using a synthesized knowledge view. On real media the curated
                // consumer/cloud items (OneDrive, Teams, …) were bypassing the
                // policy entirely — the reason Gaming PC and Dedicated Gaming came
                // out IDENTICAL despite the WiderMinimalSteer policy.
                var knowledge = subject.DeepKnowledge ?? SynthesizeFromCurated(subject.CuratedDefinition, subject.Category);
                if (knowledge is not null)
                {
                    var policy = gamingKind.Value == GamingProfileKind.DedicatedGaming
                        ? (IGamingProfilePolicy)new DedicatedGamingPolicy()
                        : new GamingPcPolicy();
                    var policyDecision = policy.Evaluate(new GamingPolicyInput
                    {
                        RawIdentity = subject.RawIdentity,
                        Source = subject.Category,
                        Knowledge = knowledge,
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
                SelectedProfiles = selectedProfiles,
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

            // Stage 15.2 (ADR-095 §2): ByOperationType counts PROFILE-DRIVEN
            // EXECUTABLE CHANGES only (AutoApply + Recommend) — never the static
            // inventory-source totals. Use ProfileInventoryAccounting.BySource for
            // inventory source counts.
            if (item.IsExecutableChange)
            {
                byType[subject.OperationType] = byType.TryGetValue(subject.OperationType, out var n) ? n + 1 : 1;
            }

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
            .Select(p => GenerateDelta(p, subjects, extras, userOverrides, presentIds, profiles))
            .ToList();
    }

    /// <summary>
    /// Builds a validated <see cref="CustomizationPlan"/> for the profile's
    /// executable changes (AutoApply selected + Recommend present-unselected).
    /// Unsupported/blocked items are never placed in the plan. Stage 15.3
    /// (ADR-096): every operation carries its COMPLETE execution payload (service
    /// name + start type, registry hive/path/value/kind/data, feature/package
    /// identity) — the real-stream blocker was ops built without payloads, which
    /// the validator correctly rejected. Returns the issues list (empty when valid).
    /// </summary>
    public (CustomizationPlan? Plan, IReadOnlyList<string> Issues) BuildPlan(
        ProfileDefinition profile,
        IReadOnlyList<ProfilePlanSubject> subjects,
        IReadOnlySet<GamingExtra> extras,
        IReadOnlyCollection<string> userOverrides,
        IReadOnlyCollection<string> presentIds,
        IReadOnlyList<ProfileDefinition>? allProfiles = null)
    {
        var report = GenerateDelta(profile, subjects, extras, userOverrides, presentIds, allProfiles);
        var issues = new List<string>();
        var plan = new CustomizationPlan();

        foreach (var item in report.Items.Where(i => i.IsExecutableChange && !i.IsUserOverride))
        {
            if (!ExecutionSupportMatrix.IsExecutable(item.OperationType))
            {
                issues.Add($"Unsupported operation '{item.LogicalId}' excluded from plan.");
                continue;
            }

            var subject = subjects.FirstOrDefault(s =>
                string.Equals(s.LogicalId, item.LogicalId, StringComparison.Ordinal));
            if (subject is null)
            {
                issues.Add($"Subject for plan operation '{item.LogicalId}' was not found.");
                continue;
            }

            // Stage 15.3: definition-level validation fails safe BEFORE the op is
            // constructed — malformed definitions are found at plan time too, not
            // only in catalog tests (ADR-096 §5).
            if (subject.OptimizationDefinition is { } def)
            {
                var defIssues = OptimizationDefinitionValidator.ValidateDefinition(def);
                if (defIssues.Count > 0)
                {
                    issues.AddRange(defIssues.Select(d => $"'{def.Id}': {d}"));
                    continue;
                }
            }

            foreach (var op in BuildOperations(subject, item))
            {
                plan.AddOperation(op);
            }
        }

        var validation = ProfilePlanValidator.Validate(report.Items, plan);
        issues.AddRange(validation.Issues);
        return (validation.IsValid ? plan : null, issues);
    }

    /// <summary>
    /// Maps one executable item to its complete plan operation(s). Optimization
    /// definitions map their real payload (service name, registry targets,
    /// feature name); component items map their discovered identity (raw package
    /// identity, feature name). Canonical operation ids reuse the live app
    /// conventions (svc:|opt:|feat:|appx:|cap:) so profile plans dedupe and
    /// conflict-detect identically to the Customize flow.
    /// </summary>
    private static IReadOnlyList<CustomizationOperation> BuildOperations(ProfilePlanSubject subject, ProfileExecutionItem item)
    {
        var selected = item.Disposition == ProfileDisposition.AutoApply;
        var risk = selected ? RiskClass.Safe : RiskClass.Removable;

        if (subject.OptimizationDefinition is { } def)
        {
            if (def.Mechanism == OptimizationMechanism.ServiceStartup)
            {
                var svc = new CustomizationOperation
                {
                    OperationId = "svc|" + def.ServiceName,
                    Category = CustomizationCategory.Service,
                    OperationType = CustomizationOperationType.ConfigureOfflineService,
                    DisplayName = subject.DisplayName,
                    Description = item.ReasonKey,
                    TargetIdentifier = def.ServiceName,
                    ServiceName = def.ServiceName,
                    ServiceStartType = def.ProposedStartType,
                    IsSelected = selected,
                    Risk = risk,
                    ActionKind = def.Action,
                    Mechanism = def.Mechanism,
                    Scope = def.Scope,
                    ReversalKey = def.ReversalKey,
                    ExecutionOrder = 0,
                };
                svc.AddSourceDefinition(def.Id);
                return new[] { svc };
            }

            if (def.Tab == OptimizationTab.WindowsComponents && !string.IsNullOrWhiteSpace(def.TargetIdentifier))
            {
                var feat = new CustomizationOperation
                {
                    OperationId = "feat|" + def.TargetIdentifier,
                    Category = CustomizationCategory.Package,
                    OperationType = CustomizationOperationType.DisableOptionalFeature,
                    DisplayName = subject.DisplayName,
                    Description = item.ReasonKey,
                    TargetIdentifier = def.TargetIdentifier,
                    IsSelected = selected,
                    Risk = risk,
                    ActionKind = def.Action,
                    Mechanism = def.Mechanism,
                    Scope = def.Scope,
                    ReversalKey = def.ReversalKey,
                    ExecutionOrder = 0,
                };
                feat.AddSourceDefinition(def.Id);
                return new[] { feat };
            }

            var ops = new List<CustomizationOperation>();
            var index = 0;
            foreach (var t in def.RegistryTargets)
            {
                var reg = new CustomizationOperation
                {
                    OperationId = $"opt|{def.Id}|{index}",
                    Category = MapCategoryForTab(def.Tab),
                    OperationType = CustomizationOperationType.SetOfflineRegistryValue,
                    DisplayName = subject.DisplayName,
                    Description = item.ReasonKey,
                    RegistryHive = t.Hive,
                    RegistryKeyPath = t.KeyPath,
                    RegistryValueName = t.ValueName,
                    RegistryValueKind = t.ValueKind,
                    RegistryValueData = t.RecommendedData,
                    RestoreValueData = t.RestoreData,
                    IsSelected = selected,
                    Risk = risk,
                    ActionKind = def.Action,
                    Mechanism = def.Mechanism,
                    Scope = def.Scope,
                    ReversalKey = def.ReversalKey,
                    ExecutionOrder = index,
                };
                reg.AddSourceDefinition(def.Id);
                ops.Add(reg);
                index++;
            }

            return ops;
        }

        // Component-layer item (deep / curated inventory object).
        var identity = !string.IsNullOrWhiteSpace(subject.RawIdentity) ? subject.RawIdentity : subject.LogicalId;
        var (opType, category) = item.OperationType switch
        {
            ExecutionOperationType.AppX => (CustomizationOperationType.RemoveProvisionedAppx, CustomizationCategory.App),
            ExecutionOperationType.OptionalFeature => (CustomizationOperationType.DisableOptionalFeature, CustomizationCategory.Package),
            ExecutionOperationType.Capability => (CustomizationOperationType.RemoveCapability, CustomizationCategory.Package),
            ExecutionOperationType.CbsPackage => (CustomizationOperationType.RemovePackage, CustomizationCategory.Package),
            _ => (CustomizationOperationType.SetOfflineRegistryValue, CustomizationCategory.System),
        };
        var opIdPrefix = item.OperationType switch
        {
            ExecutionOperationType.AppX => "appx",
            ExecutionOperationType.OptionalFeature => "feat",
            ExecutionOperationType.Capability => "cap",
            ExecutionOperationType.CbsPackage => "pkg",
            _ => "opt",
        };
        var component = new CustomizationOperation
        {
            OperationId = $"{opIdPrefix}|{identity}",
            Category = category,
            OperationType = opType,
            DisplayName = subject.DisplayName,
            Description = item.ReasonKey,
            TargetIdentifier = identity,
            IsSelected = selected,
            Risk = risk,
            ActionKind = OptimizationAction.Remove,
            ExecutionOrder = 0,
        };
        component.AddSourceDefinition(subject.LogicalId);
        return new[] { component };
    }

    private static CustomizationCategory MapCategoryForTab(OptimizationTab tab) => tab switch
    {
        OptimizationTab.Privacy => CustomizationCategory.Privacy,
        OptimizationTab.Personalization => CustomizationCategory.Personalization,
        OptimizationTab.System => CustomizationCategory.System,
        OptimizationTab.Services => CustomizationCategory.Service,
        OptimizationTab.WindowsComponents => CustomizationCategory.Package,
        OptimizationTab.Apps => CustomizationCategory.App,
        _ => CustomizationCategory.System,
    };

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

    /// <summary>
    /// Maps a GamingExtra to its ExtraScenario profile id (Stage 15.2b). The
    /// planner includes those profiles in SelectedProfiles so their data-driven
    /// Keep overrides (Xbox services, virtualization stack, printing, RDP, input)
    /// actually reach the engine — extras must override profile minimalism.
    /// </summary>
    private static IReadOnlyList<ProfileDefinition> SelectExtraProfiles(
        IReadOnlyList<ProfileDefinition> allProfiles, IReadOnlySet<GamingExtra> extras)
    {
        if (extras.Count == 0)
        {
            return Array.Empty<ProfileDefinition>();
        }

        var result = new List<ProfileDefinition>();
        foreach (var extra in extras)
        {
            var id = extra switch
            {
                GamingExtra.XboxGamePass => "XboxGamePass",
                GamingExtra.WslDocker => "WslDocker",
                GamingExtra.PrintScan => "PrintingScanning",
                GamingExtra.TouchPen => "TouchPen",
                GamingExtra.RemoteDesktop => "RemoteDesktop",
                _ => null,
            };
            if (id is null)
            {
                continue;
            }

            var profile = allProfiles.FirstOrDefault(p =>
                p.Kind == ProfileKind.ExtraScenario && string.Equals(p.Id, id, StringComparison.Ordinal));
            if (profile is not null && !result.Contains(profile))
            {
                result.Add(profile);
            }
        }

        return result;
    }

    /// <summary>
    /// Synthesizes a knowledge view for a curated-only inventory object so the
    /// gaming policy dispatches uniformly (Stage 15.2b). Curated definitions carry
    /// recommendation/risk (no function/tag), so the policy mostly returns
    /// NoOpinion for them — the real difference comes from the profile intent
    /// layer (overrides). This closes the "curated bypasses policy" hole.
    /// </summary>
    private static DeepComponentKnowledge? SynthesizeFromCurated(
        ComponentDefinition? curated, ComponentCategory category)
    {
        if (curated is null)
        {
            return null;
        }

        return new DeepComponentKnowledge
        {
            CanonicalId = curated.Id,
            DisplayNameFallback = curated.Id,
            Function = ComponentFunctionCategory.Unknown,
            Risk = MapCuratedRisk(curated.Risk),
            Recommendation = MapCuratedRecommendation(curated.Recommendation),
            Protection = ComponentProtectionLevel.None,
            ProfileTag = ComponentProfileTag.None,
            Confidence = ClassificationConfidence.Curated,
            DependencyTags = Array.Empty<string>(),
        };
    }

    private static ComponentRiskLevel MapCuratedRisk(RiskLevel risk) => risk switch
    {
        RiskLevel.Low => ComponentRiskLevel.Low,
        RiskLevel.Medium => ComponentRiskLevel.Moderate,
        RiskLevel.High => ComponentRiskLevel.High,
        RiskLevel.Critical => ComponentRiskLevel.Critical,
        _ => ComponentRiskLevel.Unknown,
    };

    private static ComponentRecommendationKind MapCuratedRecommendation(RecommendationLevel rec) => rec switch
    {
        RecommendationLevel.UsuallyKeep => ComponentRecommendationKind.RecommendedKeep,
        RecommendationLevel.NeverRemove => ComponentRecommendationKind.RequiredKeep,
        RecommendationLevel.OptionalRemove => ComponentRecommendationKind.OptionalRemove,
        RecommendationLevel.RecommendedRemove => ComponentRecommendationKind.RecommendedRemove,
        _ => ComponentRecommendationKind.Unknown,
    };
}
