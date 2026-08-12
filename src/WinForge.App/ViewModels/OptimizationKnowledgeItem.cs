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
/// A knowledge-backed, selectable row for the non-AppX Customize tabs
/// (Services / Privacy / System / Personalization — Stage 11.3). It wraps a
/// curated <see cref="OptimizationDefinition"/> and exposes the human name,
/// purpose, action-appropriate recommendation caption (Part N: never say
/// "remove" for a disable/configure), risk, scope, revert contract, and a
/// plan-backed selection that builds the STRONGLY TYPED operation for the
/// entry's mechanism (registry policy → SetOfflineRegistryValue; service →
/// ConfigureOfflineService).
///
/// <para>Selection is NON-destructive — it only toggles declarative plan
/// operations through <see cref="PlanSync"/>. Inapplicable (build/edition-gated),
/// blocked, core, or LeaveDefault entries are not selectable and explain why.
/// The host user's registry is never touched: user-scope entries target the
/// offline Default User hive (<c>DEFAULT_USER</c>), machine-scope entries target
/// the offline SOFTWARE/SYSTEM hives (ADR-052).</para>
///
/// <para>Stage 11.4 (ADR-057..060): the row also implements
/// <see cref="IRecommendationSubject"/> — the profile engine computes an EFFECTIVE
/// recommendation (separately from the definition default), exposes deterministic
/// localized reasons, and marks manual toggles as user overrides so recalculation
/// never silently overwrites an explicit choice.</para>
/// </summary>
public sealed class OptimizationKnowledgeItem : ViewModelBase, IRecommendationSubject
{
    private readonly ILocalizationService _loc;
    private readonly IAppState _appState;
    private readonly OptimizationKnowledgeViewModel _parent;
    private readonly RecommendationContextService? _ctx;

    private bool _isSelected;
    private bool _isActiveDetail;

