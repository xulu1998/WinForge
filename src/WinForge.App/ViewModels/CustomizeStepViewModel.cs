using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using WinForge.App.Mvvm;
using WinForge.App.Services;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>Which collection a <see cref="ComponentListTabViewModel"/> presents.</summary>
public enum ComponentListKind
{
    Apps,
    Components,
    Services
}

/// <summary>Discriminator for a customize tab's content type (drives the XAML template).</summary>
public enum CustomizeTabKind
{
    ComponentList,
    Privacy,
    System,
    Personalization
}

/// <summary>
/// One tab in the Customize step. The header is a localization key; the content
/// is the view model rendered for the tab (a component list or a full page VM).
/// </summary>
public sealed class CustomizeTabViewModel : ViewModelBase
{
    public string HeaderKey { get; }

    public object Content { get; }

    public CustomizeTabKind Kind { get; }

    public CustomizeTabViewModel(string headerKey, object content, CustomizeTabKind kind)
    {
        HeaderKey = headerKey;
        Content = content;
        Kind = kind;
    }
}

/// <summary>
/// Presents one of the three discovery-backed collections owned by the shared
/// <see cref="ComponentsViewModel"/>, filtered to a single <see cref="ComponentListKind"/>.
/// Reuses the single discovery pass — no duplicate discovery logic.
/// </summary>
public sealed class ComponentListTabViewModel : ViewModelBase
{
    public ComponentsViewModel Components { get; }

    public ComponentListKind Kind { get; }

    public string HeaderKey { get; }

    // IList (not IEnumerable) so the view can build a ListCollectionView over the
    // LIVE ObservableCollection. The three backing collections are all
    // ObservableCollection<T>, which implement IList and raise INotifyCollectionChanged
    // in place — so post-discovery Clear()/Add() flows straight into the visible list.
    public System.Collections.IList Items => Kind switch
    {
        ComponentListKind.Apps => Components.AppxPackages,
        ComponentListKind.Components => Components.WindowsPackages,
        _ => Components.Services
    };

    /// <summary>The "show protected entries" toggle is only meaningful for services.</summary>
    public bool ShowProtectedVisible => Kind == ComponentListKind.Services;

    public ComponentListTabViewModel(ComponentsViewModel components, ComponentListKind kind, string headerKey)
    {
        Components = components;
        Kind = kind;
        HeaderKey = headerKey;
    }
}

/// <summary>
/// Customize step coordinator. Hosts the six tabs (Apps / Windows components /
/// Services / Privacy / System / Personalization).
///
/// <para>Stage 11.3 (ADR-051/ADR-052): every tab is now a knowledge-backed decision
/// surface. The <b>Apps</b> and <b>Windows components</b> tabs reuse the shared
/// <see cref="ComponentKnowledgeViewModel"/> engine over the Component Intelligence
/// classified inventory (AppX vs capabilities/optional-features — one discovery,
/// one engine, two category filters). <b>Services / Privacy / System /
/// Personalization</b> share the catalog-driven <see cref="OptimizationKnowledgeViewModel"/>
/// (one engine, four catalogs). The former "Experience / Coming Soon" placeholder
/// is replaced by the real Personalization tab (ADR-054).</para>
///
/// All view models are reused singletons from Bootstrapper; no discovery or
/// execution logic is duplicated.
/// </summary>
public sealed class CustomizeStepViewModel : ViewModelBase
{
    public ComponentsViewModel Components { get; }

    public ObservableCollection<CustomizeTabViewModel> Tabs { get; }

    public ICommand DiscoverCommand { get; }

    /// <summary>
    /// Total selected changes across ALL surfaces (knowledge rows write straight
    /// into the shared plan). Drives the header "已选 N 项". Refreshes via
    /// <see cref="PlanSync.PlanChanged"/> so profile auto-apply and manual toggles
    /// both update the count immediately.
    /// </summary>
    public int SelectedTotal =>
        Components.AppState.CurrentCustomizationPlan?.Operations.Count(o => o.IsSelected) ?? 0;

    private bool _isDiscovering;

    /// <summary>True while the unified discovery pass is running (Components + knowledge).</summary>
    public bool IsDiscovering
    {
        get => _isDiscovering;
        private set => SetField(ref _isDiscovering, value);
    }

    /// <summary>One mounted image → one coherent discovery (Components + Component Intelligence).</summary>
    public bool CanDiscover => !IsDiscovering && Components.IsMounted;

