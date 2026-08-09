using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using WinForge.App.Mvvm;
using WinForge.App.Services;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// Components page (Step 3.3 section J). Becomes discovery-backed: it shows
/// provisioned Appx packages, Windows packages, and offline system components
/// (services) discovered from the mounted working image, each with a status, a
/// removable/supported flag, a selectable checkbox, and a reason when protected.
/// Unsupported items are not selectable. Selections produce declarative plan
/// operations; the VM never calls DISM directly — only the execution engine does.
/// </summary>
public sealed class ComponentsViewModel : ViewModelBase
{
    private readonly IAppState _appState;
    private readonly ILoggerService _logger;
    private readonly ICustomizationDiscoveryService _discovery;
    private readonly ICustomizationDefinitionProvider _definitions;

    private bool _isDiscovering;
    private bool _hasInventory;
    private string _statusMessage = "Select and mount a working image, then discover components.";

    public ComponentsViewModel(
        IAppState appState,
        ILoggerService logger,
        ICustomizationDiscoveryService discovery,
        ICustomizationDefinitionProvider definitions)
    {
        _appState = appState;
        _logger = logger;
        _discovery = discovery;
        _definitions = definitions;

        AppxPackages = new ObservableCollection<AppxSelectionItem>();
        WindowsPackages = new ObservableCollection<PackageSelectionItem>();
        Services = new ObservableCollection<ServiceSelectionItem>();

        DiscoverCommand = new AsyncRelayCommand(_ => DiscoverAsync(), _ => CanDiscover);
        _appState.PropertyChanged += OnAppStateChanged;
    }

    public ICommand DiscoverCommand { get; }

    public ObservableCollection<AppxSelectionItem> AppxPackages { get; }
    public ObservableCollection<PackageSelectionItem> WindowsPackages { get; }
    public ObservableCollection<ServiceSelectionItem> Services { get; }

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

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsMounted =>
        _appState.CurrentServicingWorkspace?.State == ServicingWorkspaceState.Mounted;

    public int SelectedTotal =>
        CountSelected(AppxPackages) + CountSelected(WindowsPackages) + CountSelected(Services);

    public bool CanDiscover => !IsDiscovering && IsMounted;

    public async Task DiscoverAsync()
    {
        if (!CanDiscover)
        {
            StatusMessage = "Discovery requires a mounted working image.";
            return;
        }

        var workspace = _appState.CurrentServicingWorkspace!;
        IsDiscovering = true;
        StatusMessage = "Discovering components from the mounted image…";
        _logger.Info("Components: discovery requested.");
        try
        {
            var inventory = await _discovery.DiscoverAsync(workspace, CancellationToken.None);
            _appState.DiscoveredInventory = inventory;
            BuildCollections(inventory);
            HasInventory = inventory.Discovered;

            // Surface per-source failures explicitly. A failed DISM call or a
            // failed offline hive load must be shown as an error, never collapsed
            // into a misleading "0 discovered" line.
            var errors = new List<string>();
            if (inventory.AppxStatus == DiscoverySourceStatus.Failed)
            {
                errors.Add($"Appx discovery failed: {inventory.AppxError}");
            }
            if (inventory.PackageStatus == DiscoverySourceStatus.Failed)
            {
                errors.Add($"Package discovery failed: {inventory.PackageError}");
            }
            if (inventory.ServiceStatus == DiscoverySourceStatus.Failed)
            {
                errors.Add($"Service discovery failed: {inventory.ServiceError}");
            }

            var summary = inventory.Discovered
                ? $"Discovered {AppxPackages.Count} app(s), {WindowsPackages.Count} package(s), {Services.Count} service(s)."
                : "Discovery found no usable mounted session.";

            StatusMessage = errors.Count > 0
                ? string.Join(" ", errors) + " " + summary
                : summary;
        }
        catch (System.Exception ex)
        {
            StatusMessage = $"Discovery failed: {ex.Message}";
            _logger.Error($"Components: discovery failed: {ex.Message}");
        }
        finally
        {
            IsDiscovering = false;
            Refresh();
        }
    }

