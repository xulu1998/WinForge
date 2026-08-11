using System.Collections.ObjectModel;
using System.Windows.Input;
using WinForge.App.Mvvm;
using WinForge.Core.Models;

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
    Experience
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
/// Services / Privacy / System / Experience).
///
/// The <b>Apps</b> tab is the knowledge-backed decision surface: its content is
/// the shared ComponentKnowledgeViewModel (which reuses the Component Intelligence
/// engine). It shows curated, human-facing components with recommendation/risk
/// badges, a hover quick card, a detail panel, and decision-useful sort/filter,
/// and hides raw Windows package identity in standard mode. This replaces the
/// former separate "Component Knowledge" tab so the removal decision is made where
/// the component lives (ADR-048). Windows components / Services tabs keep the
/// discovery-backed raw lists (not yet knowledge-modeled). All view models are
/// reused singletons from Bootstrapper; no discovery or execution logic is duplicated.
/// </summary>
public sealed class CustomizeStepViewModel : ViewModelBase
{
    public ComponentsViewModel Components { get; }

    public PrivacyViewModel Privacy { get; }

    public SystemViewModel System { get; }

    public ComingSoonViewModel Experience { get; }

    public ObservableCollection<CustomizeTabViewModel> Tabs { get; }

    public ICommand DiscoverCommand { get; }

    private readonly ComponentKnowledgeViewModel _knowledge;

    private CustomizeTabViewModel? _selectedTab;

    public CustomizeTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set => SetField(ref _selectedTab, value);
    }

    public CustomizeStepViewModel(
        ComponentsViewModel components,
        PrivacyViewModel privacy,
        SystemViewModel system,
        ComingSoonViewModel experience,
        ComponentKnowledgeViewModel knowledge)
    {
        Components = components;
        Privacy = privacy;
        System = system;
        Experience = experience;
        _knowledge = knowledge ?? throw new ArgumentNullException(nameof(knowledge));
        DiscoverCommand = components.DiscoverCommand;

        Tabs = new ObservableCollection<CustomizeTabViewModel>
        {
            // Apps = knowledge-backed decision surface (reuses CI engine; curated only).
            new CustomizeTabViewModel("Customize.Tab.Apps", _knowledge, CustomizeTabKind.ComponentList),
            new CustomizeTabViewModel("Customize.Tab.Components",
                new ComponentListTabViewModel(components, ComponentListKind.Components, "Customize.Tab.Components"),
                CustomizeTabKind.ComponentList),
            new CustomizeTabViewModel("Customize.Tab.Services",
                new ComponentListTabViewModel(components, ComponentListKind.Services, "Customize.Tab.Services"),
                CustomizeTabKind.ComponentList),
            new CustomizeTabViewModel("Customize.Tab.Privacy", privacy, CustomizeTabKind.Privacy),
            new CustomizeTabViewModel("Customize.Tab.System", system, CustomizeTabKind.System),
            new CustomizeTabViewModel("Customize.Tab.Experience", experience, CustomizeTabKind.Experience),
        };

        SelectedTab = Tabs[0];
    }
}
