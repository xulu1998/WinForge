using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using WinForge.App.Mvvm;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// Privacy page (Step 3.3 sections H, K). Presents curated, stable, documented,
/// offline-applicable registry-backed privacy toggles sourced only from WinForge's
/// trusted definition provider — never from signed-in user context, cloud/account
/// policy, or internet folklore. Each selection produces a SetOfflineRegistryValue
/// plan operation; the VM never edits the host registry directly.
/// </summary>
public sealed class PrivacyViewModel : ViewModelBase
{
    private readonly IAppState _appState;
    private readonly ILoggerService _logger;
    private readonly ICustomizationDefinitionProvider _definitions;

    public PrivacyViewModel(
        IAppState appState,
        ILoggerService logger,
        ICustomizationDefinitionProvider definitions)
    {
        _appState = appState;
        _logger = logger;
        _definitions = definitions;

        Settings = new ObservableCollection<RegistrySettingItem>();
        LoadCommand = new RelayCommand(_ => LoadDefinitions());
        _appState.PropertyChanged += OnAppStateChanged;
        LoadDefinitions();
    }

    public ICommand LoadCommand { get; }

    public ObservableCollection<RegistrySettingItem> Settings { get; }

    public int SelectedTotal => CountSelected(Settings);

    public void LoadDefinitions()
    {
        Settings.Clear();
        foreach (var setting in _definitions.GetPrivacySettings())
        {
            var item = new RegistrySettingItem(setting) { IsSelected = IsSelectedInPlan("reg|" + setting.SettingId) };
            item.PropertyChanged += OnSelectionChanged;
            Settings.Add(item);
        }

        OnPropertyChanged(nameof(SelectedTotal));
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RegistrySettingItem.IsSelected) || sender is not RegistrySettingItem item)
        {
            return;
        }

        var id = "reg|" + item.Setting.SettingId;
        PlanSync.Toggle(_appState, id, item.IsSelected, () => new CustomizationOperation
        {
            OperationId = id,
            Category = CustomizationCategory.Privacy,
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

    private static int CountSelected(ObservableCollection<RegistrySettingItem> items)
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
            // Re-sync checkbox state if the plan was reset/replaced.
            foreach (var item in Settings)
            {
                item.IsSelected = IsSelectedInPlan("reg|" + item.Setting.SettingId);
            }

            OnPropertyChanged(nameof(SelectedTotal));
        }
    }
}
