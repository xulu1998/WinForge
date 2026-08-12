using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using WinForge.App.Mvvm;
using WinForge.App.Services;
using WinForge.Core.Models;
using WinForge.Core.Profiles;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>One selectable profile card in the Customize profile selector (Part H).</summary>
public sealed class ProfileItemViewModel : ViewModelBase
{
    private readonly ILocalizationService _loc;
    private readonly ProfileViewModel _parent;
    private bool _isSelected;

    public ProfileItemViewModel(ProfileDefinition definition, ILocalizationService loc, ProfileViewModel parent)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
    }

    public ProfileDefinition Definition { get; }

    public string DisplayName => _loc[Definition.DisplayNameKey];

    public string Description => _loc[Definition.DescriptionKey];

    /// <summary>TwoWay bound to the card toggle — routes through the session context.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value))
            {
                if (value)
                {
                    _parent.Select(Definition.Id);
                }
                else
                {
                    _parent.Deselect(Definition.Id);
                }
            }
        }
    }

    /// <summary>
    /// Syncs the card's checked state WITHOUT routing through the context (no
    /// Select/Deselect). Used by <see cref="ProfileViewModel.RefreshSelectionFlags"/>
    /// to mirror the context after a profile change — routing would re-enter
    /// ToggleProfile and recurse (UX-fix regression guard).
    /// </summary>
    internal void SetSelectedSilently(bool value) => SetField(ref _isSelected, value);
}

/// <summary>Preview-group discriminator (Part I).</summary>
public enum RecommendationPreviewGroupKind
{
    Adopt,
    Keep,
    Manual,
    Conflict
}

/// <summary>One candidate row in the recommendation preview.</summary>
public sealed class RecommendationPreviewItem
{
    public string DisplayName { get; init; } = string.Empty;
    public string Caption { get; init; } = string.Empty;
    public string ReasonText { get; init; } = string.Empty;
}

/// <summary>One preview group: 推荐执行 / 建议保留 / 需要确认 / 冲突·阻止.</summary>
public sealed class RecommendationPreviewGroup
{
    public RecommendationPreviewGroupKind Kind { get; init; }
    public string HeaderKey { get; init; } = string.Empty;

    /// <summary>Per-tab count breakdown, e.g. "Apps 5 · Privacy 4" (Part 11).</summary>
    public string TabBreakdown { get; set; } = string.Empty;

    public ObservableCollection<RecommendationPreviewItem> Items { get; } = new();
}

/// <summary>
/// Stage 11.4 profile selector (final flow, ADR-057..060). Sits at the top of
/// Customize. ONE primary profile (mutually exclusive radio cards) plus optional
/// EXTRA scenarios (independent checkboxes) recompute every tab's effective
/// recommendation, and — the key product change — SELECTING A PROFILE ITSELF
/// immediately applies its SAFE recommended selections (no separate
/// "采用推荐选择" step). Auto-selection stays strictly limited to present,
/// apply-supported, low-risk, trusted, conflict-free items (Part J). Manual
/// toggles are recorded as user overrides (Part K) and survive profile
/// switching; "恢复此配置推荐" (conditional) explicitly clears overrides and
/// recalculates. The recommendation detail opens as an overlay with an explicit
/// close/back action.
/// </summary>
public sealed class ProfileViewModel : ViewModelBase
{
    private readonly RecommendationContextService _ctx;
    private readonly ILocalizationService _loc;
    private readonly Func<IEnumerable<IRecommendationSubject>> _subjects;
    private readonly Action _recompute;

    private bool _isPreviewOpen;

