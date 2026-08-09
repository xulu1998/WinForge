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
/// Regression tests for the three real-desktop validation defects found in
/// Step 3.3 (DEFECT 1: provisioned-Appx discovery returns zero; DEFECT 2:
/// offline service discovery returns zero; DEFECT 3: unsafe package selection /
/// classification mismatch). These pin the safety policy across every layer.
/// </summary>
public class Step33DefectTests
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

    // ---- DEFECT 3: UI cannot select a non-allowlisted package ----

    [Fact]
    public void NonAllowlistedPackage_SelectionItem_IsNotSelectable()
    {
        var pkg = new DiscoveredWindowsPackage
        {
            PackageIdentity = "Microsoft-OneCore-ApplicationModel-Sync-Desktop-Package~31bf3856ad364e35~amd64~~10.0.26100.1",
            DisplayName = "OneCore Sync",
            Classification = PackageClassification.Feature,
            Risk = RiskClass.Protected
        };
        var item = new PackageSelectionItem(pkg);
        Assert.False(item.CanSelect);
        Assert.False(string.IsNullOrEmpty(item.Reason));
    }

    [Fact]
    public async Task Components_SelectingNonAllowlistedPackage_DoesNotAddPlanOperation()
    {
        // Full pipeline: a non-allowlisted package discovered from the image must
        // be rendered non-selectable AND, even if IsSelected is forced true, must
        // not produce a plan operation (PlanSync gate).
        var oneCore = "Microsoft-OneCore-ApplicationModel-Sync-Desktop-Package~31bf3856ad364e35~amd64~~10.0.26100.1";
        var inventory = new DiscoveryInventory
        {
            Discovered = true,
            WindowsPackages = new[]
            {
                new DiscoveredWindowsPackage
                {
                    PackageIdentity = oneCore,
                    DisplayName = "OneCore Sync",
                    Classification = PackageClassification.Feature,
                    Risk = RiskClass.Protected
                }
            }
        };
        var appState = AppStateWithMount();
        var discovery = new FakeCustomizationDiscoveryService { Inventory = inventory };
        var vm = new ComponentsViewModel(appState, new InMemoryLoggerService(), discovery, new CustomizationDefinitionProvider());

        await vm.DiscoverAsync();

        Assert.Single(vm.WindowsPackages);
        Assert.False(vm.WindowsPackages[0].CanSelect);

        // Force the selection anyway (simulating a tampered/bypassed binding).
        vm.WindowsPackages[0].IsSelected = true;

        Assert.Empty(appState.CurrentCustomizationPlan!.Operations);
    }

    // ---- DEFECT 3: PlanSync refuses unsupported package even if called directly ----

    [Fact]
    public void PlanSync_RefusesProtectedPackageOperation()
    {
        var appState = new AppState();
        PlanSync.EnsureDraftPlan(appState);

        PlanSync.Toggle(appState, "pkg|onecore", true, () => new CustomizationOperation
        {
            OperationId = "pkg|onecore",
            OperationType = CustomizationOperationType.RemovePackage,
            TargetIdentifier = "Microsoft-OneCore-ApplicationModel-Sync-Desktop-Package~x",
            Risk = RiskClass.Protected,
            IsSelected = true
        });

        Assert.Empty(appState.CurrentCustomizationPlan!.Operations);
    }

    [Fact]
    public void PlanSync_AllowsRemovableAllowlistedPackage()
    {
        var appState = new AppState();
        PlanSync.EnsureDraftPlan(appState);

        PlanSync.Toggle(appState, "pkg|ie", true, () => new CustomizationOperation
        {
            OperationId = "pkg|ie",
            OperationType = CustomizationOperationType.RemovePackage,
            TargetIdentifier = "Microsoft-Windows-InternetExplorer-Optional-Package~x",
            Risk = RiskClass.Removable,
            IsSelected = true
        });

        Assert.Single(appState.CurrentCustomizationPlan!.Operations);
    }

    // ---- DEFECT 3: plan validation rejects manually injected unsupported op ----

    [Fact]
    public void PlanValidation_RejectsManuallyInjectedUnsupportedPackage()
    {
        var plan = new CustomizationPlan();
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "pkg|onecore",
            OperationType = CustomizationOperationType.RemovePackage,
            TargetIdentifier = "Microsoft-OneCore-ApplicationModel-Sync-Desktop-Package~x",
            Risk = RiskClass.Protected,
            IsSelected = true
        });

        var issues = plan.Validate();

        Assert.NotEmpty(issues);
        Assert.NotEqual(CustomizationPlanStatus.Validated, plan.Status);
        Assert.False(plan.IsValid);
    }

    // ---- DEFECT 3: execution retains the allowlist guard as defense in depth ----

    [Fact]
    public async Task Execution_SkipsNonAllowlistedPackage_EvenIfPlanValidated()
    {
        // An operation that is Removable (so it passes plan validation) but whose
        // target is NOT on the allowlist must still be skipped at execution and
        // must NOT reach DISM's /Remove-Package.
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wf_def3_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var mount = System.IO.Path.Combine(root, "mount");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(mount, "Windows", "System32", "config"));
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(mount, "Windows", "System32", "config", "SOFTWARE"), new byte[8]);
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
                OperationId = "pkg|onecore",
                OperationType = CustomizationOperationType.RemovePackage,
                TargetIdentifier = "Microsoft-OneCore-ApplicationModel-Sync-Desktop-Package~x",
                Risk = RiskClass.Removable, // passes plan validation
                IsSelected = true
            });
            plan.Validate();
            Assert.Equal(CustomizationPlanStatus.Validated, plan.Status);

            var result = await service.ExecuteAsync(plan, workspace, null, System.Threading.CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal(0, result.FailedOperations);
            Assert.DoesNotContain(runner.Requests, r => r.Arguments.Contains("/Remove-Package"));
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }
}
