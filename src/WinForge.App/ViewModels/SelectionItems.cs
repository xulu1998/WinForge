using System.ComponentModel;
using WinForge.App.Mvvm;
using WinForge.Core.Models;

namespace WinForge.App.ViewModels;

/// <summary>
/// UI-side wrapper that binds a discovered Appx package to a selectable checkbox.
/// Selection drives a <see cref="CustomizationOperation"/> (RemoveProvisionedAppx)
/// in the shared plan. Protected / unsupported packages cannot be selected.
/// </summary>
public sealed class AppxSelectionItem : ViewModelBase
{
    public DiscoveredAppxPackage Package { get; }

    public AppxSelectionItem(DiscoveredAppxPackage package) => Package = package;

    public bool CanSelect => Package.Risk is RiskClass.Safe or RiskClass.Removable;

    public string Reason => CanSelect
        ? string.Empty
        : "Protected or unsupported — cannot be removed.";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
}

/// <summary>
/// UI-side wrapper for a discovered Windows package. Selection drives a
/// RemovePackage operation; protected packages (language, core, driver) cannot be
/// selected and show a reason.
/// </summary>
public sealed class PackageSelectionItem : ViewModelBase
{
    public DiscoveredWindowsPackage Package { get; }

    public PackageSelectionItem(DiscoveredWindowsPackage package) => Package = package;

    public bool CanSelect => Package.Risk == RiskClass.Removable;

    public string Reason => CanSelect
        ? string.Empty
        : $"Classified {Package.Classification}; removal is not permitted by this step.";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
}

/// <summary>
/// UI-side wrapper for an offline service. Selection drives a
/// ConfigureOfflineService operation (set to <see cref="RecommendedStartType"/>).
/// </summary>
public sealed class ServiceSelectionItem : ViewModelBase
{
    public DiscoveredOfflineService Service { get; }

    /// <summary>The start type this selection will configure the service to.</summary>
    public ServiceStartType RecommendedStartType { get; }

    public ServiceSelectionItem(DiscoveredOfflineService service, ServiceStartType recommended)
    {
        Service = service;
        RecommendedStartType = recommended;
    }

    public bool CanSelect => Service.ServiceKind is ServiceClass.RecommendedConfigurable or ServiceClass.Configurable;

    public string Reason => Service.ServiceKind switch
    {
        ServiceClass.Driver => "Kernel / file-system driver — cannot be reconfigured by this step.",
        ServiceClass.Protected => "Not an approved service for this step — cannot be reconfigured.",
        ServiceClass.Unknown => "Unknown service type — cannot be reconfigured.",
        _ => string.Empty
    };

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
}

/// <summary>
/// UI-side wrapper for a trusted offline registry setting. Selection drives a
/// SetOfflineRegistryValue operation carrying the recommended data.
/// </summary>
public sealed class RegistrySettingItem : ViewModelBase
{
    public DiscoveredRegistrySetting Setting { get; }

    public RegistrySettingItem(DiscoveredRegistrySetting setting) => Setting = setting;

    public bool CanSelect => Setting.Risk is RiskClass.Safe or RiskClass.Removable;

    public string Reason => CanSelect
        ? string.Empty
        : "Setting is not permitted for offline modification.";

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
}