    public OptimizationKnowledgeItem(
        OptimizationDefinition definition,
        ILocalizationService loc,
        IAppState appState,
        OptimizationKnowledgeViewModel parent,
        RecommendationContextService? ctx = null)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        _ctx = ctx;
        Effective = EffectiveRecommendation.FromDefault(Definition.Recommendation);
    }

    public OptimizationDefinition Definition { get; }

    // ---- Stage 11.4 — recommendation subject ----

    public string LogicalId => Definition.Id;

    /// <summary>Which Customize tab this catalog entry lives in (Part 11 breakdown).</summary>
    public OptimizationTab Tab => Definition.Tab;

    /// <summary>Catalog entries are "present" when they apply to the selected image.</summary>
    public bool IsPresent => IsApplicable;

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
        OnPropertyChanged(nameof(ConflictText));
        OnPropertyChanged(nameof(AdvisedByText));
        OnPropertyChanged(nameof(WhyPoints));
    }

    private RecommendationInput ToRecommendationInput() => new()
    {
        LogicalId = LogicalId,
        Action = Definition.Action,
        DefaultRecommendation = Definition.Recommendation,
        Risk = Definition.Risk,
        Removal = Definition.Removal,
        IsPresent = IsApplicable,
        IsApplySupported = IsSelectable,
        Dependencies = Definition.Dependencies,
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
    /// Recommendation caption uses the entry's ACTION so the wording fits the
    /// operation (Part N): a Disable entry says "推荐关闭", a Configure entry says
    /// "推荐开启", a Service entry says "推荐调整" — never a removal word. With a
    /// profile effect the caption follows the EFFECTIVE level (Part L); without
    /// one it is the definition's own caption (Stage 11.3 behavior preserved).
    /// </summary>
    public string RecommendationCaption
    {
        get
        {
            var rec = Effective.WasProfileDriven
                ? Effective.Level.ToRecommendationLevel()
                : Definition.Recommendation;
            return _loc[$"Opt.Recommendation.{Definition.Action}.{rec}"];
        }
    }

    // ---- Human identity ----

    public string DisplayName => _loc[Definition.DisplayNameKey];

    public string ShortPurpose => _loc[Definition.ShortDescriptionKey];

    public string RiskCaption => _loc["Risk." + Definition.Risk];

    public string ActionCaption => _loc["Opt.Action." + Definition.Action];

    public string ScopeCaption => _loc["Opt.Scope." + Definition.Scope];

    public string RestoreCaption => _loc["Restore." + Definition.Restore];

    public string ReversalCaption => string.IsNullOrWhiteSpace(Definition.ReversalKey)
        ? _loc["Plan.Reversal.Generic"]
        : _loc[Definition.ReversalKey!];

    public string ProposedStartCaption => Definition.ProposedStartType is null
        ? string.Empty
        : _loc["Opt.StartType." + Definition.ProposedStartType];

    // ---- Applicability / selectability ----

    /// <summary>
    /// Build/edition gate (Part F "some policies vary by edition/build"). An entry
    /// with compatibility rules is only applicable when the selected image is
    /// known to satisfy them. When the image metadata is not yet known the entry
    /// stays applicable (nothing to disprove).
    /// </summary>
    public bool IsApplicable
    {
        get
        {
            var rules = Definition.CompatibilityRules;
            if (rules.Count == 0)
            {
                return true;
            }

            var workspace = _appState.CurrentImageWorkspace;
            if (workspace is null)
            {
                return true;
            }

            foreach (var rule in rules)
            {
                if (!string.IsNullOrWhiteSpace(rule.SupportedBuildMin) &&
                    int.TryParse(workspace.Build, out var build) &&
                    int.TryParse(rule.SupportedBuildMin, out var min) && build < min)
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(rule.Edition) &&
                    !string.IsNullOrWhiteSpace(workspace.SelectedEditionName) &&
                    !string.Equals(workspace.SelectedEditionName, rule.Edition, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// True when the row may be selected into the plan. Blocked / NeverRemove /
    /// core / LeaveDefault / not-applicable / post-install-only rows are not
    /// selectable and expose a reason via <see cref="BlockReason"/>.
    /// </summary>
    public bool IsSelectable =>
        IsApplicable &&
        Definition.Removal != RemovalSupport.Blocked &&
        Definition.Recommendation != RecommendationLevel.NeverRemove &&
        Definition.Scope is not (OptimizationScope.PostInstallRequired or OptimizationScope.UnsupportedOffline) &&
        (Definition.Mechanism != OptimizationMechanism.ServiceStartup || Definition.ProposedStartType is not null);

    /// <summary>Localized reason an item cannot be selected (empty when selectable).</summary>
    public string BlockReason
    {
        get
        {
            if (!IsApplicable)
            {
                return _loc["Opt.NotApplicable"];
            }

            if (Definition.Mechanism == OptimizationMechanism.ServiceStartup &&
                Definition.ProposedStartType is null)
            {
                return _loc["Opt.NoChangeRecommended"];
            }

            if (Definition.Recommendation == RecommendationLevel.NeverRemove)
            {
                return Definition.Id == "RpcSs" ? _loc["Opt.CoreNeverChange"] : _loc["Opt.Blocked"];
            }

            if (Definition.Scope is OptimizationScope.PostInstallRequired or OptimizationScope.UnsupportedOffline)
            {
                return _loc["Opt.NotApplicable"];
            }

            return Definition.Removal == RemovalSupport.Blocked ? _loc["Opt.Blocked"] : string.Empty;
        }
    }

    // ---- Selection (plan-backed, non-destructive) ----

    /// <summary>True when this row is the one shown in the detail panel (independent of selection).</summary>
    public bool IsActiveDetail
    {
        get => _isActiveDetail;
        set => SetField(ref _isActiveDetail, value);
    }

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

    public void ShowDetail() => _parent.ActiveDetail = this;

    // ---- Detail / hover fields ----

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

            if (Definition.Mechanism == OptimizationMechanism.ServiceStartup && Definition.ProposedStartType is not null)
            {
                pts.Add(_loc["Opt.Detail.ProposedStart"] + ": " + ProposedStartCaption);
            }

            return pts;
        }
    }

    /// <summary>Registry paths / service names targeted by this entry (raw, collapsed section).</summary>
    public IReadOnlyList<string> TechnicalTargets
    {
        get
        {
            if (Definition.Mechanism == OptimizationMechanism.ServiceStartup && !string.IsNullOrWhiteSpace(Definition.ServiceName))
            {
                return new[] { Definition.ServiceName! };
            }

            var list = new List<string>();
            foreach (var t in Definition.RegistryTargets)
            {
                list.Add($"{t.Hive}\\{t.KeyPath}\\{t.ValueName} = {t.RecommendedData}");
            }

            return list;
        }
    }

    public IReadOnlyList<string> OfficialEvidence
    {
        get
        {
            var list = new List<string>();
            foreach (var claim in Definition.Provenance)
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

    public IReadOnlyList<string> CommunityEvidence
    {
        get
        {
            var list = new List<string>();
            foreach (var claim in Definition.Provenance)
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

    public IReadOnlyList<string> Scenarios =>
        Definition.UserScenarios.Count == 0
            ? new[] { _loc["Component.Unknown"] }
            : Definition.UserScenarios.Select(s => _loc["ComponentScenario." + s]).ToList();

    public IReadOnlyList<string> Dependencies =>
        Definition.Dependencies.Count == 0
            ? new[] { _loc["Component.Unknown"] }
            : Definition.Dependencies.Select(FormatDependency).ToList();

    // ---- Plan sync ----

    private void SyncPlan(bool selected)
    {
        if (Definition.Mechanism == OptimizationMechanism.ServiceStartup)
        {
            var opId = "svc|" + Definition.ServiceName;
            PlanSync.Toggle(_appState, opId, selected, () => new CustomizationOperation
            {
                OperationId = opId,
                Category = CustomizationCategory.Service,
                OperationType = CustomizationOperationType.ConfigureOfflineService,
                DisplayName = DisplayName,
                Description = ShortPurpose,
                ServiceName = Definition.ServiceName,
                ServiceStartType = Definition.ProposedStartType,
                Risk = Definition.Risk == RiskLevel.Low ? RiskClass.Safe : RiskClass.Removable,
                ActionKind = Definition.Action,
                Mechanism = Definition.Mechanism,
                Scope = Definition.Scope,
                ReversalKey = Definition.ReversalKey,
                ExecutionOrder = 0,
            });
            _parent.RefreshSelectedTotal();
            return;
        }

        var index = 0;
        foreach (var target in Definition.RegistryTargets)
        {
            var opId = $"opt|{Definition.Id}|{index}";
            PlanSync.Toggle(_appState, opId, selected, () => new CustomizationOperation
            {
                OperationId = opId,
                Category = CategoryForTab(Definition.Tab),
                OperationType = CustomizationOperationType.SetOfflineRegistryValue,
                DisplayName = DisplayName,
                Description = ShortPurpose,
                RegistryHive = target.Hive,
                RegistryKeyPath = target.KeyPath,
                RegistryValueName = target.ValueName,
                RegistryValueKind = target.ValueKind,
                RegistryValueData = target.RecommendedData,
                Risk = RiskClass.Safe,
                ActionKind = Definition.Action,
                Mechanism = Definition.Mechanism,
                Scope = Definition.Scope,
                ReversalKey = Definition.ReversalKey,
                RestoreValueData = target.RestoreData,
                ExecutionOrder = index,
            });
            index++;
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

    private IReadOnlyList<string> TargetOperationIds()
    {
        if (Definition.Mechanism == OptimizationMechanism.ServiceStartup)
        {
            return new[] { "svc|" + Definition.ServiceName };
        }

        var ids = new List<string>();
        for (var i = 0; i < Definition.RegistryTargets.Count; i++)
        {
            ids.Add($"opt|{Definition.Id}|{i}");
        }

        return ids;
    }

    private static CustomizationCategory CategoryForTab(OptimizationTab tab) => tab switch
    {
        OptimizationTab.Privacy => CustomizationCategory.Privacy,
        OptimizationTab.System => CustomizationCategory.System,
        OptimizationTab.Personalization => CustomizationCategory.Personalization,
        _ => CustomizationCategory.System,
    };

    private string FormatDependency(ComponentDependency dep)
    {
        var relation = _loc["Dependency." + dep.Relation];
        var targetKey = "Comp." + dep.ToId + ".DisplayName";
        var target = _loc[targetKey];
        target = string.IsNullOrEmpty(target) || target == targetKey ? dep.ToId : target;
        var reason = string.IsNullOrEmpty(dep.Reason) ? string.Empty : " — " + _loc[dep.Reason!];
        return $"{relation}: {target}{reason}";
    }
}
