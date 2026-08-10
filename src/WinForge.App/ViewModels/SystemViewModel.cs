using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using WinForge.App.FriendlyMetadata;
using WinForge.App.Mvvm;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// System page (Step 3.3 sections I, G, K). Offers only safe, offline
/// service/registry changes sourced from WinForge's trusted definition provider:
/// recommended service start-type changes and a small set of machine-policy
/// registry tweaks. There is deliberately no boot config, partitioning, driver,
/// kernel-patch, or blanket security-disabling surface here. Selections produce
/// declarative plan operations; the VM never touches the host system directly.
/// </summary>
public sealed class SystemViewModel : ViewModelBase
{
    private readonly IAppState _appState;
    private readonly ILoggerService _logger;
    private readonly ICustomizationDefinitionProvider _definitions;
    private readonly IFriendlyMetadataProvider? _friendly;

    public SystemViewModel(
        IAppState appState,
        ILoggerService logger,
        ICustomizationDefinitionProvider definitions,
        IFriendlyMetadataProvider? friendly = null)
    {
        _appState = appState;
        _logger = logger;
        _definitions = definitions;
        _friendly = friendly;

        RecommendedServices = new ObservableCollection<ServiceSelectionItem>();
        RegistrySettings = new ObservableCollection<RegistrySettingItem>();
        LoadCommand = new RelayCommand(_ => LoadDefinitions());
        _appState.PropertyChanged += OnAppStateChanged;
        LoadDefinitions();
    }

    public ICommand LoadCommand { get; }

    public ObservableCollection<ServiceSelectionItem> RecommendedServices { get; }
    public ObservableCollection<RegistrySettingItem> RegistrySettings { get; }

    public int SelectedTotal => CountSelectedServices(RecommendedServices) + CountSelectedSettings(RegistrySettings);

    public void LoadDefinitions()
    {
        RecommendedServices.Clear();
        foreach (var svc in _definitions.GetRecommendedServiceChanges())
        {
            var target = svc.RecommendedStartType ?? ServiceStartType.Disabled;
            var item = new ServiceSelectionItem(svc, target, _friendly?.GetServiceFriendlyName(svc.ServiceName))
            {
                IsSelected = IsSelectedInPlan("svc|" + svc.ServiceName)
            };
            item.PropertyChanged += OnServiceSelectionChanged;
            RecommendedServices.Add(item);
        }

        RegistrySettings.Clear();
        foreach (var setting in _definitions.GetSystemSettings())
        {
            var item = new RegistrySettingItem(setting) { IsSelected = IsSelectedInPlan("reg|" + setting.SettingId) };
            item.PropertyChanged += OnSettingSelectionChanged;
            RegistrySettings.Add(item);
        }

        OnPropertyChanged(nameof(SelectedTotal));
    }

    private void OnServiceSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ServiceSelectionItem.IsSelected) || sender is not ServiceSelectionItem item)
        {
            return;
        }

        var id = "svc|" + item.Service.ServiceName;
        PlanSync.Toggle(_appState, id, item.IsSelected, () => new CustomizationOperation
        {
            OperationId = id,
            Category = CustomizationCategory.System,
            OperationType = CustomizationOperationType.ConfigureOfflineService,
            DisplayName = $"Set {item.Service.DisplayName} to {item.RecommendedStartType}",
            Description = $"Configure offline service {item.Service.ServiceName} to {item.RecommendedStartType}.",
            ServiceName = item.Service.ServiceName,
            ServiceStartType = item.RecommendedStartType,
            Risk = item.Service.Risk,
            ExecutionOrder = 0
        });

        OnPropertyChanged(nameof(SelectedTotal));
    }

    private void OnSettingSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RegistrySettingItem.IsSelected) || sender is not RegistrySettingItem item)
        {
            return;
        }

        var id = "reg|" + item.Setting.SettingId;
        PlanSync.Toggle(_appState, id, item.IsSelected, () => new CustomizationOperation
        {
            OperationId = id,
            Category = CustomizationCategory.System,
            OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            DisplayName = item.Setting.Title,
            Description = item.Setting.Description,
            RegistryHive = item.Setting.Hive,
            RegistryKeyPath = item.Setting.KeyPath,
            RegistryValueName = item.Setting.ValueName,
            RegistryValueKind = item.Setting.ValueKind,
            RegistryValueData = item.Setting.RecommendedData,
            Risk = item.Setting.Risk,
            ExecutionOrder = 0
        });

        OnPropertyChanged(nameof(SelectedTotal));
    }

    private bool IsSelectedInPlan(string operationId)
        => _appState.CurrentCustomizationPlan?.Operations.Any(o => o.OperationId == operationId && o.IsSelected) ?? false;

    private static int CountSelectedServices(ObservableCollection<ServiceSelectionItem> items)
    {
        var count = 0;
        foreach (var it in items)
        {
            if (it.IsSelected) count++;
        }

        return count;
    }

    private static int CountSelectedSettings(ObservableCollection<RegistrySettingItem> items)
    {
        var count = 0;
        foreach (var it in items)
        {
            if (it.IsSelected) count++;
        }

        return count;
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAppState.CurrentCustomizationPlan))
        {
            foreach (var item in RecommendedServices)
            {
                item.IsSelected = IsSelectedInPlan("svc|" + item.Service.ServiceName);
            }

            foreach (var item in RegistrySettings)
            {
                item.IsSelected = IsSelectedInPlan("reg|" + item.Setting.SettingId);
            }

            OnPropertyChanged(nameof(SelectedTotal));
        }
    }
}