    private readonly ComponentKnowledgeViewModel _knowledge;
    private readonly ComponentKnowledgeViewModel _componentsKnowledge;
    private readonly RecommendationContextService? _profileCtx;

    private CustomizeTabViewModel? _selectedTab;

    public CustomizeTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set => SetField(ref _selectedTab, value);
    }

    /// <summary>
    /// Stage 11.4 profile selector (recommended configuration engine). Null when
    /// no recommendation context is wired (plain Stage 11.3 behavior); the
    /// Customize view collapses the profile panel in that case.
    /// </summary>
    public ProfileViewModel? Profiles { get; }

    public CustomizeStepViewModel(
        ComponentsViewModel components,
        ComponentKnowledgeViewModel knowledge,
        ComponentKnowledgeViewModel componentsKnowledge,
        OptimizationKnowledgeViewModel servicesKnowledge,
        OptimizationKnowledgeViewModel privacyKnowledge,
        OptimizationKnowledgeViewModel systemKnowledge,
        OptimizationKnowledgeViewModel personalizationKnowledge,
        RecommendationContextService? profileCtx = null,
        ILocalizationService? loc = null)
    {
        Components = components ?? throw new System.ArgumentNullException(nameof(components));
        _knowledge = knowledge ?? throw new System.ArgumentNullException(nameof(knowledge));
        _componentsKnowledge = componentsKnowledge ?? throw new System.ArgumentNullException(nameof(componentsKnowledge));
        _profileCtx = profileCtx;

        if (profileCtx is not null)
        {
            if (loc is null)
            {
                throw new System.ArgumentNullException(nameof(loc));
            }

            Profiles = new ProfileViewModel(profileCtx, loc, AllSubjects, RecomputeAll);
        }

        // ADR-049: ONE Discover button drives a single coherent, read-only discovery
        // pass — the existing Components discovery (Apps/Windows components/Services)
        // AND the Component Intelligence knowledge discovery (curated classification).
        // The user never has to discover twice for two different systems.
        DiscoverCommand = new AsyncRelayCommand(_ => DiscoverAllAsync(), _ => CanDiscover);
        // Header "已选 N 项" tracks the shared plan (profile auto-apply + manual).
        PlanSync.PlanChanged += (_, _) => OnPropertyChanged(nameof(SelectedTotal));
        components.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ComponentsViewModel.IsMounted))
            {
                OnPropertyChanged(nameof(CanDiscover));
                if (DiscoverCommand is AsyncRelayCommand cmd)
                {
                    cmd.RaiseCanExecuteChanged();
                }
            }
        };

        Tabs = new ObservableCollection<CustomizeTabViewModel>
        {
            // Apps = knowledge-backed decision surface (CI engine, AppX category only).
            new CustomizeTabViewModel("Customize.Tab.Apps", knowledge, CustomizeTabKind.ComponentList),
            // Windows components = SAME knowledge engine, capability/optional-feature
            // category only (Stage 11.3, ADR-051).
            new CustomizeTabViewModel("Customize.Tab.Components", componentsKnowledge, CustomizeTabKind.ComponentList),
            // Services / Privacy / System / Personalization = catalog-driven
            // knowledge surfaces (one shared engine, four catalogs — ADR-051/ADR-052).
            new CustomizeTabViewModel("Customize.Tab.Services", servicesKnowledge, CustomizeTabKind.ComponentList),
            new CustomizeTabViewModel("Customize.Tab.Privacy", privacyKnowledge, CustomizeTabKind.Privacy),
            new CustomizeTabViewModel("Customize.Tab.System", systemKnowledge, CustomizeTabKind.System),
            new CustomizeTabViewModel("Customize.Tab.Personalization", personalizationKnowledge, CustomizeTabKind.Personalization),
        };

        SelectedTab = Tabs[0];
    }

    /// <summary>
    /// One coherent, read-only discovery pass: runs the existing Components discovery
    /// (Apps / Windows components / Services) AND the Component Intelligence knowledge
    /// discovery (curated classification) so a single Discover button populates every
    /// image-backed Customize tab. Both passes are read-only — no destructive
    /// servicing is performed (ADR-049). The Apps + Windows components knowledge tabs
    /// rebuild from the shared classified inventory. The catalog-driven tabs
    /// (Services / Privacy / System / Personalization) are always populated.
    /// </summary>
    private async Task DiscoverAllAsync()
    {
        if (!CanDiscover)
        {
            return;
        }

        IsDiscovering = true;
        OnPropertyChanged(nameof(CanDiscover));
        if (DiscoverCommand is AsyncRelayCommand cmd)
        {
            cmd.RaiseCanExecuteChanged();
        }

        try
        {
            await Components.DiscoverAsync();
            await _knowledge.DiscoverAsync();
            // Stage 11.3 real-desktop defect fix: the Windows Components knowledge
            // tab reuses the SAME classified inventory (one DISM pass) but must be
            // explicitly refreshed — otherwise it stays in its pre-discovery
            // empty state and shows zero rows after Discover.
            _componentsKnowledge.RefreshFromInventory();
            // Stage 11.4: re-evaluate recommendations against the freshly
            // discovered image contents (Part O).
            RecomputeAll();
        }
        finally
        {
            IsDiscovering = false;
            OnPropertyChanged(nameof(CanDiscover));
            if (DiscoverCommand is AsyncRelayCommand c)
            {
                c.RaiseCanExecuteChanged();
            }
        }
    }

    // ---- Stage 11.4 — profile recompute plumbing ----

    /// <summary>Every knowledge row across all six tabs, as recommendation subjects.</summary>
    private IEnumerable<IRecommendationSubject> AllSubjects()
    {
        foreach (var tab in Tabs)
        {
            switch (tab.Content)
            {
                case ComponentKnowledgeViewModel k:
                    foreach (var s in k.RecommendationSubjects)
                    {
                        yield return s;
                    }

                    break;
                case OptimizationKnowledgeViewModel o:
                    foreach (var s in o.RecommendationSubjects)
                    {
                        yield return s;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Recomputes present-ids (Part O), then every tab's effective recommendation
    /// from the shared profile context. Never mutates selections (Part K).
    /// </summary>
    private void RecomputeAll()
    {
        if (_profileCtx is null || Profiles is null)
        {
            return;
        }

        // Phase 14.3 (ADR-088/089): derive the knowledge-driven gaming context from
        // the CURRENT profile selection BEFORE rows recompute, so each row's gaming
        // decision reflects the active primary (Gaming PC / Dedicated Gaming) and
        // the selected extras (Xbox / WSL / printing / touch / remote).
        PushGamingContext();

        _profileCtx.SetPresentIds(AllSubjects().Where(s => s.IsPresent).Select(s => s.LogicalId));
        foreach (var tab in Tabs)
        {
            switch (tab.Content)
            {
                case ComponentKnowledgeViewModel k:
                    k.RecomputeRecommendations();
                    break;
                case OptimizationKnowledgeViewModel o:
                    o.RecomputeRecommendations();
                    break;
            }
        }

        Profiles.RefreshSummary();
    }

    /// <summary>
    /// Pushes the active gaming kind + extras into both knowledge tabs. Only a
    /// primary profile with <see cref="WinForge.Core.Profiles.ProfileDefinition.GamingKind"/>
    /// activates the pipeline; extras map 1:1 to
    /// <see cref="WinForge.Core.Profiles.GamingExtra"/>.
    /// </summary>
    private void PushGamingContext()
    {
        if (_profileCtx is null)
        {
            return;
        }

        var selected = _profileCtx.SelectedProfiles;
        var gamingProfile = selected.FirstOrDefault(p => p.GamingKind is not null);
        var kind = gamingProfile is null ? null : gamingProfile.GamingKind;
        var extras = new HashSet<WinForge.Core.Profiles.GamingExtra>();
        foreach (var p in selected)
        {
            var extra = p.Id switch
            {
                "XboxGamePass" => WinForge.Core.Profiles.GamingExtra.XboxGamePass,
                "WslDocker" => WinForge.Core.Profiles.GamingExtra.WslDocker,
                "PrintingScanning" => WinForge.Core.Profiles.GamingExtra.PrintScan,
                "TouchPen" => WinForge.Core.Profiles.GamingExtra.TouchPen,
                "RemoteDesktop" => WinForge.Core.Profiles.GamingExtra.RemoteDesktop,
                _ => (WinForge.Core.Profiles.GamingExtra?)null,
            };
            if (extra is not null)
            {
                extras.Add(extra.Value);
            }
        }

        _knowledge.SetGamingContext(kind, extras);
        _componentsKnowledge.SetGamingContext(kind, extras);
    }
}
