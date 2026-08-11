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

/// <summary>
/// Drives the Component Intelligence prototype (Stage 11.1): read-only discovery of
/// an offline image, classification through the curated catalog, and a human-facing
/// explanation of WHAT a component is, WHETHER you need it, and WHAT breaks if
/// removed.
///
/// <para>Standard mode shows only curated (well-understood) components; Advanced mode
/// additionally shows raw discovered objects (read-only, never offered for removal).
/// The ViewModel performs NO servicing and never calls DISM — discovery is delegated
/// to <see cref="IComponentIntelligenceService"/>.</para>
/// </summary>
public sealed class ComponentIntelligenceViewModel : ViewModelBase
{
    private readonly IAppState _appState;
    private readonly ILoggerService _logger;
    private readonly IComponentIntelligenceService _service;
    private readonly IComponentCatalogProvider _catalog;
    private readonly ILocalizationService _loc;

    private bool _isDiscovering;
    private bool _hasInventory;
    private bool _standardMode = true;
    private ComponentListItem? _selectedEntry;
    private string _statusMessage = string.Empty;
    private string _summary = string.Empty;
    private string _counts = string.Empty;
    private ComponentInventory? _classified;

    public ComponentIntelligenceViewModel(
        IAppState appState,
        ILoggerService logger,
        IComponentIntelligenceService service,
        IComponentCatalogProvider catalog,
        ILocalizationService loc)
    {
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _loc = loc ?? throw new ArgumentNullException(nameof(loc));

        Entries = new ObservableCollection<ComponentListItem>();

        // Seed the prototype with the curated catalog (no image required) so ordinary
        // users immediately see what WinForge understands. Real discovery refines this.
        _classified = ComponentMatcher.BuildInventoryEntries(null, _catalog.GetDefinitions());
        RebuildEntries();

        DiscoverCommand = new AsyncRelayCommand(_ => DiscoverAsync(), _ => CanDiscover);
        _loc.CultureChanged += OnCultureChanged;
        _appState.PropertyChanged += OnAppStateChanged;
        StatusMessage = _loc["ComponentIntelligence.NoImage"];
    }

    public ICommand DiscoverCommand { get; }

    public ObservableCollection<ComponentListItem> Entries { get; }

    public ComponentListItem? SelectedEntry
    {
        get => _selectedEntry;
        set => SetField(ref _selectedEntry, value);
    }

    /// <summary>
    /// When true (the default) the list shows only curated, well-understood components.
    /// Flip to false to also inspect raw discovered objects (read-only).
    /// </summary>
    public bool StandardMode
    {
        get => _standardMode;
        set
        {
            if (SetField(ref _standardMode, value))
            {
                RebuildEntries();
            }
        }
    }

    public bool IsDiscovering
    {
        get => _isDiscovering;
        private set => SetField(ref _isDiscovering, value);
    }

    public bool HasInventory
    {
        get => _hasInventory;
        private set => SetField(ref _hasInventory, value);
    }

    /// <summary>
    /// The last classified inventory (catalog-only rows are present even before a
    /// discovery pass). The Customize knowledge tab reuses this same data so
    /// Component Intelligence acts as the backing knowledge engine.
    /// </summary>
    public ComponentInventory? Inventory => _classified;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetField(ref _summary, value);
    }

    public string Counts
    {
        get => _counts;
        private set => SetField(ref _counts, value);
    }

    public bool IsMounted =>
        _appState.CurrentServicingWorkspace?.State == ServicingWorkspaceState.Mounted;

    public bool CanDiscover => !IsDiscovering && IsMounted;

    public int CuratedCount => _classified?.Entries.Count(e => e.Classification == ComponentClassification.Curated) ?? 0;
    public int UnclassifiedCount => _classified?.Entries.Count(e => e.Classification == ComponentClassification.DiscoveredUnclassified) ?? 0;
    public int ProtectedCount => _classified?.Entries.Count(e => e.Classification == ComponentClassification.Protected) ?? 0;
    public int UnsupportedCount => _classified?.Entries.Count(e => e.Classification == ComponentClassification.Unsupported) ?? 0;

    public async Task DiscoverAsync()
    {
        if (!CanDiscover)
        {
            StatusMessage = _loc["ComponentIntelligence.NoImage"];
            return;
        }

        var workspace = _appState.CurrentServicingWorkspace!;
        IsDiscovering = true;
        StatusMessage = _loc["ComponentIntelligence.Discovering"];
        _logger.Info("ComponentIntelligence: discovery requested.");

        try
        {
            var raw = await _service.DiscoverAsync(workspace, CancellationToken.None);
            _classified = ComponentMatcher.BuildInventoryEntries(raw, _catalog.GetDefinitions());
            RebuildEntries();
            HasInventory = _classified.Discovered;

            var appx = CountCategory(raw, ComponentCategory.AppX);
            var cap = CountCategory(raw, ComponentCategory.Capability);
            var feat = CountCategory(raw, ComponentCategory.OptionalFeature);
            var pkg = CountCategory(raw, ComponentCategory.CbsPackage);
            Summary = string.Format(_loc["ComponentIntelligence.Summary"], appx, cap, feat, pkg);
            Counts = string.Format(
                _loc["ComponentIntelligence.Counts"], CuratedCount, UnclassifiedCount, ProtectedCount, UnsupportedCount);

            if (_classified.Cancelled)
            {
                StatusMessage = _loc["ComponentIntelligence.StatusCancelled"];
            }
            else if (!_classified.Discovered)
            {
                StatusMessage = _loc["ComponentIntelligence.NoImage"];
            }
            else
            {
                StatusMessage = Counts;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Discovery failed: {ex.Message}";
            _logger.Error($"ComponentIntelligence: discovery failed: {ex.Message}");
        }
        finally
        {
            IsDiscovering = false;
            Refresh();
        }
    }

    private void RebuildEntries()
    {
        var list = _classified?.Entries ?? Enumerable.Empty<ComponentInventoryEntry>();
        if (StandardMode)
        {
            list = list.Where(e => e.Classification == ComponentClassification.Curated);
        }

        Entries.Clear();
        foreach (var e in list)
        {
            Entries.Add(new ComponentListItem(e, _loc));
        }

        SelectedEntry = Entries.Count > 0 ? Entries[0] : null;

        OnPropertyChanged(nameof(CuratedCount));
        OnPropertyChanged(nameof(UnclassifiedCount));
        OnPropertyChanged(nameof(ProtectedCount));
        OnPropertyChanged(nameof(UnsupportedCount));
    }

    private static int CountCategory(ComponentInventory? raw, ComponentCategory category)
    {
        if (raw is null)
        {
            return 0;
        }

        return raw.Categories
            .Where(c => c.Category == category)
            .Sum(c => c.Items.Count);
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        // Rebuild so every getter re-resolves through the (now switched) Loc service.
        var prevSelected = SelectedEntry?.Entry.LogicalId;
        RebuildEntries();
        if (prevSelected is not null)
        {
            SelectedEntry = Entries.FirstOrDefault(x => x.Entry.LogicalId == prevSelected);
        }
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IAppState.CurrentServicingWorkspace))
        {
            Refresh();
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
