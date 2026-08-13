using System;
using System.Collections.Generic;
using System.Linq;
using WinForge.App.Mvvm;
using WinForge.App.Services;
using WinForge.Core.Models;
using WinForge.Core.Profiles;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// A knowledge-enhanced, selectable component row for the Customize "Component
/// Knowledge" tab. It wraps a curated <see cref="ComponentInventoryEntry"/> and
/// exposes the human-facing name, category, recommendation/risk badges, a compact
/// hover card, a full detail view, and (when removable) a plan-backed selection.
///
/// <para>Selection is NON-destructive: it only toggles declarative plan operations
/// through <see cref="PlanSync"/> — exactly the same flow the existing Components
/// page uses. No DISM is called here. Protected / Unknown / NeverRemove items are
/// NOT selectable and explain why.</para>
///
/// <para>Stage 11.4 (ADR-057..060): the row also implements
/// <see cref="IRecommendationSubject"/> — the profile engine computes an EFFECTIVE
/// recommendation (separately from the definition default, which is never
/// mutated), exposes deterministic localized reasons, and marks manual toggles as
/// user overrides so recalculation never silently overwrites an explicit choice.</para>
/// </summary>
public sealed class ComponentKnowledgeItem : ViewModelBase, IRecommendationSubject, IGamingEvaluationSubject
{
    private readonly ILocalizationService _loc;
    private readonly IAppState _appState;
    private readonly ComponentKnowledgeViewModel _parent;
    private readonly RecommendationContextService? _ctx;
    private readonly WinForge.Core.ComponentIntelligence.DeepComponentKnowledge? _deep;

    public ComponentKnowledgeItem(
        ComponentInventoryEntry entry,
        ILocalizationService loc,
        IAppState appState,
        ComponentKnowledgeViewModel parent,
        RecommendationContextService? ctx = null,
        WinForge.Core.ComponentIntelligence.DeepComponentKnowledge? deep = null)
    {
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        _ctx = ctx;
        _deep = deep;
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        Effective = EffectiveRecommendation.FromDefault(RecommendationLevel);
    }

    /// <summary>Phase 14 deep classification knowledge (null for curated-only rows).</summary>
    public bool HasDeepKnowledge => _deep is not null;

    /// <summary>Phase 14 deep classification knowledge — the gaming pipeline's input (ADR-088).</summary>
    public WinForge.Core.ComponentIntelligence.DeepComponentKnowledge? DeepKnowledge
        => _parent.KnowledgeOf(this);

    /// <summary>Raw deep classification attached at construction (internal; display uses this).</summary>
    internal WinForge.Core.ComponentIntelligence.DeepComponentKnowledge? DeepKnowledgeRaw => _deep;

    /// <summary>Raw discovery source of this row (AppX / Capability / OptionalFeature / …).</summary>
    public ComponentCategory SourceCategory =>
        Entry.Definition?.Category ??
        Entry.RepresentativeRaw?.Category ??
        ComponentCategory.Unknown;

    /// <summary>Raw Windows identity of the row (representative), for traceability.</summary>
    public string RawIdentity =>
        Entry.RepresentativeRaw?.RawIdentity ?? Entry.LogicalId;

    /// <summary>Deep classification display name (fallback-safe).</summary>
    public string DeepDisplayName
    {
        get
        {
            if (_deep is null)
            {
                return string.Empty;
            }

            var name = _loc[_deep.DisplayNameKey];
            if (!string.IsNullOrEmpty(name) && name != _deep.DisplayNameKey)
            {
                return name;
            }

            return string.IsNullOrWhiteSpace(_deep.DisplayNameFallback)
                ? _deep.CanonicalId
                : _deep.DisplayNameFallback;
        }
    }

    /// <summary>Deep classification purpose text (fallback-safe).</summary>
    public string DeepPurposeText
    {
        get
        {
            if (_deep is null)
            {
                return string.Empty;
            }

            var s = _loc[_deep.DescriptionKey];
            if (!string.IsNullOrEmpty(s) && s != _deep.DescriptionKey)
            {
                return s;
            }

            return string.IsNullOrWhiteSpace(_deep.DescriptionFallback)
                ? _loc["Component.NotConfirmed"]
                : _deep.DescriptionFallback;
        }
    }

