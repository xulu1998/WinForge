using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using WinForge.App.Mvvm;
using WinForge.App.Services;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// Knowledge engine behind the non-AppX Customize tabs (Services / Privacy /
/// System / Personalization — Stage 11.3). It is catalog-driven (no raw-image
/// matching required): every row wraps a reviewed <see cref="OptimizationDefinition"/>
/// from <see cref="IOptimizationCatalogProvider"/> for its <see cref="OptimizationTab"/>.
/// The same master–detail UX as the Apps tab is reused (row click opens the
/// detail panel; checkbox toggles the plan; the two states stay independent).
///
/// <para>One shared implementation serves four tabs — no duplicated knowledge
/// surfaces (Part L). Standard mode shows only reviewed, standard-visible
/// entries; Unknown / experimental candidates never appear (Part M).</para>
/// </summary>
public sealed class OptimizationKnowledgeViewModel : ViewModelBase
{
    private readonly IAppState _appState;
    private readonly ILoggerService _logger;
    private readonly ILocalizationService _loc;
    private readonly IOptimizationCatalogProvider _catalog;

    private readonly List<OptimizationKnowledgeItem> _all = new();
    private ComponentKnowledgeFilter _filter = ComponentKnowledgeFilter.All;
    private OptimizationKnowledgeItem? _activeDetail;

    public OptimizationKnowledgeViewModel(
        IAppState appState,
        ILoggerService logger,
        ILocalizationService loc,
        IOptimizationCatalogProvider catalog,
        OptimizationTab tab)
    {
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Tab = tab;

        Items = new ObservableCollection<OptimizationKnowledgeItem>();
        ShowDetailCommand = new RelayCommand(p => { if (p is OptimizationKnowledgeItem it) ActiveDetail = it; });
        ClearDetailCommand = new RelayCommand(_ => ActiveDetail = null);

        _appState.PropertyChanged += OnAppStateChanged;
        _loc.CultureChanged += OnCultureChanged;

        Rebuild();
    }

    public OptimizationTab Tab { get; }

    public ObservableCollection<OptimizationKnowledgeItem> Items { get; }

    public ICommand ShowDetailCommand { get; }

    public ICommand ClearDetailCommand { get; }

    public OptimizationKnowledgeItem? ActiveDetail
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

    public bool IsEmpty => Items.Count == 0;

    public string EmptyStateText => _loc["Opt.Empty"];

    public int ItemCount => _all.Count;

    private void Rebuild()
    {
        _all.Clear();
        foreach (var definition in _catalog.GetEntries())
        {
            // Part M: only reviewed, standard-visible entries may reach Standard mode.
            if (definition.Tab != Tab || !definition.IsStandardVisible)
            {
                continue;
            }

            _all.Add(new OptimizationKnowledgeItem(definition, _loc, _appState, this));
        }

        _all.Sort(CompareForSort);
        foreach (var it in _all)
        {
            it.RefreshSelectionFromPlan();
        }

        ApplyFilter();
        OnPropertyChanged(nameof(ItemCount));
    }

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var it in _all)
        {
            if (_filter == ComponentKnowledgeFilter.All || it.Definition.Recommendation == MapFilter(_filter))
            {
                Items.Add(it);
            }
        }

        // Same deterministic behaviour as the Apps tab: if the item shown in the
        // detail panel leaves the visible filtered set, close the panel (selection
        // is NOT touched).
        if (_activeDetail is not null && !Items.Contains(_activeDetail))
        {
            ActiveDetail = null;
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyStateText));
    }

    private void RefreshActiveDetailFlags()
    {
        foreach (var it in _all)
        {
            it.IsActiveDetail = ReferenceEquals(it, _activeDetail);
        }
    }

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

    private static int CompareForSort(OptimizationKnowledgeItem a, OptimizationKnowledgeItem b)
    {
        var c = RecommendationOrder(a.Definition.Recommendation)
            .CompareTo(RecommendationOrder(b.Definition.Recommendation));
        if (c != 0)
        {
            return c;
        }

        c = ((int)a.Definition.Risk).CompareTo((int)b.Definition.Risk);
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
        if (e.PropertyName is nameof(IAppState.CurrentCustomizationPlan)
            or nameof(IAppState.CurrentImageWorkspace))
        {
            foreach (var it in _all)
            {
                it.RefreshSelectionFromPlan();
            }

            if (e.PropertyName == nameof(IAppState.CurrentImageWorkspace))
            {
                // Build/edition gating may have changed — re-evaluate the list.
                Rebuild();
            }
        }
    }
}