    private void BuildCollections(DiscoveryInventory inventory)
    {
        AppxPackages.Clear();
        WindowsPackages.Clear();
        Services.Clear();

        foreach (var appx in inventory.AppxPackages)
        {
            var item = new AppxSelectionItem(appx) { IsSelected = IsSelectedInPlan("appx|" + appx.PackageName) };
            item.PropertyChanged += OnSelectionChanged;
            AppxPackages.Add(item);
        }

        foreach (var pkg in inventory.WindowsPackages)
        {
            var item = new PackageSelectionItem(pkg) { IsSelected = IsSelectedInPlan("pkg|" + pkg.PackageIdentity) };
            item.PropertyChanged += OnSelectionChanged;
            WindowsPackages.Add(item);
        }

        foreach (var svc in inventory.Services)
        {
            var item = new ServiceSelectionItem(svc, ServiceStartType.Disabled)
            {
                IsSelected = IsSelectedInPlan("svc|" + svc.ServiceName)
            };
            item.PropertyChanged += OnSelectionChanged;
            Services.Add(item);
        }
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppxSelectionItem.IsSelected) &&
            e.PropertyName != nameof(PackageSelectionItem.IsSelected) &&
            e.PropertyName != nameof(ServiceSelectionItem.IsSelected))
        {
            return;
        }

        switch (sender)
        {
            case AppxSelectionItem appx:
                SyncAppx(appx);
                break;
            case PackageSelectionItem pkg:
                SyncPackage(pkg);
                break;
            case ServiceSelectionItem svc:
                SyncService(svc);
                break;
        }

        OnPropertyChanged(nameof(SelectedTotal));
    }

    private void SyncAppx(AppxSelectionItem item)
    {
        var id = "appx|" + item.Package.PackageName;
        PlanSync.Toggle(_appState, id, item.IsSelected, () => new CustomizationOperation
        {
            OperationId = id,
            Category = CustomizationCategory.App,
            OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            DisplayName = item.Package.DisplayName,
            Description = $"Remove provisioned Appx package {item.Package.PackageName}.",
            TargetIdentifier = item.Package.PackageName,
            Risk = item.Package.Risk,
            ExecutionOrder = 0
        });
    }

    private void SyncPackage(PackageSelectionItem item)
    {
        var id = "pkg|" + item.Package.PackageIdentity;
        PlanSync.Toggle(_appState, id, item.IsSelected, () => new CustomizationOperation
        {
            OperationId = id,
            Category = CustomizationCategory.Package,
            OperationType = CustomizationOperationType.RemovePackage,
            DisplayName = item.Package.DisplayName,
            Description = $"Remove Windows package {item.Package.PackageIdentity}.",
            TargetIdentifier = item.Package.PackageIdentity,
            Risk = item.Package.Risk,
            ExecutionOrder = 0
        });
    }

    private void SyncService(ServiceSelectionItem item)
    {
        var id = "svc|" + item.Service.ServiceName;
        PlanSync.Toggle(_appState, id, item.IsSelected, () => new CustomizationOperation
        {
            OperationId = id,
            Category = CustomizationCategory.Service,
            OperationType = CustomizationOperationType.ConfigureOfflineService,
            DisplayName = $"Set {item.Service.DisplayName} to {item.RecommendedStartType}",
            Description = $"Configure offline service {item.Service.ServiceName} to {item.RecommendedStartType}.",
            ServiceName = item.Service.ServiceName,
            ServiceStartType = item.RecommendedStartType,
            Risk = item.Service.Risk,
            ExecutionOrder = 0
        });
    }

    private bool IsSelectedInPlan(string operationId)
        => _appState.CurrentCustomizationPlan?.Operations.Any(o => o.OperationId == operationId && o.IsSelected) ?? false;

    private static int CountSelected<T>(ObservableCollection<T> items)
        where T : ViewModelBase
    {
        var count = 0;
        foreach (var it in items)
        {
            if (it is AppxSelectionItem a && a.IsSelected) count++;
            else if (it is PackageSelectionItem p && p.IsSelected) count++;
            else if (it is ServiceSelectionItem s && s.IsSelected) count++;
        }

        return count;
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IAppState.CurrentServicingWorkspace) or nameof(IAppState.CurrentCustomizationPlan))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsMounted));
        OnPropertyChanged(nameof(CanDiscover));
        OnPropertyChanged(nameof(SelectedTotal));
        if (DiscoverCommand is AsyncRelayCommand cmd)
        {
            cmd.RaiseCanExecuteChanged();
        }
    }
}
