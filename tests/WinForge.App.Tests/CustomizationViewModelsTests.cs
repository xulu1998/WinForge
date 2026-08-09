using System.Linq;
using System.Threading.Tasks;
using WinForge.App.ViewModels;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// View-model wiring for Step 3.3: discovery-backed Components page, trusted
/// Privacy / System pages, and the Plan review / validate / apply flow. Verifies
/// that selections produce the correct declarative plan operations and that the
/// shared plan is kept in sync, using fakes for discovery / execution.
/// </summary>
public class CustomizationViewModelsTests
{
    private static AppState AppStateWithMount() => new AppState
    {
        CurrentServicingWorkspace = new ImageServicingWorkspace
        {
            WorkingDirectory = @"C:\wf\ws",
            MountDirectory = @"C:\wf\ws\mount",
            WorkingImagePath = @"C:\wf\ws\image\install.wim",
            State = ServicingWorkspaceState.Mounted
        }
    };

    // ---- Components ----

    [Fact]
    public async Task Components_Discover_PopulatesCollections()
    {
        var appState = AppStateWithMount();
        var discovery = new FakeCustomizationDiscoveryService
        {
            Inventory = new DiscoveryInventory
            {
                Discovered = true,
                AppxPackages = new[]
                {
                    new DiscoveredAppxPackage { PackageName = "A", DisplayName = "AppA", Risk = RiskClass.Removable }
                },
                WindowsPackages = new[]
                {
                    new DiscoveredWindowsPackage { PackageIdentity = "P", DisplayName = "Pkg", Classification = PackageClassification.Feature, Risk = RiskClass.Removable }
                },
                Services = new[]
                {
                    new DiscoveredOfflineService { ServiceName = "DiagTrack", CurrentStartValue = 2, Risk = RiskClass.Removable }
                }
            }
        };
        var vm = new ComponentsViewModel(appState, new InMemoryLoggerService(), discovery, new CustomizationDefinitionProvider());

        await vm.DiscoverAsync();

        Assert.True(vm.HasInventory);
        Assert.Single(vm.AppxPackages);
        Assert.Single(vm.WindowsPackages);
        Assert.Single(vm.Services);
    }

    [Fact]
    public async Task Components_SelectingAppx_AddsPlanOperation()
    {
        var appState = AppStateWithMount();
        var discovery = new FakeCustomizationDiscoveryService
        {
            Inventory = new DiscoveryInventory
            {
                Discovered = true,
                AppxPackages = new[] { new DiscoveredAppxPackage { PackageName = "A", DisplayName = "AppA", Risk = RiskClass.Removable } }
            }
        };
        var vm = new ComponentsViewModel(appState, new InMemoryLoggerService(), discovery, new CustomizationDefinitionProvider());
        await vm.DiscoverAsync();

        vm.AppxPackages[0].IsSelected = true;

        var plan = appState.CurrentCustomizationPlan!;
        var op = plan.Operations.Single(o => o.OperationId == "appx|A");
        Assert.True(op.IsSelected);
        Assert.Equal(CustomizationOperationType.RemoveProvisionedAppx, op.OperationType);
        Assert.Equal("A", op.TargetIdentifier);
    }

    [Fact]
    public async Task Components_Deselecting_RemovesPlanOperation()
    {
        var appState = AppStateWithMount();
        var discovery = new FakeCustomizationDiscoveryService
        {
            Inventory = new DiscoveryInventory
            {
                Discovered = true,
                AppxPackages = new[] { new DiscoveredAppxPackage { PackageName = "A", DisplayName = "AppA", Risk = RiskClass.Removable } }
            }
        };
        var vm = new ComponentsViewModel(appState, new InMemoryLoggerService(), discovery, new CustomizationDefinitionProvider());
        await vm.DiscoverAsync();
        vm.AppxPackages[0].IsSelected = true;
        vm.AppxPackages[0].IsSelected = false;

        Assert.Empty(appState.CurrentCustomizationPlan!.Operations);
    }

    [Fact]
    public async Task Components_ProtectedAppx_NotSelectable()
    {
        var appState = AppStateWithMount();
        var discovery = new FakeCustomizationDiscoveryService
        {
            Inventory = new DiscoveryInventory
            {
                Discovered = true,
                AppxPackages = new[] { new DiscoveredAppxPackage { PackageName = "A", DisplayName = "AppA", Risk = RiskClass.Protected } }
            }
        };
        var vm = new ComponentsViewModel(appState, new InMemoryLoggerService(), discovery, new CustomizationDefinitionProvider());
        await vm.DiscoverAsync();
        Assert.False(vm.AppxPackages[0].CanSelect);
    }

    // ---- Privacy ----

    [Fact]
    public void Privacy_LoadsDefinitions()
    {
        var provider = new FakeCustomizationDefinitionProvider();
        provider.Privacy.Add(new DiscoveredRegistrySetting
        {
            SettingId = "p1", Category = CustomizationCategory.Privacy, Title = "P1",
            Hive = "SOFTWARE", KeyPath = "K", ValueName = "V", ValueKind = OfflineRegistryValueKind.DWord,
            RecommendedData = "0", Risk = RiskClass.Safe
        });
        var vm = new PrivacyViewModel(new AppState(), new InMemoryLoggerService(), provider);
        Assert.Single(vm.Settings);
    }