    /// <summary>Deep classification risk caption (localized; e.g. 高/中/低/严重).</summary>
    public string DeepRiskText
        => _deep is null ? string.Empty : _loc["Deep.Risk." + _deep.Risk];

    /// <summary>Deep classification recommendation caption (localized).</summary>
    public string DeepRecommendationText
        => _deep is null ? string.Empty : _loc["Deep.Rec." + _deep.Recommendation];

    /// <summary>Deep classification function category caption (localized).</summary>
    public string DeepFunctionText
        => _deep is null ? string.Empty : _loc["Deep.Function." + _deep.Function];

    public ComponentInventoryEntry Entry { get; }

    // ---- Stage 11.4 — recommendation subject ----

    public string LogicalId => Entry.LogicalId;

    /// <summary>Apps tab = AppX rows; Windows Components = features/capabilities.</summary>
    public OptimizationTab Tab =>
        (Entry.Definition?.Category ?? Entry.RepresentativeRaw?.Category ?? ComponentCategory.Unknown) == ComponentCategory.AppX
            ? OptimizationTab.Apps
            : OptimizationTab.WindowsComponents;

    public bool IsPresent => Entry.RawItems.Count > 0;

    public bool WasOverridden => Effective.WasOverridden;

    public bool HasConflict => Effective.HasConflict;

    public EffectiveRecommendation Effective { get; private set; }

    public void RefreshRecommendation(RecommendationContextService context)
        => ApplyRecommendation(context.Evaluate(ToRecommendationInput()));

    public void ApplyRecommendation(EffectiveRecommendation effective)
    {
        Effective = effective ?? throw new ArgumentNullException(nameof(effective));
        OnPropertyChanged(nameof(Effective));
        OnPropertyChanged(nameof(RecommendationCaption));
        OnPropertyChanged(nameof(WasOverridden));
        OnPropertyChanged(nameof(HasConflict));
        OnPropertyChanged(nameof(ReasonText));
        OnPropertyChanged(nameof(SelectionOriginText));
        OnPropertyChanged(nameof(ConflictText));
        OnPropertyChanged(nameof(AdvisedByText));
        OnPropertyChanged(nameof(WhyPoints));
    }

    private RecommendationInput ToRecommendationInput() => new()
    {
        LogicalId = LogicalId,
        Action = Entry.Definition?.Action ?? OptimizationAction.Remove,
        DefaultRecommendation = RecommendationLevel,
        Risk = RiskLevel,
        Removal = Entry.Definition?.Removal ?? RemovalSupport.Unknown,
        IsPresent = IsPresent,
        IsApplySupported = IsApplySupported,
        Dependencies = Entry.Definition?.Dependencies ?? new List<ComponentDependency>(),
        // Phase 14.3 (ADR-088/090): post-safety-gate knowledge-driven gaming
        // decision — null when no gaming profile is active (engine falls through
        // to the legacy tiers unchanged).
        GamingDecision = _parent.GetGamingDecision(this),
    };

    /// <summary>Deterministic localized "why" for the effective recommendation (Part F).</summary>
    public string ReasonText
    {
        get
        {
            var resolved = new List<string>();
            foreach (var key in Effective.ReasonKeys)
            {
                var text = _loc[key];
                if (!string.IsNullOrEmpty(text) && text != key)
                {
                    resolved.Add(text);
                }
            }

            return resolved.Count > 0 ? string.Join("; ", resolved) : string.Empty;
        }
    }

    /// <summary>
    /// Part 13 — "配置建议: 游戏优先 → 建议保留": which profile drove the decision
    /// and the resulting action-aware caption. Empty when the default won.
    /// </summary>
    public string AdvisedByText
    {
        get
        {
            if (!Effective.WasProfileDriven || Effective.AdvisedByProfileIds.Count == 0)
            {
                return string.Empty;
            }

            var names = string.Join(" + ", Effective.AdvisedByProfileIds.Select(id => _loc["Profile." + id + ".DisplayName"]));
            return $"{_loc["Profile.AdvisedBy"]}: {names} → {RecommendationCaption}";
        }
    }

