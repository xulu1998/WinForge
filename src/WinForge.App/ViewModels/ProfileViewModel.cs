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
    public ObservableCollection<RecommendationPreviewItem> Items { get; } = new();
}

/// <summary>
/// Stage 11.4 profile selector + preview + adopt (ADR-057..060). Sits at the top
/// of Customize. Selecting one or more profiles recomputes every tab's effective
/// recommendation; NOTHING is selected into the plan until the user explicitly
/// clicks "采用推荐选择" — and even then only low-risk, apply-supported,
/// conflict-free, present items are auto-selected (Part J). Manual toggles are
/// recorded as user overrides (Part K) and survive recalculation / reapply.
/// </summary>
public sealed class ProfileViewModel : ViewModelBase
{
    private readonly RecommendationContextService _ctx;
    private readonly ILocalizationService _loc;
    private readonly Func<IEnumerable<IRecommendationSubject>> _subjects;
    private readonly Action _recompute;

    private bool _isPreviewOpen;
    private bool _hasAdopted;
    private string _adoptedSignature = string.Empty;

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
            ctx.AllProfiles.Select(p => new ProfileItemViewModel(p, _loc, this)));
        RefreshSelectionFlags();

        ShowPreviewCommand = new RelayCommand(_ => ShowPreview());
        AdoptCommand = new RelayCommand(_ => Adopt(), _ => _ctx.HasActiveProfiles);
        ReapplyCommand = new RelayCommand(_ => Reapply(), _ => _ctx.HasActiveProfiles && ReapplyVisible);

        _ctx.Changed += (_, _) => OnContextChanged();
    }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; }

    public ObservableCollection<RecommendationPreviewGroup> PreviewGroups { get; } = new();

    public ICommand ShowPreviewCommand { get; }

    public ICommand AdoptCommand { get; }

    public ICommand ReapplyCommand { get; }

    public bool IsPreviewOpen
    {
        get => _isPreviewOpen;
        private set => SetField(ref _isPreviewOpen, value);
    }

    public bool HasActiveProfiles => _ctx.HasActiveProfiles;

    /// <summary>
    /// 当前推荐配置 caption. Custom counts as "自定义" (manual mode); with no
    /// profile at all it is the manual-mode text.
    /// </summary>
    public string ActiveProfileCaption
    {
        get
        {
            if (_ctx.HasActiveProfiles)
            {
                return string.Join(" + ", _ctx.SelectedProfiles.Select(p => _loc[p.DisplayNameKey]));
            }

            return _ctx.IsProfileSelected("Custom")
                ? _loc["Profile.Custom.DisplayName"]
                : _loc["Profile.None"];
        }
    }

    /// <summary>
    /// Conditional "重新采用推荐" visibility (UX fix): only after recommendations
    /// were adopted AND the current state diverged — the user manually changed
    /// selections afterward, or the profile set changed since adoption. Hidden in
    /// the initial untouched state and right after an adopt with no divergence.
    /// </summary>
    public bool ReapplyVisible =>
        _hasAdopted && (ContextSignature() != _adoptedSignature || _ctx.IsUserOverriddenAny());

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

    // ---- Profile selection (routed from the cards) ----

    internal void Select(string profileId) => _ctx.ToggleProfile(profileId);

    internal void Deselect(string profileId) => _ctx.ToggleProfile(profileId);

    // ---- Recompute plumbing ----

    private void OnContextChanged()
    {
        RefreshSelectionFlags();
        _recompute();
        RefreshSummary();
    }

    /// <summary>Called by the Customize coordinator after every tab recomputed.</summary>
    public void RefreshSummary()
    {
        OnPropertyChanged(nameof(HasActiveProfiles));
        OnPropertyChanged(nameof(ActiveProfileCaption));
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
        OnPropertyChanged(nameof(ReapplyVisible));
        if (AdoptCommand is RelayCommand a) a.RaiseCanExecuteChanged();
        if (ReapplyCommand is RelayCommand r) r.RaiseCanExecuteChanged();
    }

    private void RefreshSelectionFlags()
    {
        // Silently mirror the context's selection — never routes back through
        // Select/Deselect, otherwise a profile change would re-enter ToggleProfile
        // and recurse (Custom's clear-presets path exposed this).
        foreach (var p in Profiles)
        {
            p.SetSelectedSilently(_ctx.IsProfileSelected(p.Definition.Id));
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
        foreach (var s in items.OrderBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            group.Items.Add(new RecommendationPreviewItem
            {
                DisplayName = s.DisplayName,
                Caption = s.RecommendationCaption,
                ReasonText = s.ReasonText,
            });
        }

        PreviewGroups.Add(group);
    }

    // ---- Adopt / Reapply (Part I/J/K) ----

    /// <summary>
    /// Part J eligibility: present + apply-supported + effective level is a
    /// recommended change (remove/disable/set) + risk Low + no conflict + not a
    /// user override. Everything else stays manual.
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

    /// <summary>Adopts the current recommendations — the ONLY path that changes selections.</summary>
    public void Adopt()
    {
        if (!_ctx.HasActiveProfiles)
        {
            return;
        }

        foreach (var s in Subjects().Where(IsAdoptEligible))
        {
            s.SetSelectedForAdoption(true);
        }

        _hasAdopted = true;
        _adoptedSignature = ContextSignature();
        RefreshSummary();
    }

    /// <summary>
    /// Re-applies the recommendations. Identical eligibility to Adopt; user
    /// overrides (manual choices) are excluded by the predicate, so an explicit
    /// user choice survives recalculation (Part K). After re-applying, the
    /// adoption signature moves to the current profile set.
    /// </summary>
    public void Reapply()
    {
        if (!ReapplyVisible)
        {
            return;
        }

        foreach (var s in Subjects().Where(IsAdoptEligible))
        {
            s.SetSelectedForAdoption(true);
        }

        _adoptedSignature = ContextSignature();
        RefreshSummary();
    }

    /// <summary>Deterministic signature of the active profile set (sorted, Custom excluded).</summary>
    private string ContextSignature()
        => string.Join("|", _ctx.SelectedProfiles.Select(p => p.Id).OrderBy(id => id, StringComparer.Ordinal));

    private int CountPresent(Func<IRecommendationSubject, bool> predicate)
        => _ctx.HasActiveProfiles ? Subjects().Count(s => s.IsPresent && predicate(s)) : 0;
}
