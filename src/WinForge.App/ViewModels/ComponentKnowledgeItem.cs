using System;
using System.Collections.Generic;
using System.Linq;
using WinForge.App.Mvvm;
using WinForge.App.Services;
using WinForge.Core.Models;
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
/// </summary>
public sealed class ComponentKnowledgeItem : ViewModelBase
{
    private readonly ILocalizationService _loc;
    private readonly IAppState _appState;
    private readonly ComponentKnowledgeViewModel _parent;

    public ComponentKnowledgeItem(
        ComponentInventoryEntry entry,
        ILocalizationService loc,
        IAppState appState,
        ComponentKnowledgeViewModel parent)
    {
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
    }

    public ComponentInventoryEntry Entry { get; }

    public bool IsCurated => Entry.Definition is not null;

    public RecommendationLevel RecommendationLevel => Entry.Definition?.Recommendation ?? RecommendationLevel.Unknown;

    public RiskLevel RiskLevel => Entry.Definition?.Risk ?? RiskLevel.Unknown;

    // ---- Human name + category ----
    public string DisplayName
    {
        get
        {
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
            if (Entry.Definition is null)
            {
                return _loc["Component.NotConfirmed"];
            }

            var s = _loc[Entry.Definition.ShortDescriptionKey];
            return !string.IsNullOrEmpty(s) && s != Entry.Definition.ShortDescriptionKey ? s : _loc["Component.Unknown"];
        }
    }

    public string RecommendationCaption => _loc["Recommendation." + RecommendationLevel];

    public string RiskCaption => _loc["Risk." + RiskLevel];

    // ---- Selectability ----
    /// <summary>True when the item may be selected into the plan (removable, not Protected/Unknown).</summary>
    public bool IsSelectable =>
        Entry.Definition is not null &&
        Entry.Definition.Removal != RemovalSupport.Blocked &&
        Entry.Definition.Recommendation != RecommendationLevel.NeverRemove &&
        Entry.Definition.Risk != RiskLevel.Critical;

    /// <summary>Localized reason an item cannot be selected (empty when selectable).</summary>
    public string BlockReason =>
        Entry.Definition is null
            ? _loc["Component.NotConfirmed"]
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
        }
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