    public string ConflictText
    {
        get
        {
            if (!Effective.HasConflict)
            {
                return string.Empty;
            }

            var parts = Effective.Conflicts.Select(c =>
            {
                var keep = _loc["Profile." + c.KeepProfileId + ".DisplayName"];
                var trim = _loc["Profile." + c.TrimProfileId + ".DisplayName"];
                return $"{_loc["Profile.Conflict.Summary"]} ({trim} → {keep})";
            });
            return string.Join("; ", parts);
        }
    }

    /// <summary>
    /// Final flow row feedback: why is this row currently selected (or not)?
    /// "由「X」自动选择" when the active profile auto-applied it, "手动选择" when
    /// the user explicitly toggled it, empty when untouched.
    /// </summary>
    public string SelectionOriginText
    {
        get
        {
            if (_ctx is null)
            {
                return string.Empty;
            }

            if (_ctx.IsUserOverridden(LogicalId) || Effective.WasOverridden)
            {
                return _loc["Profile.Origin.Manual"];
            }

            if (_ctx.IsProfileManaged(LogicalId))
            {
                var primary = _ctx.SelectedProfiles.FirstOrDefault(p => p.Kind == WinForge.Core.Profiles.ProfileKind.Primary);
                return primary is null
                    ? string.Empty
                    : string.Format(_loc["Profile.Origin.Auto"], _loc[primary.DisplayNameKey]);
            }

            return string.Empty;
        }
    }

    public bool IsCurated => Entry.Definition is not null;

    public RecommendationLevel RecommendationLevel => Entry.Definition?.Recommendation ?? RecommendationLevel.Unknown;

    public RiskLevel RiskLevel => Entry.Definition?.Risk ?? RiskLevel.Unknown;

    // ---- Human name + category ----
    public string DisplayName
    {
        get
        {
            if (_deep is not null)
            {
                var deepName = DeepDisplayName;
                if (!string.IsNullOrWhiteSpace(deepName))
                {
                    return deepName;
                }
            }

            if (Entry.Definition is not null)
            {
                var name = _loc[Entry.Definition.DisplayNameKey];
                if (!string.IsNullOrEmpty(name) && name != Entry.Definition.DisplayNameKey)
                {
                    return name;
                }
            }

            var raw = Entry.RepresentativeRaw;
            if (raw is not null && !string.IsNullOrEmpty(raw.DisplayName))
            {
                return raw.DisplayName;
            }

            return raw is not null && !string.IsNullOrEmpty(raw.RawIdentity)
                ? raw.RawIdentity
                : _loc["Component.Unknown"];
        }
    }

    public string CategoryCaption => Entry.Definition is not null
        ? _loc["Category." + Entry.Definition.Category]
        : (Entry.RepresentativeRaw is null ? _loc["Component.Unknown"] : _loc["Category." + Entry.RepresentativeRaw.Category]);

    public string ShortPurpose
    {
        get
        {
            if (_deep is not null)
            {
                var deepPurpose = DeepPurposeText;
                if (!string.IsNullOrWhiteSpace(deepPurpose))
                {
                    return deepPurpose;
                }
            }

            if (Entry.Definition is null)
            {
                return _loc["Component.NotConfirmed"];
            }

            var s = _loc[Entry.Definition.ShortDescriptionKey];
            return !string.IsNullOrEmpty(s) && s != Entry.Definition.ShortDescriptionKey ? s : _loc["Component.Unknown"];
        }
    }

    /// <summary>
    /// Recommendation caption. With no profile effect this is the definition's own
    /// curated caption (Stage 11.2 behavior preserved). When the profile engine
    /// changed the outcome (Part L), the caption follows the EFFECTIVE level and
    /// the item's action type — AppX rows say 推荐移除, feature rows say 推荐禁用.
    /// </summary>
    public string RecommendationCaption
    {
        get
        {
            if (_deep is not null && !Effective.WasProfileDriven)
            {
                var deepRec = DeepRecommendationText;
                if (!string.IsNullOrWhiteSpace(deepRec))
                {
                    return deepRec;
                }
            }

            if (!Effective.WasProfileDriven)
            {
                return _loc["Recommendation." + RecommendationLevel];
            }

            var mapped = Effective.Level.ToRecommendationLevel();
            var action = Entry.Definition?.Action ?? OptimizationAction.Remove;
            return action == OptimizationAction.Remove
                ? _loc["Recommendation." + mapped]
                : _loc[$"Opt.Recommendation.{action}.{mapped}"];
        }
    }

