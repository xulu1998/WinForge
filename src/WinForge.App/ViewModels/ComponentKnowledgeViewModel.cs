using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using WinForge.App.Mvvm;
using WinForge.App.Services;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>Filter for the knowledge-backed Customize Apps view (mirrors the recommendation order).</summary>
public enum ComponentKnowledgeFilter
{
    All,
    RecommendedRemove,
    OptionalRemove,
    UsuallyKeep,
    AdvancedOnly,
    NeverRemove
}

/// <summary>One selectable filter option for the UI ComboBox.</summary>
public sealed class ComponentKnowledgeFilterItem
{
    public ComponentKnowledgeFilter Value { get; init; }
    public string Caption { get; init; } = string.Empty;
}

/// <summary>
/// Knowledge engine behind the Customize **Apps tab** (Stage 11.2 UX rework, ADR-048 —
/// the former separate "Component Knowledge" tab was removed and this engine
/// repurposed as the Apps tab). It reuses the already-discovered, classified inventory
/// held by <see cref="ComponentIntelligenceViewModel"/> (so Component Intelligence is the
/// backing knowledge model, not a second workflow) and presents curated components
/// with human names, recommendation/risk badges, default decision-useful sorting,
/// filtering, a hover quick card, and a click-for-detail panel. Selection toggles
/// declarative plan operations through <see cref="PlanSync"/> — the same
/// non-destructive flow the existing Components page uses.
///
/// <para>Standard behaviour: only CURATED (well-understood) components are offered
/// as selectable removal items. Raw unclassified / Protected objects are NOT
/// exposed as selectable removal items here — they remain in the separate
/// Component Intelligence inspection surface.</para>
/// </summary>
public sealed class ComponentKnowledgeViewModel : ViewModelBase
{
    private readonly IAppState _appState;
    private readonly ILoggerService _logger;
    private readonly ILocalizationService _loc;
    private readonly ComponentIntelligenceViewModel _ciVm;

    private readonly List<ComponentKnowledgeItem> _all = new();
    private readonly ComponentCategory[]? _categoryFilter;
    private ComponentKnowledgeFilter _filter = ComponentKnowledgeFilter.All;
    private ComponentKnowledgeItem? _activeDetail;
    private bool _isDiscovering;
    private bool _hasInventory;

