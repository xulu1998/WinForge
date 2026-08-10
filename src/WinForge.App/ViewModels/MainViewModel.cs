using System.Collections.ObjectModel;
using System.Windows.Input;
using WinForge.App.Mvvm;
using WinForge.App.Services;
using WinForge.App.Workflow;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// Root view model hosting the application shell. The primary surface is the
/// sequential <see cref="WorkflowViewModel"/> (the Wizard/Stepper). A small utility
/// rail provides non-workflow pages (Home / Logs / Settings / About) that are
/// orthogonal to the workflow. The shell never touches views directly — the XAML
/// <c>ContentControl</c> renders the active view model via data templates.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly HomeViewModel _home;
    private readonly LogsViewModel _logs;
    private readonly SettingsViewModel _settings;
    private readonly AboutViewModel _about;
    private readonly ComingSoonViewModel _comingSoon;
    private readonly WorkflowViewModel _workflow;
    private readonly ComponentIntelligenceViewModel _componentIntelligence;
    private readonly INavigationService _navigation;

    private object? _activeView;
    private bool _isWorkflowActive = true;
    private PageKey _currentUtility = PageKey.Home;

    public MainViewModel(
        INavigationService navigation,
        HomeViewModel home,
        LogsViewModel logs,
        SettingsViewModel settings,
        AboutViewModel about,
        ComingSoonViewModel comingSoon,
        WorkflowViewModel workflow,
        ComponentIntelligenceViewModel componentIntelligence)
    {
        _home = home;
        _logs = logs;
        _settings = settings;
        _about = about;
        _comingSoon = comingSoon;
        _workflow = workflow;
        _componentIntelligence = componentIntelligence;
        _navigation = navigation;

        // Legacy pages (Home's "Select image", etc.) navigate through INavigationService.
        // Translate those requests onto the wizard or a utility page so the old page keys
        // still drive the new shell without the removed feature-list nav.
        _navigation.CurrentPageChanged += OnNavigated;

        // Titles are localization keys; the XAML binds them through the Loc service.
        // Every rail entry funnels through the single navigation coordinator so the
        // service's CurrentPage always matches the visible surface.
        UtilityItems = new ObservableCollection<NavItem>
        {
            new(PageKey.Home, "Nav.Home", new RelayCommand(_ => _navigation.NavigateTo(PageKey.Home))),
            new(PageKey.Logs, "Nav.Logs", new RelayCommand(_ => _navigation.NavigateTo(PageKey.Logs))),
            new(PageKey.Settings, "Nav.Settings", new RelayCommand(_ => _navigation.NavigateTo(PageKey.Settings))),
            new(PageKey.About, "Nav.About", new RelayCommand(_ => _navigation.NavigateTo(PageKey.About))),
            new(PageKey.ComponentIntelligence, "Nav.ComponentIntelligence", new RelayCommand(_ => _navigation.NavigateTo(PageKey.ComponentIntelligence))),
        };

        ShowWorkflowCommand = new RelayCommand(_ => _navigation.NavigateTo(PageKey.Workflow));
        ShowUtilityCommand = new RelayCommand(p => _navigation.NavigateTo((PageKey)p!));

        // Show the wizard by navigating through the coordinator. This keeps
        // INavigationService.CurrentPage in sync with the visible surface — without
        // it, the wizard was displayed directly while CurrentPage stayed at its
        // initial "Home", so a later Finish() -> NavigateTo(Home) was a no-op and
        // the wizard never disappeared.
        _navigation.NavigateTo(PageKey.Workflow);
    }

    public WorkflowViewModel Workflow => _workflow;

    public ObservableCollection<NavItem> UtilityItems { get; }

    public object? ActiveView
    {
        get => _activeView;
        private set => SetField(ref _activeView, value);
    }

    public bool IsWorkflowActive
    {
        get => _isWorkflowActive;
        private set => SetField(ref _isWorkflowActive, value);
    }

    public ICommand ShowWorkflowCommand { get; }

    public ICommand ShowUtilityCommand { get; }

    private void ShowWorkflow()
    {
        IsWorkflowActive = true;
        ActiveView = _workflow;
        SyncActive();
    }

    private void ShowUtility(PageKey page)
    {
        IsWorkflowActive = false;
        _currentUtility = page;
        ActiveView = ResolveUtility(page);
        SyncActive();
    }

    private object ResolveUtility(PageKey page) => page switch
    {
        PageKey.Home => _home,
        PageKey.Logs => _logs,
        PageKey.Settings => _settings,
        PageKey.About => _about,
        PageKey.ComponentIntelligence => _componentIntelligence,
        _ => _comingSoon
    };

    private void SyncActive()
    {
        foreach (var item in UtilityItems)
        {
            item.IsActive = !IsWorkflowActive && item.Key == _currentUtility;
        }

        OnPropertyChanged(nameof(IsWorkflowActive));
    }

    /// <summary>
    /// Translates a legacy <see cref="INavigationService"/> page change onto the new
    /// shell. Utility pages (Home / Logs / Settings / About) are shown directly; the
    /// old feature pages (Image / Components / …) jump into the matching workflow step
    /// so deep links from the Home page still work.
    /// </summary>
    private void OnNavigated(object? sender, PageKey page)
    {
        switch (page)
        {
            case PageKey.Home:
            case PageKey.Logs:
            case PageKey.Settings:
            case PageKey.About:
            case PageKey.ComponentIntelligence:
                ShowUtility(page);
                break;
            case PageKey.Workflow:
                // The wizard surface. Do NOT reset the active step here — this is the
                // "show the wizard" entry point, not a deep link to a specific step.
                ShowWorkflow();
                break;
            default:
                ShowWorkflow();
                var step = page switch
                {
                    PageKey.Image => WorkflowStep.Source,
                    PageKey.Components => WorkflowStep.Customize,
                    PageKey.Privacy => WorkflowStep.Customize,
                    PageKey.System => WorkflowStep.Customize,
                    PageKey.Experience => WorkflowStep.Customize,
                    PageKey.Plan => WorkflowStep.Review,
                    PageKey.Build => WorkflowStep.Build,
                    _ => WorkflowStep.Source
                };
                _workflow.GoToStep(step);
                break;
        }
    }
}
