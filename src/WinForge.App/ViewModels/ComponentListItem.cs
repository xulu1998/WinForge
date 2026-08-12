using System;
using System.Collections.Generic;
using System.Linq;
using WinForge.App.Mvvm;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// UI-facing wrapper around a single classified <see cref="ComponentInventoryEntry"/>.
/// It resolves human-readable, localized text for an ordinary user and NEVER hides
/// uncertainty: when a piece of knowledge is Unknown it renders the localized
/// "Unknown" caption (en "Unknown" / zh-CN "尚未确认") so the user is never misled.
///
/// <para>Getters resolve through <see cref="ILocalizationService"/> on every read, so
/// the displayed text refreshes when the active language changes (the host ViewModel
/// rebuilds the list on <see cref="ILocalizationService.CultureChanged"/>).</para>
///
/// <para>This wrapper is purely presentational — it performs no discovery or servicing
/// and never hides a raw Windows identity behind a friendly name.</para>
/// </summary>
public sealed class ComponentListItem : ViewModelBase
{
    private readonly ILocalizationService _loc;

    public ComponentListItem(ComponentInventoryEntry entry, ILocalizationService loc)
    {
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
    }

    public ComponentInventoryEntry Entry { get; }

    public bool IsCurated => Entry.Definition is not null;

    public ComponentClassification Classification => Entry.Classification;

    // Stable enum values exposed for color coding in the view.
    public RecommendationLevel RecommendationLevel => Entry.Definition?.Recommendation ?? RecommendationLevel.Unknown;
    public RiskLevel RiskLevel => Entry.Definition?.Risk ?? RiskLevel.Unknown;

    private string Unknown => _loc["Component.Unknown"];

    // ---- Human name ----
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

            if (raw is not null && !string.IsNullOrEmpty(raw.RawIdentity))
            {
                return raw.RawIdentity;
            }

            return Unknown;
        }
    }

    public string ShortDescription
    {
        get
        {
            if (Entry.Definition is not null)
            {
                var s = _loc[Entry.Definition.ShortDescriptionKey];
                if (!string.IsNullOrEmpty(s) && s != Entry.Definition.ShortDescriptionKey)
                {
                    return s;
                }
            }

            return Unknown;
        }
    }

    public string Recommendation => Entry.Definition is null ? Unknown : _loc["Recommendation." + Entry.Definition.Recommendation];
    public string Risk => Entry.Definition is null ? Unknown : _loc["Risk." + Entry.Definition.Risk];
    public string RemovalInfo => Entry.Definition is null ? Unknown : _loc["Removal." + Entry.Definition.Removal];
    public string Restoration => Entry.Definition is null ? Unknown : _loc["Restore." + Entry.Definition.Restore];

    public string ClassificationCaption => _loc["Classification." + Entry.Classification];
    public string CategoryCaption => Entry.RepresentativeRaw is null ? Unknown : _loc["Category." + Entry.RepresentativeRaw.Category];

    public IReadOnlyList<string> KeepIf => FormatOptionalList(Entry.Definition?.KeepIf);
    public IReadOnlyList<string> RemoveIf => FormatOptionalList(Entry.Definition?.RemoveIf);
    public IReadOnlyList<string> KnownImpact => FormatOptionalList(Entry.Definition?.KnownImpact);

    public IReadOnlyList<string> Scenarios
    {
        get
        {
            if (Entry.Definition is null || Entry.Definition.UserScenarios.Count == 0)
            {
                return new[] { Unknown };
            }

            return Entry.Definition.UserScenarios.Select(s => _loc["ComponentScenario." + s]).ToList();
        }
    }

    public IReadOnlyList<string> Dependencies
    {
        get
        {
            if (Entry.Definition is null || Entry.Definition.Dependencies.Count == 0)
            {
                return new[] { Unknown };
            }

            return Entry.Definition.Dependencies.Select(FormatDependency).ToList();
        }
    }

    public IReadOnlyList<string> Conflicts
    {
        get
        {
            if (Entry.Definition is null || Entry.Definition.Conflicts.Count == 0)
            {
                return new[] { Unknown };
            }

            return Entry.Definition.Conflicts.ToList();
        }
    }

    public string SavingsText
    {
        get
        {
            if (Entry.Definition is null || Entry.Definition.SavingsConfidence == SavingsConfidence.None)
            {
                return Unknown;
            }

            return $"{Entry.Definition.EstimatedSavingsBytes} ({_loc["Savings." + Entry.Definition.SavingsConfidence]})";
        }
    }

    // ---- Raw / technical details (always available for the collapsed section) ----
    public IReadOnlyList<IRawInventoryItem> RawItems => Entry.RawItems;
    public string RawIdentity => Entry.RepresentativeRaw?.RawIdentity ?? Unknown;
    public string RawState => Entry.RepresentativeRaw?.State ?? Unknown;
    public string RawVersion => Entry.RepresentativeRaw?.Version ?? Unknown;
    public string RawCategory => Entry.RepresentativeRaw is null ? Unknown : _loc["Category." + Entry.RepresentativeRaw.Category];

    public string MatchRuleText
    {
        get
        {
            if (Entry.Definition is null)
            {
                return Unknown;
            }

            var rules = Entry.Definition.TechnicalTargets
                .Select(t => $"{_loc["Category." + t.Category]} · {t.Match} · {t.Pattern}")
                .ToList();
            return rules.Count > 0 ? string.Join("; ", rules) : Unknown;
        }
    }

    public string PresentText => Entry.RawItems.Count > 0 ? _loc["Component.Present"] : _loc["Component.NotInventoried"];

    private IReadOnlyList<string> FormatOptionalList(IReadOnlyList<string>? keys)
    {
        if (keys is null)
        {
            return new[] { Unknown };
        }

        var resolved = ResolveList(keys);
        return resolved.Count > 0 ? resolved : new[] { Unknown };
    }

    private IReadOnlyList<string> ResolveList(IReadOnlyList<string> keys)
    {
        var result = new List<string>();
        foreach (var k in keys)
        {
            var v = _loc[k];
            if (!string.IsNullOrEmpty(v) && v != k)
            {
                result.Add(v);
            }
        }

        return result;
    }

    private string FormatDependency(ComponentDependency dep)
    {
        var relation = _loc["Dependency." + dep.Relation];
        var target = ResolveTargetName(dep.ToId);
        var reason = string.IsNullOrEmpty(dep.Reason) ? string.Empty : " — " + dep.Reason;
        return $"{relation}: {target}{reason}";
    }

    private string ResolveTargetName(string toId)
    {
        var key = "Comp." + toId + ".DisplayName";
        var v = _loc[key];
        return string.IsNullOrEmpty(v) || v == key ? toId : v;
    }
}