    public ComponentKnowledgeViewModel(
        ComponentIntelligenceViewModel ciVm,
        IAppState appState,
        ILoggerService logger,
        ILocalizationService loc,
        ComponentCategory[]? categoryFilter = null)
    {
        _ciVm = ciVm ?? throw new ArgumentNullException(nameof(ciVm));
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));
        // Default = provisioned AppX only (Apps tab). The Windows Components tab
        // (Stage 11.3) passes { Capability, OptionalFeature } to reuse this exact
        // knowledge engine for a different raw category (ADR-051).
        _categoryFilter = categoryFilter ?? new[] { ComponentCategory.AppX };

        Items = new ObservableCollection<ComponentKnowledgeItem>();
        DiscoverCommand = new AsyncRelayCommand(_ => DiscoverAsync(), _ => CanDiscover);
        ShowDetailCommand = new RelayCommand(p => { if (p is ComponentKnowledgeItem it) ActiveDetail = it; });
        ClearDetailCommand = new RelayCommand(_ => ActiveDetail = null);

        _appState.PropertyChanged += OnAppStateChanged;
        _loc.CultureChanged += OnCultureChanged;

        Rebuild();
    }

    public ObservableCollection<ComponentKnowledgeItem> Items { get; }

    public ICommand DiscoverCommand { get; }

    public ICommand ShowDetailCommand { get; }

    public ICommand ClearDetailCommand { get; }

    /// <summary>The detail panel target (null = collapsed). Setting it NEVER changes selection.</summary>
    public ComponentKnowledgeItem? ActiveDetail
    {
        get => _activeDetail;
        set
        {
            if (SetField(ref _activeDetail, value))
            {
                RefreshActiveDetailFlags();
            }
        }
    }

    /// <summary>
    /// Keeps each row's <see cref="ComponentKnowledgeItem.IsActiveDetail"/> flag in
    /// sync with the single open detail, so exactly one row shows the "currently
    /// being inspected" highlight independent of removal selection.
    /// </summary>
    private void RefreshActiveDetailFlags()
    {
        foreach (var it in _all)
        {
            it.IsActiveDetail = ReferenceEquals(it, _activeDetail);
        }
    }

    public ComponentKnowledgeFilter Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value))
            {
                ApplyFilter();
            }
        }
    }

    public IReadOnlyList<ComponentKnowledgeFilterItem> FilterOptions { get; } = new List<ComponentKnowledgeFilterItem>
    {
        new() { Value = ComponentKnowledgeFilter.All, Caption = "Knowledge.Filter.All" },
        new() { Value = ComponentKnowledgeFilter.RecommendedRemove, Caption = "Knowledge.Filter.RecommendedRemove" },
        new() { Value = ComponentKnowledgeFilter.OptionalRemove, Caption = "Knowledge.Filter.OptionalRemove" },
        new() { Value = ComponentKnowledgeFilter.UsuallyKeep, Caption = "Knowledge.Filter.UsuallyKeep" },
        new() { Value = ComponentKnowledgeFilter.AdvancedOnly, Caption = "Knowledge.Filter.AdvancedOnly" },
        new() { Value = ComponentKnowledgeFilter.NeverRemove, Caption = "Knowledge.Filter.NeverRemove" },
    };

    public bool IsDiscovering
    {
        get => _isDiscovering;
        private set => SetField(ref _isDiscovering, value);
    }

    public bool HasInventory
    {
        get => _hasInventory;
        private set
        {
            if (SetField(ref _hasInventory, value))
            {
                OnPropertyChanged(nameof(EmptyStateText));
            }
        }
    }

    /// <summary>True when there are no rows to display (drives the empty-state panel).</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>
    /// Localized empty-state caption. Before discovery it prompts the user to
    /// discover; after discovery with no curated-present components it states that
    /// explicitly. Never an unexplained empty detail card.
    /// </summary>
    public string EmptyStateText => HasInventory
        ? _loc["Knowledge.EmptyNoCurated"]
        : _loc["Knowledge.EmptyAwaitDiscovery"];

    public bool IsMounted =>
        _appState.CurrentServicingWorkspace?.State == ServicingWorkspaceState.Mounted;

    public bool CanDiscover => !IsDiscovering && IsMounted;

    public async Task DiscoverAsync()
    {
        if (!CanDiscover)
        {
            return;
        }

        IsDiscovering = true;
        try
        {
            await _ciVm.DiscoverAsync();
            Rebuild();
            HasInventory = _ciVm.Inventory?.Discovered ?? false;
        }
        finally
        {
            IsDiscovering = false;
            Refresh();
        }
    }

    /// <summary>Rebuilds the full (sorted) list from the shared classified inventory.</summary>
    private void Rebuild()
    {
        _all.Clear();
        var inventory = _ciVm.Inventory;
        if (inventory is not null)
        {
            foreach (var entry in inventory.Entries)
            {
                // ADR-049 (real-desktop fix): only CURATED components actually
                // PRESENT in the image are offered as removable rows. Catalog-only
                // definitions (no matching raw item) are NOT shown — the user must
                // never be offered removal of something absent from the image, and
                // before discovery the list shows the empty-await-discovery state.
                // Raw unclassified / Protected objects stay in the Component
                // Intelligence inspection surface.
                if (entry.Classification != ComponentClassification.Curated || entry.RawItems.Count == 0)
                {
                    continue;
                }

                // Stage 11.3: one knowledge engine serves multiple tabs — the Apps
                // tab shows AppX only, the Windows Components tab shows capabilities
                // / optional features. Catalog-only definitions from other
                // categories never leak into this tab's list.
                if (_categoryFilter is not null && entry.Definition is not null &&
                    !_categoryFilter.Contains(entry.Definition.Category))
                {
                    continue;
                }

                _all.Add(new ComponentKnowledgeItem(entry, _loc, _appState, this));
            }
        }

        _all.Sort(CompareForSort);
        foreach (var it in _all)
        {
            it.RefreshSelectionFromPlan();
        }

        HasInventory = inventory?.Discovered ?? false;
        ApplyFilter();
        OnPropertyChanged(nameof(CuratedCount));
    }

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var it in _all)
        {
            if (_filter == ComponentKnowledgeFilter.All || it.RecommendationLevel == MapFilter(_filter))
            {
                Items.Add(it);
            }
        }

        // Preferred deterministic behaviour (spec): if the item currently shown in
        // the detail panel is no longer in the visible filtered set, close the
        // detail panel. Removal selections are intentionally NOT touched.
        if (_activeDetail is not null && !Items.Contains(_activeDetail))
        {
            ActiveDetail = null;
        }

        // Empty-state is derived from Items.Count; EmptyStateText also depends on
        // HasInventory, so re-raise both whenever the visible list is rebuilt.
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyStateText));
    }

    public int CuratedCount => _all.Count;

    internal void RefreshSelectedTotal()
    {
        // Hook for future aggregate counts; selection already lives in the shared plan.
    }

    private static int RecommendationOrder(RecommendationLevel r) => r switch
    {
        RecommendationLevel.RecommendedRemove => 1,
        RecommendationLevel.OptionalRemove => 2,
        RecommendationLevel.UsuallyKeep => 3,
        RecommendationLevel.AdvancedOnly => 4,
        RecommendationLevel.NeverRemove => 5,
        _ => 99,
    };

    private static int RiskOrder(RiskLevel r) => (int)r; // Unknown=0..Critical=4

    private static int CompareForSort(ComponentKnowledgeItem a, ComponentKnowledgeItem b)
    {
        // 1) recommendation usefulness, 2) risk, 3) category, 4) name.
        var c = RecommendationOrder(a.RecommendationLevel).CompareTo(RecommendationOrder(b.RecommendationLevel));
        if (c != 0)
        {
            return c;
        }

        c = RiskOrder(a.RiskLevel).CompareTo(RiskOrder(b.RiskLevel));
        if (c != 0)
        {
            return c;
        }

        c = string.CompareOrdinal(a.CategoryCaption, b.CategoryCaption);
        if (c != 0)
        {
            return c;
        }

        return string.CompareOrdinal(a.DisplayName, b.DisplayName);
    }

    private static RecommendationLevel MapFilter(ComponentKnowledgeFilter f) => f switch
    {
        ComponentKnowledgeFilter.RecommendedRemove => RecommendationLevel.RecommendedRemove,
        ComponentKnowledgeFilter.OptionalRemove => RecommendationLevel.OptionalRemove,
        ComponentKnowledgeFilter.UsuallyKeep => RecommendationLevel.UsuallyKeep,
        ComponentKnowledgeFilter.AdvancedOnly => RecommendationLevel.AdvancedOnly,
        ComponentKnowledgeFilter.NeverRemove => RecommendationLevel.NeverRemove,
        _ => RecommendationLevel.Unknown,
    };

    private void OnCultureChanged(object? sender, EventArgs e) => Rebuild();

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IAppState.CurrentCustomizationPlan))
        {
            foreach (var it in _all)
            {
                it.RefreshSelectionFromPlan();
            }
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsMounted));
        OnPropertyChanged(nameof(CanDiscover));
        if (DiscoverCommand is AsyncRelayCommand cmd)
        {
            cmd.RaiseCanExecuteChanged();
        }
    }
}