    public string RiskCaption => _loc["Risk." + RiskLevel];

    // ---- Selectability ----
    /// <summary>
    /// Execution eligibility (Stage 11.3 defect fix): a row is VISIBLE whenever it
    /// is a reviewed, present component, but it is only SELECTABLE when the
    /// mechanism can actually be applied to the offline image. Display eligibility
    /// and execution eligibility are deliberately separate — e.g. Capability rows
    /// stay visible for knowledge but are blocked ("当前版本暂不支持应用") because the
    /// capability execution allowlist is intentionally empty this tranche. This
    /// prevents selecting an operation that would silently become Skipped at Apply.
    /// </summary>
    public bool IsApplySupported
    {
        get
        {
            foreach (var raw in Entry.RawItems)
            {
                switch (raw.Category)
                {
                    case ComponentCategory.OptionalFeature:
                        if (!FeatureConfigPolicy.IsFeatureAllowed(raw.RawIdentity))
                        {
                            return false;
                        }

                        break;
                    case ComponentCategory.Capability:
                        if (!FeatureConfigPolicy.IsCapabilityAllowed(raw.RawIdentity))
                        {
                            return false;
                        }

                        break;
                }
            }

            return true;
        }
    }

    /// <summary>True when the item may be selected into the plan (removable, not Protected/Unknown, apply-supported).</summary>
    public bool IsSelectable =>
        IsApplySupported &&
        Entry.Definition is not null &&
        Entry.Definition.Removal != RemovalSupport.Blocked &&
        Entry.Definition.Recommendation != RecommendationLevel.NeverRemove &&
        Entry.Definition.Risk != RiskLevel.Critical;

    /// <summary>Localized reason an item cannot be selected (empty when selectable).</summary>
    public string BlockReason =>
        Entry.Definition is null
            ? _loc["Component.NotConfirmed"]
            : !IsApplySupported
                ? _loc["Opt.ApplyUnsupported"]
                : Entry.Definition.Recommendation == RecommendationLevel.NeverRemove
                    ? _loc["Component.Blocked"]
                    : Entry.Definition.Risk == RiskLevel.Critical
                        ? _loc["Component.Blocked"]
                        : Entry.Definition.Removal == RemovalSupport.Blocked
                            ? _loc["Component.Blocked"]
                            : string.Empty;

    private bool _isSelected;
    private bool _isActiveDetail;

    /// <summary>
    /// True when this row's component is the one currently shown in the detail panel.
    /// Drives the "currently being inspected" row highlight and is intentionally
    /// independent of <see cref="IsSelected"/> (removal selection). The parent
    /// <see cref="ComponentKnowledgeViewModel"/> refreshes this flag whenever
    /// <c>ActiveDetail</c> changes.
    /// </summary>
    public bool IsActiveDetail
    {
        get => _isActiveDetail;
        set => SetField(ref _isActiveDetail, value);
    }

    /// <summary>Plan-backed selection. Setting it toggles declarative plan operations (no DISM).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!IsSelectable)
            {
                return;
            }

            if (!SetField(ref _isSelected, value))
            {
                return;
            }

