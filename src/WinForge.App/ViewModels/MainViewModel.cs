using System.Collections.ObjectModel;
using System.Windows.Input;
using WinForge.App.Mvvm;
using WinForge.App.Services;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// Root view model hosting the navigation shell. Owns the active page view
/// model (<see cref="CurrentView"/>) and the navigation rail items. It never
/// touches views directly — the XAML <c>ContentControl</c> renders the current
/// view model via data templates.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly INavigationService _navigation;
    private readonly HomeViewModel _home;
    private readonly ImageViewModel _image;
    private readonly ComponentsViewModel _components;
    private readonly PrivacyViewModel _privacy;
    private readonly SystemViewModel _system;
    private readonly PlanReviewViewModel _plan;
    private readonly LogsViewModel _logs;
    private readonly ComingSoonViewModel _comingSoon;
    private object? _currentView;

    public MainViewModel(
        INavigationService navigation,
        HomeViewModel home,
        ImageViewModel image,
        ComponentsViewModel components,
        PrivacyViewModel privacy,
        SystemViewModel system,
        PlanReviewViewModel plan,
        LogsViewModel logs,
        ComingSoonViewModel comingSoon)
    {
        _navigation = navigation;
        _home = home;
        _image = image;
        _components = components;
        _privacy = privacy;
        _system = system;
        _plan = plan;
        _logs = logs;
        _comingSoon = comingSoon;

        NavigationItems = new ObservableCollection<NavItem>
        {
            new(PageKey.Home, "Home", new RelayCommand(_ => Navigate(PageKey.Home))),
            new(PageKey.Image, "Image", new RelayCommand(_ => Navigate(PageKey.Image))),
            new(PageKey.Components, "Components", new RelayCommand(_ => Navigate(PageKey.Components))),
            new(PageKey.Experience, "Experience", new RelayCommand(_ => Navigate(PageKey.Experience))),
            new(PageKey.Privacy, "Privacy", new RelayCommand(_ => Navigate(PageKey.Privacy))),
            new(PageKey.System, "System", new RelayCommand(_ => Navigate(PageKey.System))),
            new(PageKey.Plan, "Plan", new RelayCommand(_ => Navigate(PageKey.Plan))),
            new(PageKey.Build, "Build", new RelayCommand(_ => Navigate(PageKey.Build))),
            new(PageKey.Logs, "Logs", new RelayCommand(_ => Navigate(PageKey.Logs))),
            new(PageKey.Settings, "Settings", new RelayCommand(_ => Navigate(PageKey.Settings))),
        };

        _navigation.CurrentPageChanged += OnCurrentPageChanged;

        // Establish the initial page (Home). NavigateTo is a no-op when the
        // target equals the default, so set the view/active state explicitly.
        _navigation.NavigateTo(PageKey.Home);
        CurrentView = Resolve(PageKey.Home);
        SyncActive(PageKey.Home);
    }

    public ObservableCollection<NavItem> NavigationItems { get; }

    public object? CurrentView
    {
        get => _currentView;
        private set => SetField(ref _currentView, value);
    }

    public void Navigate(PageKey page) => _navigation.NavigateTo(page);

    private void OnCurrentPageChanged(object? sender, PageKey page)
    {
        SyncActive(page);
        CurrentView = Resolve(page);
        if (page == PageKey.Plan)
        {
            _plan.Refresh();
        }
    }

    private object Resolve(PageKey page) => page switch
    {
        PageKey.Home => _home,
        PageKey.Image => _image,
        PageKey.Components => _components,
        PageKey.Privacy => _privacy,
        PageKey.System => _system,
        PageKey.Plan => _plan,
        PageKey.Logs => _logs,
        _ => PrepareComingSoon(page)
    };

    private object PrepareComingSoon(PageKey page)
    {
        _comingSoon.Title = PageTitle(page);
        return _comingSoon;
    }

    private static string PageTitle(PageKey page) => page switch
    {
        PageKey.Components => "Components",
        PageKey.Experience => "Experience",
        PageKey.Privacy => "Privacy",
        PageKey.System => "System",
        PageKey.Plan => "Plan Review",
        PageKey.Build => "Build",
        PageKey.Settings => "Settings",
        _ => "Coming soon"
    };

    private void SyncActive(PageKey page)
    {
        foreach (var item in NavigationItems)
        {
            item.IsActive = item.Key == page;
        }
    }
}