    public ProfileViewModel(
        RecommendationContextService ctx,
        ILocalizationService loc,
        Func<IEnumerable<IRecommendationSubject>> subjects,
        Action recompute)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));
        _subjects = subjects ?? throw new ArgumentNullException(nameof(subjects));
        _recompute = recompute ?? throw new ArgumentNullException(nameof(recompute));

        Profiles = new ObservableCollection<ProfileItemViewModel>(
            ctx.AllProfiles.Where(p => p.Kind == ProfileKind.Primary)
                .Select(p => new ProfileItemViewModel(p, _loc, this)));
        ExtraScenarios = new ObservableCollection<ProfileItemViewModel>(
            ctx.AllProfiles.Where(p => p.Kind == ProfileKind.ExtraScenario)
                .Select(p => new ProfileItemViewModel(p, _loc, this)));
        RefreshSelectionFlags();

        ShowPreviewCommand = new RelayCommand(_ => ShowPreview());
        ClosePreviewCommand = new RelayCommand(_ => ClosePreview());
        RestoreCommand = new RelayCommand(_ => Restore(), _ => _ctx.HasActiveProfiles && RestoreVisible);

        _ctx.Changed += (_, _) => OnContextChanged();
    }

    /// <summary>Primary profiles — mutually exclusive radio cards (Part 1).</summary>
    public ObservableCollection<ProfileItemViewModel> Profiles { get; }

    /// <summary>Extra scenarios — independent secondary checkboxes (Part 2).</summary>
    public ObservableCollection<ProfileItemViewModel> ExtraScenarios { get; }

    /// <summary>Extras are additional requirements on top of a primary profile.</summary>
    public bool CanToggleExtras => _ctx.HasActiveProfiles;

    public bool HasExtraScenarios => ExtraScenarios.Count > 0;

    public bool HasUnsupported => UnsupportedCount > 0;

    public ObservableCollection<RecommendationPreviewGroup> PreviewGroups { get; } = new();

    public ICommand ShowPreviewCommand { get; }

    public ICommand ClosePreviewCommand { get; }

    /// <summary>"恢复此配置推荐" — recalculates Profile-managed selections, explicitly
    /// clearing user overrides first. Only visible when an override exists.</summary>
    public ICommand RestoreCommand { get; }

    public bool IsPreviewOpen
    {
        get => _isPreviewOpen;
        private set => SetField(ref _isPreviewOpen, value);
    }

    public bool HasActiveProfiles => _ctx.HasActiveProfiles;

    /// <summary>True when the row's current selection is auto-managed by the active profile.</summary>
    public bool IsProfileManaged(string logicalId) => _ctx.IsProfileManaged(logicalId);

    /// <summary>
    /// 当前推荐配置 caption. Custom counts as "自定义" (manual mode); with no
    /// primary profile at all it is the manual-mode text.
    /// </summary>
    public string ActiveProfileCaption
    {
        get
        {
            if (_ctx.HasActiveProfiles)
            {
                var primary = _loc[_ctx.SelectedProfiles.First(p => p.Kind == ProfileKind.Primary).DisplayNameKey];
                var extras = _ctx.SelectedProfiles.Where(p => p.Kind == ProfileKind.ExtraScenario)
                    .Select(p => _loc[p.DisplayNameKey]);
                return extras.Any() ? $"{primary} + {string.Join(" + ", extras)}" : primary;
            }

            return _ctx.PrimaryProfileId == "Custom"
                ? _loc["Profile.Custom.DisplayName"]
                : _loc["Profile.None"];
        }
    }

    /// <summary>
    /// Conditional "恢复此配置推荐" visibility (final flow): only when a profile is
    /// active AND the user has manually overridden at least one Profile-managed
    /// selection. Hidden initially and right after a plain profile selection.
    /// </summary>
    public bool RestoreVisible => _ctx.HasActiveProfiles && _ctx.HasUserOverrides;

    public bool HasPreviewItems => PreviewGroups.Any(g => g.Items.Count > 0);

    // ---- Summary metrics (Part P) — real present/applicable items only ----

    public int TrimCount => CountPresent(s => IsAdoptEligible(s));
    public int ManualCount => CountPresent(s => IsManualReview(s));
    public int KeepCount => CountPresent(s => s.Effective.Level == EffectiveRecommendationLevel.RecommendKeep);
    public int ConflictCount => CountPresent(s => s.Effective.HasConflict);
    public int UnsupportedCount => CountPresent(s => !s.Effective.IsApplySupported);

    public bool HasConflicts => ConflictCount > 0;

    public string SummaryAdoptLabel => _loc["Profile.Summary.Adopt"];
    public string SummaryConfirmLabel => _loc["Profile.Summary.Confirm"];
    public string SummaryKeepLabel => _loc["Profile.Summary.Keep"];
    public string SummaryConflictLabel => _loc["Profile.Summary.Conflict"];
    public string SummaryUnsupportedLabel => _loc["Profile.Summary.Unsupported"];

    // ---- Profile selection (routed from the cards) ----

    internal void Select(string profileId)
    {
        var kind = _ctx.AllProfiles.FirstOrDefault(p => p.Id == profileId)?.Kind;
        if (kind == ProfileKind.ExtraScenario)
        {
            _ctx.ToggleExtraScenario(profileId);
        }
        else
        {
            _ctx.ToggleProfile(profileId);
        }
    }

    internal void Deselect(string profileId)
    {
        var kind = _ctx.AllProfiles.FirstOrDefault(p => p.Id == profileId)?.Kind;
        if (kind == ProfileKind.ExtraScenario)
        {
            _ctx.ToggleExtraScenario(profileId);
        }
        // Primary radio cards are never user-deselected (radio semantics); the
        // silent sync below handles context-driven resets.
    }

    // ---- Recompute plumbing ----

    private void OnContextChanged()
    {
        RefreshSelectionFlags();
        _recompute();
        ApplyProfileSelections();
        RefreshSummary();
    }

    /// <summary>Called by the Customize coordinator after every tab recomputed.</summary>
    public void RefreshSummary()
    {
        OnPropertyChanged(nameof(HasActiveProfiles));
        OnPropertyChanged(nameof(ActiveProfileCaption));
        OnPropertyChanged(nameof(CanToggleExtras));
        OnPropertyChanged(nameof(HasExtraScenarios));
        OnPropertyChanged(nameof(HasUnsupported));
        OnPropertyChanged(nameof(TrimCount));
        OnPropertyChanged(nameof(ManualCount));
        OnPropertyChanged(nameof(KeepCount));
        OnPropertyChanged(nameof(ConflictCount));
        OnPropertyChanged(nameof(UnsupportedCount));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(SummaryAdoptLabel));
        OnPropertyChanged(nameof(SummaryConfirmLabel));
        OnPropertyChanged(nameof(SummaryKeepLabel));
        OnPropertyChanged(nameof(SummaryConflictLabel));
        OnPropertyChanged(nameof(SummaryUnsupportedLabel));
        OnPropertyChanged(nameof(RestoreVisible));
        if (RestoreCommand is RelayCommand r) r.RaiseCanExecuteChanged();
    }

    private void RefreshSelectionFlags()
    {
        // Silently mirror the context's selection — never routes back through
        // Select/Deselect, otherwise a profile change would re-enter the context
        // toggle and recurse.
        foreach (var p in Profiles)
        {
            p.SetSelectedSilently(_ctx.IsProfileSelected(p.Definition.Id));
        }

        foreach (var e in ExtraScenarios)
        {
            e.SetSelectedSilently(_ctx.IsExtraSelected(e.Definition.Id));
        }
    }

    private IEnumerable<IRecommendationSubject> Subjects() => _subjects();

    // ---- Preview (Part I) — non-destructive ----

    public void ShowPreview()
    {
        BuildPreviewGroups();
        IsPreviewOpen = true;
    }

    private void BuildPreviewGroups()
    {
        PreviewGroups.Clear();
        if (!_ctx.HasActiveProfiles)
        {
            return;
        }

        AddGroup(RecommendationPreviewGroupKind.Adopt, "Profile.Preview.Group.Adopt",
            Subjects().Where(IsAdoptEligible));
        AddGroup(RecommendationPreviewGroupKind.Keep, "Profile.Preview.Group.Keep",
            Subjects().Where(s => s.IsPresent && s.Effective.Level == EffectiveRecommendationLevel.RecommendKeep));
        AddGroup(RecommendationPreviewGroupKind.Manual, "Profile.Preview.Group.Manual",
            Subjects().Where(IsManualReview));
        AddGroup(RecommendationPreviewGroupKind.Conflict, "Profile.Preview.Group.Conflict",
            Subjects().Where(s => s.IsPresent &&
                (s.Effective.HasConflict || s.Effective.Level == EffectiveRecommendationLevel.Blocked || !s.Effective.IsApplySupported)));

        OnPropertyChanged(nameof(HasPreviewItems));
    }

    private void AddGroup(RecommendationPreviewGroupKind kind, string headerKey, IEnumerable<IRecommendationSubject> items)
    {
        var group = new RecommendationPreviewGroup { Kind = kind, HeaderKey = headerKey };
        var materialized = items.ToList();
        foreach (var s in materialized.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            group.Items.Add(new RecommendationPreviewItem
            {
                DisplayName = s.DisplayName,
                Caption = s.RecommendationCaption,
                ReasonText = s.ReasonText,
            });
        }

        // Part 11 — per-tab breakdown: "Apps 5 · Privacy 4".
        group.TabBreakdown = string.Join(" · ", materialized
            .GroupBy(s => s.Tab)
            .OrderBy(g => g.Key)
            .Select(g => $"{_loc["Customize.Tab." + g.Key]} {g.Count()}"));

        PreviewGroups.Add(group);
    }

    // ---- Final flow — selecting a Profile IS the adoption (Part J unchanged) ----

    /// <summary>
    /// Part J eligibility: present + apply-supported + effective level is a
    /// recommended change (remove/disable/set) + risk Low + no conflict + not a
    /// user override. Everything else stays manual (需要确认 / 当前不可执行).
    /// </summary>
    private static bool IsAdoptEligible(IRecommendationSubject s)
        => s.IsPresent
           && s.Effective.IsApplySupported
           && s.Effective.Risk == RiskLevel.Low
           && !s.Effective.HasConflict
           && !s.Effective.WasOverridden
           && s.Effective.Level is EffectiveRecommendationLevel.RecommendRemove
               or EffectiveRecommendationLevel.RecommendDisable
               or EffectiveRecommendationLevel.RecommendSet;

    private static bool IsManualReview(IRecommendationSubject s)
        => s.IsPresent
           && !IsAdoptEligible(s)
           && s.Effective.Level != EffectiveRecommendationLevel.RecommendKeep
           && !(s.Effective.HasConflict || s.Effective.Level == EffectiveRecommendationLevel.Blocked || !s.Effective.IsApplySupported);

    /// <summary>
    /// Directly applies the active profile's safe recommendations to the plan.
    /// Called automatically whenever the profile context changes (selection of a
    /// profile or an extra scenario, Custom, restore). Deterministic:
    /// <list type="bullet">
    ///   <item>a user override NEVER changes — the item stays exactly as the user left it;</item>
    ///   <item>an eligible item is selected (Profile-managed);</item>
    ///   <item>an item that WAS Profile-managed but is no longer eligible under the
    ///     NEW profile is deselected (unless overridden);</item>
    ///   <item>an item the user selected manually (never Profile-managed) is untouched;</item>
    ///   <item>Custom / no profile: Profile-managed bookkeeping is cleared and NOTHING changes.</item>
    /// </list>
    /// </summary>
    private void ApplyProfileSelections()
    {
        if (!_ctx.HasActiveProfiles)
        {
            _ctx.SetProfileManaged(Array.Empty<string>());
            return;
        }

        var subjects = Subjects().ToList();
        var eligible = subjects.Where(IsAdoptEligible).Select(s => s.LogicalId).ToHashSet(StringComparer.Ordinal);

        foreach (var s in subjects)
        {
            if (s.WasOverridden)
            {
                continue; // user choice protected (Part K)
            }

            if (eligible.Contains(s.LogicalId))
            {
                s.SetSelectedForAdoption(true);
            }
            else if (_ctx.IsProfileManaged(s.LogicalId) && s.IsSelected)
            {
                // Was auto-applied by the previous profile, no longer recommended.
                s.SetSelectedForAdoption(false);
            }
        }

        _ctx.SetProfileManaged(eligible);
        RefreshSummary();
    }

    /// <summary>
    /// "恢复此配置推荐" — the ONLY explicit path that may overwrite user overrides.
    /// Clears all overrides (raising the context change, which re-applies the
    /// profile's safe recommendations), then refreshes the summary.
    /// </summary>
    public void Restore()
    {
        if (!RestoreVisible)
        {
            return;
        }

        _ctx.ClearUserOverrides(); // triggers OnContextChanged -> recompute + ApplyProfileSelections
        RefreshSummary();
    }

    private int CountPresent(Func<IRecommendationSubject, bool> predicate)
        => _ctx.HasActiveProfiles ? Subjects().Count(s => s.IsPresent && predicate(s)) : 0;

    /// <summary>Closes the recommendation-detail overlay, restoring the Customize surface.</summary>
    public void ClosePreview() => IsPreviewOpen = false;
}
