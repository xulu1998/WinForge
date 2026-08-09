using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using WinForge.App.ViewModels;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// ADR-030 regression tests for the Step 3.3 service-inventory safety boundary.
/// The offline SYSTEM hive's <c>Services</c> tree contains far more than
/// user-facing Windows services (kernel / file-system drivers, performance and
/// provider entries, and many other low-level records). This suite pins the
/// separation of DISCOVERED from USER-CONFIGURABLE: only the trusted allowlist
/// (DiagTrack / WerSvc / PcaSvc) is configurable; everything else is protected
/// and cannot reach a plan operation or execution.
/// </summary>
public class ServiceInventorySafetyTests
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

    // ---- Selection-item gating (UI checkboxes) ----

    [Fact]
    public void ServiceSelectionItem_Driver_NotSelectable_WithReason()
    {
        var svc = new DiscoveredOfflineService
        {
            ServiceName = "MyFsDriver", ServiceKind = ServiceClass.Driver, Risk = RiskClass.Protected, ServiceType = 2
        };
        var item = new ServiceSelectionItem(svc, ServiceStartType.Disabled);
        Assert.False(item.CanSelect);
        Assert.Contains("driver", item.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServiceSelectionItem_Protected_NotSelectable_WithReason()
    {
        var svc = new DiscoveredOfflineService
        {
            ServiceName = ".NET CLR Data", ServiceKind = ServiceClass.Protected, Risk = RiskClass.Protected, ServiceType = 16
        };
        var item = new ServiceSelectionItem(svc, ServiceStartType.Disabled);
        Assert.False(item.CanSelect);
        Assert.False(string.IsNullOrEmpty(item.Reason));
    }

    [Fact]
    public void ServiceSelectionItem_Unknown_NotSelectable()
    {
        var svc = new DiscoveredOfflineService
        {
            ServiceName = "SomethingWeird", ServiceKind = ServiceClass.Unknown, Risk = RiskClass.Protected
        };
        var item = new ServiceSelectionItem(svc, ServiceStartType.Disabled);
        Assert.False(item.CanSelect);
    }

    [Fact]
    public void ServiceSelectionItem_RecommendedConfigurable_IsSelectable()
    {
        var svc = new DiscoveredOfflineService
        {
            ServiceName = "DiagTrack", ServiceKind = ServiceClass.RecommendedConfigurable,
            Risk = RiskClass.Removable, RecommendedStartType = ServiceStartType.Disabled
        };
        var item = new ServiceSelectionItem(svc, ServiceStartType.Disabled);
        Assert.True(item.CanSelect);
        Assert.Equal(string.Empty, item.Reason);
    }

    // ---- UI: Components page hides protected/system entries by default ----

    [Fact]
    public async Task Components_HidesProtectedServices_ByDefault_ShowsConfigurable()
    {
        var appState = AppStateWithMount();
        var inventory = new DiscoveryInventory
        {
            Discovered = true,
            Services = new[]
            {
                new DiscoveredOfflineService { ServiceName = "MyFsDriver", ServiceKind = ServiceClass.Driver, Risk = RiskClass.Protected, ServiceType = 2 },
                new DiscoveredOfflineService { ServiceName = ".NET CLR Data", ServiceKind = ServiceClass.Protected, Risk = RiskClass.Protected, ServiceType = 16 },
                new DiscoveredOfflineService { ServiceName = "DiagTrack", ServiceKind = ServiceClass.RecommendedConfigurable, Risk = RiskClass.Removable, RecommendedStartType = ServiceStartType.Disabled }
            }
        };
        var discovery = new FakeCustomizationDiscoveryService { Inventory = inventory };
        var vm = new ComponentsViewModel(appState, new InMemoryLoggerService(), discovery, new CustomizationDefinitionProvider());

        await vm.DiscoverAsync();

        // By default only the configurable service is shown (the other two are
        // hidden read-only diagnostics).
        Assert.Single(vm.Services);
        Assert.Equal("DiagTrack", vm.Services[0].Service.ServiceName);
        Assert.True(vm.Services[0].CanSelect);

        // Opting in reveals the protected/system entries — still NOT selectable.
        vm.ShowProtectedEntries = true;
        Assert.Equal(3, vm.Services.Count);
        var driver = vm.Services.First(s => s.Service.ServiceName == "MyFsDriver");
        var clr = vm.Services.First(s => s.Service.ServiceName == ".NET CLR Data");
        Assert.False(driver.CanSelect);
        Assert.False(clr.CanSelect);
        Assert.Contains("driver", driver.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- PlanSync refuses unapproved service identifiers ----

    [Fact]
    public void PlanSync_RefusesUnapprovedServiceIdentifier()
    {
        var appState = new AppState();
        PlanSync.EnsureDraftPlan(appState);

        // Riska: caller sets Risk = Removable to try to bypass classification.
        PlanSync.Toggle(appState, "svc|Dnscache", true, () => new CustomizationOperation
        {
            OperationId = "svc|Dnscache",
            OperationType = CustomizationOperationType.ConfigureOfflineService,
            ServiceName = "Dnscache",
            ServiceStartType = ServiceStartType.Disabled,
            Risk = RiskClass.Removable,
            IsSelected = true
        });

        Assert.Empty(appState.CurrentCustomizationPlan!.Operations);
    }

    [Fact]
    public void PlanSync_AllowsApprovedServiceIdentifier()
    {
        var appState = new AppState();
        PlanSync.EnsureDraftPlan(appState);

        PlanSync.Toggle(appState, "svc|DiagTrack", true, () => new CustomizationOperation
        {
            OperationId = "svc|DiagTrack",
            OperationType = CustomizationOperationType.ConfigureOfflineService,
            ServiceName = "DiagTrack",
            ServiceStartType = ServiceStartType.Disabled,
            Risk = RiskClass.Removable,
            IsSelected = true
        });

        Assert.Single(appState.CurrentCustomizationPlan!.Operations);
    }

    // ---- Plan validation rejects manually injected unapproved service op ----

    [Fact]
    public void PlanValidation_RejectsInjectedUnapprovedService()
    {
        var plan = new CustomizationPlan();
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "svc|BadDriver",
            OperationType = CustomizationOperationType.ConfigureOfflineService,
            ServiceName = "SomeKernelDriver",
            ServiceStartType = ServiceStartType.Disabled,
            Risk = RiskClass.Removable, // attempted bypass
            IsSelected = true
        });

        var issues = plan.Validate();

        Assert.NotEmpty(issues);
        Assert.NotEqual(CustomizationPlanStatus.Validated, plan.Status);
        Assert.False(plan.IsValid);
    }

    [Fact]
    public void PlanValidation_AllowsApprovedService()
    {
        var plan = new CustomizationPlan();
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "svc|DiagTrack",
            OperationType = CustomizationOperationType.ConfigureOfflineService,
            ServiceName = "DiagTrack",
            ServiceStartType = ServiceStartType.Disabled,
            Risk = RiskClass.Removable,
            IsSelected = true
        });

        var issues = plan.Validate();

        Assert.Empty(issues);
        Assert.Equal(CustomizationPlanStatus.Validated, plan.Status);
    }

    // ---- Execution retains an independent final safety guard ----

    [Fact]
    public async Task Execution_SkipsNonAllowlistedService_EvenIfForcedValidated()
    {
        // The plan-validation gate already rejects unapproved service ops; this
        // is the defense-in-depth backstop. We force Validated (simulating a
        // defeated/bypassed validation) to prove execution still refuses.
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wf_svc_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var mount = System.IO.Path.Combine(root, "mount");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(mount, "Windows", "System32", "config"));
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(mount, "Windows", "System32", "config", "SYSTEM"), new byte[8]);

            var workspace = new ImageServicingWorkspace
            {
                WorkingDirectory = root,
                MountDirectory = mount,
                WorkingImagePath = System.IO.Path.Combine(root, "image", "install.wim"),
                State = ServicingWorkspaceState.Mounted
            };

            var runner = new FakeProcessRunner
            {
                Responder = req => req.Arguments.Contains("/Get-MountedImageInfo")
                    ? new ProcessResult { ExitCode = 0, StandardOutput = $"Mount Dir : {mount}\n" }
                    : new ProcessResult { ExitCode = 0, StandardOutput = string.Empty }
            };
            var registry = new FakeOfflineRegistryService();
            registry.Values["WinForge_SYSTEM|Select|Current"] = "1";
            var validator = new FakeMountIdentityValidator { SessionMatches = true, WithinMount = true };
            var service = new WindowsCustomizationExecutionService(runner, registry, new InMemoryLoggerService(), validator);

            var plan = new CustomizationPlan();
            plan.AddOperation(new CustomizationOperation
            {
                OperationId = "svc|Dnscache",
                OperationType = CustomizationOperationType.ConfigureOfflineService,
                ServiceName = "Dnscache",
                ServiceStartType = ServiceStartType.Disabled,
                Risk = RiskClass.Removable,
                IsSelected = true
            });
            // Force Validated to simulate a bypassed/defeated validation gate —
            // this exercises the final defense-in-depth guard in ApplyService.
            var statusProp = typeof(CustomizationPlan).GetProperty("Status", BindingFlags.Public | BindingFlags.Instance)!;
            statusProp.GetSetMethod(nonPublic: true)!.Invoke(plan, new object[] { CustomizationPlanStatus.Validated });

            var result = await service.ExecuteAsync(plan, workspace, null, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(0, result.FailedOperations);
            var op = result.Operations.Single();
            Assert.Equal(CustomizationOperationStatus.Skipped, op.ExecutionStatus);
            Assert.DoesNotContain(registry.LoadedHives, h => h.Contains("SYSTEM"));
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    // ---- Host SYSTEM hive remains impossible to target ----

    [Fact]
    public async Task Execution_RefusesServiceWhenHiveOutsideMount()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wf_host_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var mount = System.IO.Path.Combine(root, "mount");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(mount, "Windows", "System32", "config"));
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(mount, "Windows", "System32", "config", "SYSTEM"), new byte[8]);

            var workspace = new ImageServicingWorkspace
            {
                WorkingDirectory = root,
                MountDirectory = mount,
                WorkingImagePath = System.IO.Path.Combine(root, "image", "install.wim"),
                State = ServicingWorkspaceState.Mounted
            };

            var runner = new FakeProcessRunner
            {
                Responder = req => req.Arguments.Contains("/Get-MountedImageInfo")
                    ? new ProcessResult { ExitCode = 0, StandardOutput = $"Mount Dir : {mount}\n" }
                    : new ProcessResult { ExitCode = 0, StandardOutput = string.Empty }
            };
            var registry = new FakeOfflineRegistryService();
            registry.Values["WinForge_SYSTEM|Select|Current"] = "1";
            // Validator reports the hive as being OUTSIDE the mounted workspace.
            var validator = new FakeMountIdentityValidator { SessionMatches = true, WithinMount = false };
            var service = new WindowsCustomizationExecutionService(runner, registry, new InMemoryLoggerService(), validator);

            var plan = new CustomizationPlan();
            plan.AddOperation(new CustomizationOperation
            {
                OperationId = "svc|DiagTrack",
                OperationType = CustomizationOperationType.ConfigureOfflineService,
                ServiceName = "DiagTrack",
                ServiceStartType = ServiceStartType.Disabled,
                Risk = RiskClass.Removable,
                IsSelected = true
            });
            plan.Validate();
            Assert.Equal(CustomizationPlanStatus.Validated, plan.Status);

            var result = await service.ExecuteAsync(plan, workspace, null, CancellationToken.None);

            // A service whose hive resolves outside the mount is never applied.
            Assert.False(result.Success);
            var op = result.Operations.Single();
            Assert.Equal(CustomizationOperationStatus.FailedRecoverable, op.ExecutionStatus);
            Assert.DoesNotContain(registry.LoadedHives, h => h.Contains("SYSTEM"));
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    // ---- Single source of truth: policy matches the trusted definition provider ----

    [Fact]
    public void ServiceConfigPolicy_MatchesTrustedDefinitionProvider()
    {
        var provider = new CustomizationDefinitionProvider();
        var trusted = provider.GetRecommendedServiceChanges()
            .Select(s => s.ServiceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(trusted.Count, ServiceConfigPolicy.AllowedServiceMarkers.Count);
        foreach (var marker in ServiceConfigPolicy.AllowedServiceMarkers)
        {
            Assert.Contains(trusted, t => string.Equals(t, marker, StringComparison.OrdinalIgnoreCase));
        }
    }
}