            SyncPlan(value);
            // Part K — a manual toggle is an explicit user choice; subsequent
            // profile recalculation / reapply must not silently overwrite it.
            _ctx?.SetUserOverride(LogicalId);
        }
    }

    /// <summary>
    /// Programmatic selection used by "adopt recommendations" / "reapply" (Part I/J).
    /// Like <see cref="IsSelected"/> it only toggles declarative plan operations,
    /// but it NEVER marks a user override — the adopt command may be re-run safely.
    /// </summary>
    public void SetSelectedForAdoption(bool selected)
    {
        if (!IsSelectable)
        {
            return;
        }

        if (!SetField(ref _isSelected, selected))
        {
            return;
        }

        SyncPlan(selected);
    }

    // ---- Hover card fields (compact) ----
    public string KeepIfText => JoinOptional(Entry.Definition?.KeepIf);

    public string RemoveIfText => JoinOptional(Entry.Definition?.RemoveIf);

    public string ImpactText => JoinOptional(Entry.Definition?.KnownImpact);

    public string RestoreCaption => Entry.Definition is null
        ? _loc["Component.Unknown"]
        : _loc["Restore." + Entry.Definition.Restore];

    // ---- Detail fields (full) ----
    public IReadOnlyList<string> Scenarios =>
        Entry.Definition is null || Entry.Definition.UserScenarios.Count == 0
            ? new[] { _loc["Component.Unknown"] }
            : Entry.Definition.UserScenarios.Select(s => _loc["ComponentScenario." + s]).ToList();

    public IReadOnlyList<string> Dependencies =>
        Entry.Definition is null || Entry.Definition.Dependencies.Count == 0
            ? new[] { _loc["Component.Unknown"] }
            : Entry.Definition.Dependencies.Select(FormatDependency).ToList();

    public IReadOnlyList<string> RawIdentities =>
        Entry.RawItems.Count > 0
            ? Entry.RawItems.Select(r => r.RawIdentity).ToList()
            : Entry.Definition?.TechnicalTargets.Where(t => t.Category == ComponentCategory.AppX)
                .Select(t => t.Pattern).ToList() ?? new List<string>();

    /// <summary>Trusted (official / WinForge / empirical) evidence source captions.</summary>
    public IReadOnlyList<string> OfficialEvidence
    {
        get
        {
            var list = new List<string>();
            if (Entry.Definition is null)
            {
                return list;
            }

            foreach (var claim in Entry.Definition.Provenance)
            {
                foreach (var s in claim.Sources)
                {
                    if (s.SourceType != KnowledgeSourceType.CommunityProject)
                    {
                        list.Add($"{_loc["KnowledgeSource." + s.SourceType]} ({_loc["Confidence." + s.Confidence]})");
                    }
                }
            }

            return list.Distinct().ToList();
        }
    }

    /// <summary>Community evidence source captions (rendered distinctly from official facts).</summary>
    public IReadOnlyList<string> CommunityEvidence
    {
        get
        {
            var list = new List<string>();
            if (Entry.Definition is null)
            {
                return list;
            }

            foreach (var claim in Entry.Definition.Provenance)
            {
                foreach (var s in claim.Sources)
                {
                    if (s.SourceType == KnowledgeSourceType.CommunityProject)
                    {
                        list.Add($"{_loc["KnowledgeSource." + s.SourceType]} ({_loc["Confidence." + s.Confidence]})");
                    }
                }
            }

            return list.Distinct().ToList();
        }
    }

    /// <summary>Deterministic, curated "why" bullet points (Part J). Never runtime AI prose.</summary>
    public IReadOnlyList<string> WhyPoints
    {
        get
        {
            var pts = new List<string>
            {
                _loc["Component.Recommendation"] + ": " + RecommendationCaption,
            };
            if (Effective.WasProfileDriven || Effective.HasConflict)
            {
                pts.Add(_loc["Profile.Why"] + ": " + ReasonText);
            }

            if (!string.IsNullOrEmpty(AdvisedByText))
            {
                pts.Add(AdvisedByText);
            }

            if (Effective.WasOverridden)
            {
                pts.Add(_loc["Profile.Reason.UserOverride"]);
            }

            var keep = JoinOptional(Entry.Definition?.KeepIf);
            var remove = JoinOptional(Entry.Definition?.RemoveIf);
            if (!string.IsNullOrEmpty(keep))
            {
                pts.Add(_loc["Component.KeepIf"] + ": " + keep);
            }

            if (!string.IsNullOrEmpty(remove))
            {
                pts.Add(_loc["Component.RemoveIf"] + ": " + remove);
            }

            return pts;
        }
    }

    /// <summary>Opens the full detail view WITHOUT changing selection.</summary>
    public void ShowDetail() => _parent.ActiveDetail = this;

    // ---- Helpers ----
    private string JoinOptional(IReadOnlyList<string>? keys)
    {
        if (keys is null || keys.Count == 0)
        {
            return string.Empty;
        }

        var resolved = keys
            .Select(k => _loc[k])
            .Where(v => !string.IsNullOrEmpty(v) && !v.StartsWith("Comp."))
            .ToList();
        return resolved.Count > 0 ? string.Join("; ", resolved) : string.Empty;
    }

    private string FormatDependency(ComponentDependency dep)
    {
        var relation = _loc["Dependency." + dep.Relation];
        var targetKey = "Comp." + dep.ToId + ".DisplayName";
        var target = _loc[targetKey];
        target = string.IsNullOrEmpty(target) || target == targetKey ? dep.ToId : target;
        var reason = string.IsNullOrEmpty(dep.Reason) ? string.Empty : " — " + dep.Reason;
        return $"{relation}: {target}{reason}";
    }

    /// <summary>
    /// The concrete plan operations this component maps to. Stage 11.3 (ADR-051):
    /// the operation type follows the raw category — AppX → RemoveProvisionedAppx,
    /// OptionalFeature → DisableOptionalFeature, Capability → RemoveCapability —
    /// so the Windows Components tab builds strongly typed FEATURE operations
    /// instead of pretending every change is an app removal.
    /// </summary>
    private IReadOnlyList<(string OpId, ComponentCategory Category, string Identity)> TargetOperations()
    {
        var ops = new List<(string, ComponentCategory, string)>();
        if (Entry.RawItems.Count > 0)
        {
            foreach (var raw in Entry.RawItems)
            {
                var id = raw.Category switch
                {
                    ComponentCategory.AppX => "appx|" + raw.RawIdentity,
                    ComponentCategory.OptionalFeature => "feat|" + raw.RawIdentity,
                    ComponentCategory.Capability => "cap|" + raw.RawIdentity,
                    _ => null,
                };
                if (id is not null)
                {
                    ops.Add((id, raw.Category, raw.RawIdentity));
                }
            }
        }
        else if (Entry.Definition is not null)
        {
            // Catalog-only: use the AppX technical-target patterns as best-effort ids.
            foreach (var t in Entry.Definition.TechnicalTargets.Where(t => t.Category == ComponentCategory.AppX))
            {
                ops.Add(("kcomp|" + Entry.Definition.Id + "|" + t.Pattern, ComponentCategory.AppX, t.Pattern));
            }
        }

        return ops;
    }

    private IReadOnlyList<string> TargetOperationIds()
        => TargetOperations().Select(o => o.OpId).ToList();

    private void SyncPlan(bool selected)
    {
        foreach (var (opId, category, identity) in TargetOperations())
        {
            var (operationType, opCategory, risk) = category switch
            {
                ComponentCategory.AppX => (CustomizationOperationType.RemoveProvisionedAppx, CustomizationCategory.App,
                    RiskLevel == RiskLevel.Low ? RiskClass.Safe : RiskClass.Removable),
                ComponentCategory.OptionalFeature => (CustomizationOperationType.DisableOptionalFeature, CustomizationCategory.Package,
                    RiskClass.Removable),
                ComponentCategory.Capability => (CustomizationOperationType.RemoveCapability, CustomizationCategory.Package,
                    RiskClass.Removable),
                _ => (CustomizationOperationType.RemoveProvisionedAppx, CustomizationCategory.App, RiskClass.Removable),
            };

            PlanSync.Toggle(_appState, opId, selected, () => new CustomizationOperation
            {
                OperationId = opId,
                Category = opCategory,
                OperationType = operationType,
                DisplayName = DisplayName,
                Description = $"Change for {DisplayName} ({Entry.Definition?.Id}).",
                TargetIdentifier = identity,
                Risk = risk,
                ExecutionOrder = 0,
                ActionKind = Entry.Definition?.Action,
                Mechanism = Entry.Definition?.Mechanism,
                Scope = Entry.Definition?.Scope,
                ReversalKey = Entry.Definition?.ReversalKey,
                RestoreValueData = Entry.Definition?.RestoreValueData,
            });
        }

        _parent.RefreshSelectedTotal();
    }

    internal void RefreshSelectionFromPlan()
    {
        var ids = TargetOperationIds();
        var selected = ids.Count > 0 && ids.All(id =>
            _appState.CurrentCustomizationPlan?.Operations.Any(o => o.OperationId == id && o.IsSelected) ?? false);
        SetField(ref _isSelected, selected);
    }
}