    [Fact]
    public void Privacy_SelectingSetting_AddsRegistryOperation()
    {
        var appState = new AppState();
        var provider = new FakeCustomizationDefinitionProvider();
        provider.Privacy.Add(new DiscoveredRegistrySetting
        {
            SettingId = "p1", Category = CustomizationCategory.Privacy, Title = "P1",
            Hive = "SOFTWARE", KeyPath = "K", ValueName = "V", ValueKind = OfflineRegistryValueKind.DWord,
            RecommendedData = "0", Risk = RiskClass.Safe
        });
        var vm = new PrivacyViewModel(appState, new InMemoryLoggerService(), provider);
        vm.Settings[0].IsSelected = true;

        var op = appState.CurrentCustomizationPlan!.Operations.Single(o => o.OperationId == "reg|p1");
        Assert.Equal(CustomizationOperationType.SetOfflineRegistryValue, op.OperationType);
        Assert.Equal("SOFTWARE", op.RegistryHive);
        Assert.Equal("0", op.RegistryValueData);
    }

    // ---- System ----

    [Fact]
    public void System_LoadsRecommendedServices()
    {
        var provider = new FakeCustomizationDefinitionProvider();
        provider.Services.Add(new DiscoveredOfflineService
        {
            ServiceName = "DiagTrack", DisplayName = "Telemetry", CurrentStartValue = 2,
            RecommendedStartType = ServiceStartType.Disabled, Risk = RiskClass.Removable
        });
        var vm = new SystemViewModel(new AppState(), new InMemoryLoggerService(), provider);
        Assert.Single(vm.RecommendedServices);
        Assert.Equal(ServiceStartType.Disabled, vm.RecommendedServices[0].RecommendedStartType);
    }

    [Fact]
    public void System_SelectingService_AddsServiceOperation()
    {
        var appState = new AppState();
        var provider = new FakeCustomizationDefinitionProvider();
        provider.Services.Add(new DiscoveredOfflineService
        {
            ServiceName = "DiagTrack", DisplayName = "Telemetry", CurrentStartValue = 2,
            RecommendedStartType = ServiceStartType.Disabled, Risk = RiskClass.Removable
        });
        var vm = new SystemViewModel(appState, new InMemoryLoggerService(), provider);
        vm.RecommendedServices[0].IsSelected = true;

        var op = appState.CurrentCustomizationPlan!.Operations.Single(o => o.OperationId == "svc|DiagTrack");
        Assert.Equal(CustomizationOperationType.ConfigureOfflineService, op.OperationType);
        Assert.Equal(ServiceStartType.Disabled, op.ServiceStartType);
    }

    // ---- Plan review ----

    [Fact]
    public void PlanReview_Validate_SetsValidated()
    {
        var appState = AppStateWithMount();
        var plan = new CustomizationPlan();
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "a", OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            TargetIdentifier = "x", Risk = RiskClass.Removable, IsSelected = true
        });
        appState.CurrentCustomizationPlan = plan;

        var vm = new PlanReviewViewModel(appState, new InMemoryLoggerService(), new FakeCustomizationExecutionService());
        vm.ValidatePlan();

        Assert.Equal(CustomizationPlanStatus.Validated, plan.Status);
        Assert.True(vm.CanApply);
    }

    [Fact]
    public async Task PlanReview_Apply_InvokesExecution_AndCompletes()
    {
        var appState = AppStateWithMount();
        var plan = new CustomizationPlan();
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "a", OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            TargetIdentifier = "x", Risk = RiskClass.Removable, IsSelected = true
        });
        plan.Validate();
        Assert.Equal(CustomizationPlanStatus.Validated, plan.Status);
        appState.CurrentCustomizationPlan = plan;

        var execution = new FakeCustomizationExecutionService
        {
            Result = new CustomizationResult { TotalOperations = 1, Succeeded = 1 }
        };
        var vm = new PlanReviewViewModel(appState, new InMemoryLoggerService(), execution);

        await vm.ApplyAsync();

        Assert.Equal(1, execution.ExecuteCalls);
        Assert.Same(plan, execution.LastPlan);
        Assert.Equal(CustomizationExecutionState.Completed, appState.CustomizationExecutionState);
    }

    [Fact]
    public void PlanReview_ApplyDisabled_WhenNotValidated()
    {
        var appState = AppStateWithMount();
        var plan = new CustomizationPlan();
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "a", OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            TargetIdentifier = "x", Risk = RiskClass.Removable, IsSelected = true
        });
        // Not validated.
        appState.CurrentCustomizationPlan = plan;

        var vm = new PlanReviewViewModel(appState, new InMemoryLoggerService(), new FakeCustomizationExecutionService());
        Assert.False(vm.CanApply);
    }
}
