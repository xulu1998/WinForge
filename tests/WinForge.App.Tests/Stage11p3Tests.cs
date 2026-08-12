using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinForge.App.Mvvm;
using WinForge.App.ViewModels;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.ComponentIntelligence;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

// ---------------------------------------------------------------------------
// Stage 11.3 — Customize coverage expansion + Personalization activation.
// Part T test areas 1-33 (regression areas 34-40 stay covered by the existing
// Stage 11.2 / Stage 11.1 / Phase 3 / Phase 10 suites).
// ---------------------------------------------------------------------------

public class Stage11p3CatalogTests
{
    private static OptimizationCatalog Catalog() => new();

    [Fact]
    public void Every_Standard_Visible_Item_Has_Name_Purpose_Recommendation_Risk_Provenance()
    {
        foreach (var e in Catalog().GetEntries().Where(x => x.IsStandardVisible))
        {
            Assert.False(string.IsNullOrWhiteSpace(e.DisplayNameKey), $"{e.Id}: missing DisplayNameKey");
            Assert.False(string.IsNullOrWhiteSpace(e.ShortDescriptionKey), $"{e.Id}: missing ShortDescriptionKey");
            Assert.NotEqual(RecommendationLevel.Unknown, e.Recommendation);
            Assert.NotEqual(RiskLevel.Unknown, e.Risk);
            Assert.NotEqual(OptimizationAction.Unknown, e.Action);
            Assert.NotEqual(OptimizationMechanism.Unknown, e.Mechanism);
            Assert.NotEqual(OptimizationScope.Unknown, e.Scope);
            Assert.NotEmpty(e.Provenance);
            Assert.NotEqual(RemovalSupport.Unknown, e.Removal);
        }
    }

    [Fact]
    public void No_Unknown_Or_Experimental_Item_Leaks_Into_Standard_Mode()
    {
        foreach (var e in Catalog().GetEntries().Where(x => x.IsStandardVisible))
        {
            Assert.NotEqual(RecommendationLevel.Unknown, e.Recommendation);
            Assert.NotEqual(RiskLevel.Unknown, e.Risk);
            Assert.NotEqual(RemovalSupport.Experimental, e.Removal);
        }
    }

    [Fact]
    public void Community_Only_Evidence_Never_Promotes_To_Recommended()
    {
        // Part P: a community project's opinion must NEVER auto-promote to
        // RecommendedRemove / RecommendedDisable.
        foreach (var e in Catalog().GetEntries().Where(x => x.IsStandardVisible))
        {
            var sourceTypes = e.Provenance.SelectMany(p => p.Sources).Select(s => s.SourceType).Distinct().ToList();
            var communityOnly = sourceTypes.Count > 0 && sourceTypes.All(t => t == KnowledgeSourceType.CommunityProject);
            if (communityOnly)
            {
                Assert.NotEqual(RecommendationLevel.RecommendedRemove, e.Recommendation);
            }
        }
    }

    [Fact]
    public void Selectable_Services_Are_Allowlisted()
    {
        foreach (var e in Catalog().GetEntries().Where(x => x.Tab == OptimizationTab.Services && x.ProposedStartType is not null))
        {
            Assert.True(ServiceConfigPolicy.IsConfigurable(e.ServiceName),
                $"Service '{e.ServiceName}' is offered for change but is not on the allowlist.");
        }
    }

    [Fact]
    public void Core_Service_Is_Never_Offered_For_Change()
    {
        var rpc = Catalog().GetEntries().Single(e => e.Id == "RpcSs");
        Assert.Null(rpc.ProposedStartType);          // LeaveDefault
        Assert.Equal(RecommendationLevel.NeverRemove, rpc.Recommendation);
        Assert.Equal(RemovalSupport.Blocked, rpc.Removal);
    }

    [Fact]
    public void Features_Catalog_Is_Pinned_To_FeatureConfigPolicy()
    {
        var featureCatalog = new WindowsFeaturesCatalog();
        foreach (var def in featureCatalog.GetDefinitions())
        {
            Assert.Equal(OptimizationAction.Feature, def.Action);
            Assert.Equal(OptimizationMechanism.DisableOptionalFeature, def.Mechanism);
            Assert.Equal(OptimizationScope.MountedImageFeature, def.Scope);
            foreach (var target in def.TechnicalTargets)
            {
                Assert.Equal(ComponentCategory.OptionalFeature, target.Category);
                Assert.Equal(MatchMethod.Exact, target.Match);
                Assert.True(FeatureConfigPolicy.IsFeatureAllowed(target.Pattern),
                    $"Feature '{target.Pattern}' is in the catalog but not on FeatureConfigPolicy.");
            }
        }
    }

    [Fact]
    public void Registry_Targets_Never_Touch_The_Host_Registry()
    {
        // Part K / test 7+9: every registry target is an OFFLINE hive
        // (SOFTWARE / SYSTEM / DEFAULT_USER) — never HKCU of the host system.
        foreach (var e in Catalog().GetEntries())
        {
            foreach (var t in e.RegistryTargets)
            {
                Assert.Contains(t.Hive, new[] { "SOFTWARE", "SYSTEM", "DEFAULT_USER" });
                Assert.DoesNotContain("HKCU", t.KeyPath, StringComparison.OrdinalIgnoreCase);
                Assert.False(t.KeyPath.StartsWith("HKEY", StringComparison.OrdinalIgnoreCase));
            }
        }
    }

    [Fact]
    public void Default_User_Hive_Resolves_To_Offline_Profile_File()
    {
        // Test 21 strategy: user-scope settings target the OFFLINE Default User
        // profile (Users\Default\NTUSER.DAT), not the host user's registry.
        var ws = new ImageServicingWorkspace { MountDirectory = @"C:\wf\mount" };
        var path = OfflineHivePaths.GetHiveFilePath(ws, "DEFAULT_USER");
        Assert.Equal(@"C:\wf\mount\Users\Default\NTUSER.DAT", path);
        Assert.Equal("WinForge_DEFAULT_USER", OfflineHivePaths.GetWinForgeHiveName("DEFAULT_USER"));
    }
}

public class Stage11p3KnowledgeTabTests
{
    private static OptimizationKnowledgeViewModel MakeTab(AppState state, OptimizationTab tab)
        => ComponentKnowledgeTestFactory.MakeOptimization(state, new InMemoryLoggerService(), tab);

    [Fact]
    public void Personalization_Tab_Is_No_Longer_Coming_Soon()
    {
        var customize = ComponentKnowledgeTestFactory.MakeCustomize(new AppState(), new InMemoryLoggerService());
        Assert.Equal("Customize.Tab.Personalization", customize.Tabs[5].HeaderKey);
        var content = Assert.IsType<OptimizationKnowledgeViewModel>(customize.Tabs[5].Content);
        Assert.Equal(OptimizationTab.Personalization, content.Tab);
        Assert.DoesNotContain(customize.Tabs, t => t.Content is ComingSoonViewModel);
    }

    [Fact]
    public void All_Four_Catalog_Tabs_Populate_Real_Controls()
    {
        var state = new AppState();
        foreach (var tab in new[] { OptimizationTab.Services, OptimizationTab.Privacy, OptimizationTab.System, OptimizationTab.Personalization })
        {
            var vm = MakeTab(state, tab);
            Assert.True(vm.ItemCount >= 10, $"{tab}: only {vm.ItemCount} reviewed controls");
            Assert.False(vm.IsEmpty, $"{tab}: tab is empty");
        }
    }

    [Fact]
    public void Personalization_Covers_All_Required_Groups()
    {
        // Part R: Start/Search, Taskbar, Explorer, Lock screen/Desktop, Appearance.
        var vm = MakeTab(new AppState(), OptimizationTab.Personalization);
        var mechanisms = vm.Items.Select(i => i.Definition.Mechanism).ToHashSet();
        Assert.Contains(OptimizationMechanism.StartPreference, mechanisms);
        Assert.Contains(OptimizationMechanism.TaskbarPreference, mechanisms);
        Assert.Contains(OptimizationMechanism.ExplorerPreference, mechanisms);
        Assert.Contains(OptimizationMechanism.VisualPreference, mechanisms);
        Assert.Contains(OptimizationMechanism.PrivacyPolicy, mechanisms); // Spotlight lock screen
    }

    [Fact]
    public void Show_File_Extensions_Uses_Offline_Default_User_Strategy()
    {
        var state = new AppState();
        var vm = MakeTab(state, OptimizationTab.Personalization);
        var item = vm.Items.Single(i => i.Definition.Id == "ShowFileExtensions");
        Assert.True(item.IsSelectable);
        item.IsSelected = true;

        var op = state.CurrentCustomizationPlan!.Operations.Single(o => o.OperationId == "opt|ShowFileExtensions|0");
        Assert.Equal(CustomizationOperationType.SetOfflineRegistryValue, op.OperationType);
        Assert.Equal("DEFAULT_USER", op.RegistryHive);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", op.RegistryKeyPath);
        Assert.Equal("HideFileExt", op.RegistryValueName);
        Assert.Equal("0", op.RegistryValueData);
        Assert.Equal("1", op.RestoreValueData);          // reversibility (Part O)
        Assert.Equal(OptimizationScope.OfflineDefaultUser, op.Scope);
        Assert.Equal(OptimizationAction.Configure, op.ActionKind);
        Assert.Equal(OptimizationMechanism.ExplorerPreference, op.Mechanism);
    }

    [Fact]
    public void Dark_Mode_Produces_One_Op_Per_Registry_Target()
    {
        var state = new AppState();
        var vm = MakeTab(state, OptimizationTab.Personalization);
        var item = vm.Items.Single(i => i.Definition.Id == "DarkMode");
        item.IsSelected = true;

        var ops = state.CurrentCustomizationPlan!.Operations
            .Where(o => o.OperationId.StartsWith("opt|DarkMode|")).ToList();
        Assert.Equal(2, ops.Count);
        Assert.Contains(ops, o => o.RegistryValueName == "AppsUseLightTheme");
        Assert.Contains(ops, o => o.RegistryValueName == "SystemUsesLightTheme");
    }

    [Fact]
    public void Registry_Policy_Op_Carries_Action_Scope_And_Revert()
    {
        var state = new AppState();
        var vm = MakeTab(state, OptimizationTab.Privacy);
        var item = vm.Items.Single(i => i.Definition.Id == "AdvertisingId");
        item.IsSelected = true;

        var op = state.CurrentCustomizationPlan!.Operations.Single(o => o.OperationId == "opt|AdvertisingId|0");
        Assert.Equal(CustomizationCategory.Privacy, op.Category);
        Assert.Equal(OptimizationAction.Disable, op.ActionKind);
        Assert.Equal(OptimizationMechanism.PrivacyPolicy, op.Mechanism);
        Assert.Equal(OptimizationScope.OfflineMachine, op.Scope);
        Assert.Equal("1", op.RestoreValueData);
    }

    [Fact]
    public void Service_Op_Targets_Offline_Machine_And_Uses_Service_ConflictKey()
    {
        var state = new AppState();
        var vm = MakeTab(state, OptimizationTab.Services);
        var item = vm.Items.Single(i => i.Definition.Id == "DiagTrack");
        Assert.True(item.IsSelectable);
        item.IsSelected = true;

        var op = state.CurrentCustomizationPlan!.Operations.Single(o => o.OperationId == "svc|DiagTrack");
        Assert.Equal(CustomizationOperationType.ConfigureOfflineService, op.OperationType);
        Assert.Equal("DiagTrack", op.ServiceName);
        Assert.Equal(ServiceStartType.Disabled, op.ServiceStartType);
        Assert.Equal(OptimizationScope.OfflineMachine, op.Scope);
        Assert.Equal("svc|DiagTrack", op.ConflictKey);
    }

    [Fact]
    public void Incompatible_Build_Is_Gated_And_Compatible_Build_Is_Selectable()
    {
        var state = new AppState();
        var vm = MakeTab(state, OptimizationTab.System);
        var item = vm.Items.Single(i => i.Definition.Id == "WindowsAi");

        state.CurrentImageWorkspace = new ImageWorkspace { Build = "22631" };
        Assert.False(item.IsApplicable);
        Assert.False(item.IsSelectable);
        Assert.False(string.IsNullOrWhiteSpace(item.BlockReason));

        state.CurrentImageWorkspace = new ImageWorkspace { Build = "26200" };
        Assert.True(item.IsApplicable);
        Assert.True(item.IsSelectable);
    }

    [Fact]
    public void Post_Install_Only_Scope_Is_Not_Selectable()
    {
        var state = new AppState();
        var vm = MakeTab(state, OptimizationTab.Personalization);
        // Inject a synthetic post-install-only entry through a tiny provider.
        var provider = new SyntheticCatalog(new OptimizationDefinition
        {
            Id = "SyntheticPostInstall",
            Tab = OptimizationTab.Personalization,
            Action = OptimizationAction.Configure,
            Mechanism = OptimizationMechanism.ExplorerPreference,
            Scope = OptimizationScope.PostInstallRequired,
            DisplayNameKey = "Opt.Personalization.Synthetic.DisplayName",
            ShortDescriptionKey = "Opt.Personalization.Synthetic.Short",
            Recommendation = RecommendationLevel.OptionalRemove,
            Risk = RiskLevel.Low,
            Removal = RemovalSupport.Supported,
            Restore = RestoreSupport.Easy,
            Provenance = new[] { new KnowledgeClaim(KnowledgeClaimKind.Fact, "k", new[] { new KnowledgeSource(KnowledgeSourceType.MicrosoftOfficial, "M", ConfidenceLevel.Verified) }) },
            RegistryTargets = new[] { new RegistryTarget { Hive = "DEFAULT_USER", KeyPath = "Software\\K", ValueName = "V", RecommendedData = "0", RestoreData = "1" } },
        });
        var syntheticVm = new OptimizationKnowledgeViewModel(state, new InMemoryLoggerService(),
            new FakeLocalizationService(), provider, OptimizationTab.Personalization);
        var item = syntheticVm.Items.Single();
        Assert.False(item.IsSelectable);
        Assert.False(string.IsNullOrWhiteSpace(item.BlockReason));
    }

    [Fact]
    public void Core_Service_Row_Is_Blocked_And_Informational()
    {
        var vm = MakeTab(new AppState(), OptimizationTab.Services);
        var rpc = vm.Items.Single(i => i.Definition.Id == "RpcSs");
        Assert.False(rpc.IsSelectable);
        Assert.False(string.IsNullOrWhiteSpace(rpc.BlockReason));
        Assert.True(string.IsNullOrWhiteSpace(rpc.ProposedStartCaption));
    }

    [Fact]
    public void Filtering_Closes_Detail_Without_Touching_Selection()
    {
        var state = new AppState();
        var vm = MakeTab(state, OptimizationTab.Privacy);
        var item = vm.Items.Single(i => i.Definition.Id == "AdvertisingId");
        item.IsSelected = true;                       // removal selection
        vm.ActiveDetail = item;                       // inspection

        vm.Filter = ComponentKnowledgeFilter.UsuallyKeep; // hides RecommendedRemove rows
        Assert.Null(vm.ActiveDetail);                 // detail closed (filtered out)
        Assert.True(item.IsSelected);                 // selection survives (Part M/ADR-050)
    }
}

public class Stage11p3ComponentsKnowledgeTests
{
    private sealed class StaticCiService : IComponentIntelligenceService
    {
        public ComponentInventory Inventory { get; set; } = new();
        public Task<ComponentInventory> DiscoverAsync(
            ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
            => Task.FromResult(Inventory);
    }

    private static (AppState State, ComponentKnowledgeViewModel Vm) BuildComponentsKnowledge(
        ComponentInventory inventory)
    {
        var state = new AppState { };
        var logger = new InMemoryLoggerService();
        var loc = new FakeLocalizationService();
        var svc = new StaticCiService { Inventory = inventory };
        var ciVm = new ComponentIntelligenceViewModel(state, logger, svc,
            new CompositeComponentCatalog(new CuratedComponentCatalog(), new WindowsFeaturesCatalog()), loc);
        state.CurrentServicingWorkspace = new ImageServicingWorkspace
        {
            State = ServicingWorkspaceState.Mounted,
            MountDirectory = @"C:\wf\mount"
        };
        ciVm.DiscoverAsync().GetAwaiter().GetResult();
        var vm = new ComponentKnowledgeViewModel(ciVm, state, logger, loc,
            new[] { ComponentCategory.OptionalFeature, ComponentCategory.Capability });
        return (state, vm);
    }

    [Fact]
    public void Present_Feature_Appears_Absent_Feature_Is_Hidden()
    {
        var present = new ComponentInventory
        {
            Discovered = true,
            Categories = new[]
            {
                new CategoryDiscoveryResult
                {
                    Category = ComponentCategory.OptionalFeature,
                    Status = InventoryStatus.Success,
                    Items = new List<IRawInventoryItem>
                    {
                        new RawOptionalFeature { Category = ComponentCategory.OptionalFeature,
                            RawIdentity = "Microsoft-Hyper-V", DisplayName = "Hyper-V",
                            FeatureStateValue = FeatureState.Enabled }
                    }
                }
            }
        };
        var (_, vm) = BuildComponentsKnowledge(present);

        // Present-in-image features are shown as curated rows (Part D / test 27).
        var hyperv = vm.Items.Single(i => i.Entry.Definition?.Id == "HyperV");
        Assert.True(hyperv.IsSelectable);
        Assert.NotEmpty(hyperv.RawIdentities);
        Assert.Equal("Microsoft-Hyper-V", hyperv.RawIdentities[0]);

        // Absent features (catalog-only rows with no raw match) are NOT shown.
        var absent = new ComponentInventory { Discovered = true, Categories = Array.Empty<CategoryDiscoveryResult>() };
        var (_, absentVm) = BuildComponentsKnowledge(absent);
        Assert.Empty(absentVm.Items);
        Assert.True(absentVm.IsEmpty);
    }

    [Fact]
    public void Feature_Selection_Builds_Strongly_Typed_Feature_Operation()
    {
        var inventory = new ComponentInventory
        {
            Discovered = true,
            Categories = new[]
            {
                new CategoryDiscoveryResult
                {
                    Category = ComponentCategory.OptionalFeature,
                    Status = InventoryStatus.Success,
                    Items = new List<IRawInventoryItem>
                    {
                        new RawOptionalFeature { Category = ComponentCategory.OptionalFeature,
                            RawIdentity = "Microsoft-Hyper-V", DisplayName = "Hyper-V",
                            FeatureStateValue = FeatureState.Enabled }
                    }
                }
            }
        };
        var (state, vm) = BuildComponentsKnowledge(inventory);
        var hyperv = vm.Items.Single(i => i.Entry.Definition?.Id == "HyperV");
        hyperv.IsSelected = true;

        var op = state.CurrentCustomizationPlan!.Operations.Single(o => o.OperationId == "feat|Microsoft-Hyper-V");
        Assert.Equal(CustomizationOperationType.DisableOptionalFeature, op.OperationType);
        Assert.Equal("Microsoft-Hyper-V", op.TargetIdentifier);
        Assert.Equal(OptimizationAction.Feature, op.ActionKind);
        Assert.Equal(OptimizationMechanism.DisableOptionalFeature, op.Mechanism);
        Assert.Equal(OptimizationScope.MountedImageFeature, op.Scope);
        Assert.Equal("feat|Microsoft-Hyper-V", op.ConflictKey);
    }

    [Fact]
    public void Capability_Definition_Maps_To_RemoveCapability_Operation()
    {
        var state = new AppState { };
        var logger = new InMemoryLoggerService();
        var loc = new FakeLocalizationService();

        var capDef = new ComponentDefinition
        {
            Id = "CapX",
            Category = ComponentCategory.Capability,
            DisplayNameKey = "Feat.CapX.DisplayName",
            ShortDescriptionKey = "Feat.CapX.Short",
            Recommendation = RecommendationLevel.OptionalRemove,
            Risk = RiskLevel.Low,
            Removal = RemovalSupport.Supported,
            Restore = RestoreSupport.Easy,
            Action = OptimizationAction.Feature,
            Mechanism = OptimizationMechanism.RemoveCapability,
            Scope = OptimizationScope.MountedImageFeature,
            TechnicalTargets = new[] { new TechnicalTarget { Category = ComponentCategory.Capability, Match = MatchMethod.Exact, Pattern = "OneCore.TestCap" } },
        };
        var inventory = new ComponentInventory
        {
            Discovered = true,
            Categories = new[]
            {
                new CategoryDiscoveryResult
                {
                    Category = ComponentCategory.Capability,
                    Status = InventoryStatus.Success,
                    Items = new List<IRawInventoryItem>
                    {
                        new RawCapability { Category = ComponentCategory.Capability,
                            RawIdentity = "OneCore.TestCap", DisplayName = "Test Capability",
                            CapState = CapabilityState.Installed }
                    }
                }
            }
        };
        var svc = new StaticCiService { Inventory = inventory };
        var ciVm = new ComponentIntelligenceViewModel(state, logger, svc, new InMemoryCatalog(capDef), loc);
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted, MountDirectory = @"C:\wf\mount" };
        ciVm.DiscoverAsync().GetAwaiter().GetResult();
        var vm = new ComponentKnowledgeViewModel(ciVm, state, logger, loc,
            new[] { ComponentCategory.Capability, ComponentCategory.OptionalFeature });

        var cap = vm.Items.Single(i => i.Entry.Definition?.Id == "CapX");
        // Capability rows are VISIBLE (knowledge) but apply is not supported in the
        // first tranche → checkbox disabled with an explicit reason (defect fix:
        // display eligibility and execution eligibility are separate).
        Assert.True(cap.IsCurated);
        Assert.False(FeatureConfigPolicy.IsCapabilityAllowed("OneCore.TestCap"));
        Assert.False(cap.IsApplySupported);
        Assert.False(cap.IsSelectable);
        Assert.Equal("Opt.ApplyUnsupported", cap.BlockReason);

        // Selecting must be a no-op — no silent Skipped operation may enter the plan.
        cap.IsSelected = true;
        Assert.Null(state.CurrentCustomizationPlan);
    }
}

public class Stage11p3PlanAndExecutionTests
{
    private sealed class RecordingProcessRunner : IProcessRunner
    {
        private readonly List<ProcessRequest> _requests = new();
        public IReadOnlyList<ProcessRequest> Requests => _requests;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            _requests.Add(request);
            if (request.Arguments.Contains("Get-MountedImageInfo"))
            {
                return Task.FromResult(new ProcessResult { ExitCode = 0, StandardOutput = "Mount Dir : C:\\wf\\mount\n" });
            }

            return Task.FromResult(new ProcessResult { ExitCode = 0, StandardOutput = string.Empty });
        }
    }

    [Fact]
    public void Review_Lists_Exact_Action_Type_For_Every_Operation()
    {
        var state = new AppState();
        var plan = new CustomizationPlan();
        plan.AddOperation(Op("appx|1", CustomizationOperationType.RemoveProvisionedAppx, OptimizationAction.Remove, "A"));
        plan.AddOperation(Op("feat|2", CustomizationOperationType.DisableOptionalFeature, OptimizationAction.Feature, "B"));
        plan.AddOperation(Op("svc|3", CustomizationOperationType.ConfigureOfflineService, OptimizationAction.Service, "C", serviceName: "DiagTrack", start: ServiceStartType.Disabled));
        plan.AddOperation(Op("reg|4", CustomizationOperationType.SetOfflineRegistryValue, OptimizationAction.Disable, "D",
            hive: "SOFTWARE", key: "K", value: "V", data: "0"));
        plan.AddOperation(Op("reg|5", CustomizationOperationType.SetOfflineRegistryValue, OptimizationAction.Configure, "E",
            hive: "DEFAULT_USER", key: "Software\\K2", value: "V2", data: "1"));
        state.CurrentCustomizationPlan = plan;

        var vm = new PlanReviewViewModel(state, new InMemoryLoggerService(),
            new FakeCustomizationExecutionService(), new FakeLocalizationService());
        Assert.Equal(5, vm.Operations.Count);
        Assert.Equal(1, vm.TotalRemoves);
        Assert.Equal(1, vm.TotalDisables);
        Assert.Equal(1, vm.TotalConfigures);
        Assert.Equal(1, vm.TotalServiceChanges);
        Assert.Equal(1, vm.TotalFeatures);
        Assert.Equal("Opt.Action.Feature", vm.Operations[1].ActionCaption);
        Assert.Equal("Opt.Scope.MountedImageFeature", vm.Operations[1].ScopeCaption);
        Assert.Equal("Plan.Reversal.Generic", vm.Operations[1].ReversalCaption);
    }

    [Fact]
    public void Reversal_Metadata_Round_Trips_Through_Frozen_Plan()
    {
        var state = new AppState();
        var plan = new CustomizationPlan();
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "opt|ShowFileExtensions|0",
            Category = CustomizationCategory.Personalization,
            OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            DisplayName = "Show file extensions",
            RegistryHive = "DEFAULT_USER",
            RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
            RegistryValueName = "HideFileExt",
            RegistryValueKind = OfflineRegistryValueKind.DWord,
            RegistryValueData = "0",
            RestoreValueData = "1",
            Scope = OptimizationScope.OfflineDefaultUser,
            ActionKind = OptimizationAction.Configure,
            Mechanism = OptimizationMechanism.ExplorerPreference,
            Risk = RiskClass.Safe,
            IsSelected = true,
        });
        state.CurrentCustomizationPlan = plan;
        Assert.Empty(plan.Validate());
        Assert.Equal(CustomizationPlanStatus.Validated, plan.Status);

        var snapshot = plan.FreezeForExecution();
        var op = snapshot.Operations.Single();
        Assert.Equal("1", op.RestoreValueData);
        Assert.Equal(OptimizationScope.OfflineDefaultUser, op.Scope);
        Assert.Equal(OptimizationMechanism.ExplorerPreference, op.Mechanism);
    }

    [Fact]
    public void New_Operation_Types_Validate_With_Target_And_Reject_Missing_Target()
    {
        var plan = new CustomizationPlan();
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "feat|Microsoft-Hyper-V",
            OperationType = CustomizationOperationType.DisableOptionalFeature,
            TargetIdentifier = "Microsoft-Hyper-V",
            Risk = RiskClass.Removable,
            IsSelected = true,
        });
        Assert.Empty(plan.Validate());

        var bad = new CustomizationPlan();
        bad.AddOperation(new CustomizationOperation
        {
            OperationId = "feat|",
            OperationType = CustomizationOperationType.DisableOptionalFeature,
            TargetIdentifier = null,
            Risk = RiskClass.Removable,
            IsSelected = true,
        });
        Assert.NotEmpty(bad.Validate());
        Assert.Equal(CustomizationPlanStatus.Draft, bad.Status);
    }

    [Fact]
    public async Task Execute_Feature_Disable_Invokes_Dism_With_FeatureName()
    {
        var runner = new RecordingProcessRunner();
        var ws = new ImageServicingWorkspace
        {
            State = ServicingWorkspaceState.Mounted,
            MountDirectory = @"C:\wf\mount"
        };
        var execution = new WindowsCustomizationExecutionService(
            runner, new FakeOfflineRegistryService(), new InMemoryLoggerService(), new FakeMountIdentityValidator());

        var plan = new CustomizationPlan();
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "feat|Microsoft-Hyper-V",
            OperationType = CustomizationOperationType.DisableOptionalFeature,
            DisplayName = "Hyper-V",
            TargetIdentifier = "Microsoft-Hyper-V",
            Risk = RiskClass.Removable,
            IsSelected = true,
        });
        Assert.Empty(plan.Validate());
        var result = await execution.ExecuteAsync(plan, ws);

        Assert.True(result.Success, result.Summary);
        var dism = runner.Requests.Select(r => r.Arguments).First(a => a.Contains("/Disable-Feature"));
        Assert.Contains("/FeatureName:\"Microsoft-Hyper-V\"", dism);
        Assert.Contains("/Image:\"C:\\wf\\mount\"", dism);
        Assert.Equal(CustomizationOperationStatus.Succeeded, result.Operations.Single().ExecutionStatus);
    }

    [Fact]
    public async Task Execute_Capability_Removal_Is_Skipped_First_Tranche()
    {
        var runner = new RecordingProcessRunner();
        var ws = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted, MountDirectory = @"C:\wf\mount" };
        var execution = new WindowsCustomizationExecutionService(
            runner, new FakeOfflineRegistryService(), new InMemoryLoggerService(), new FakeMountIdentityValidator());

        var plan = new CustomizationPlan();
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "cap|OneCore.TestCap",
            OperationType = CustomizationOperationType.RemoveCapability,
            DisplayName = "Cap",
            TargetIdentifier = "OneCore.TestCap",
            Risk = RiskClass.Removable,
            IsSelected = true,
        });
        Assert.Empty(plan.Validate());
        var result = await execution.ExecuteAsync(plan, ws);

        Assert.Equal(CustomizationOperationStatus.Skipped, result.Operations.Single().ExecutionStatus);
        Assert.DoesNotContain(runner.Requests, r => r.Arguments.Contains("/Remove-Capability"));
    }

    [Fact]
    public async Task Execute_Non_Allowlisted_Feature_Is_Skipped()
    {
        var runner = new RecordingProcessRunner();
        var ws = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted, MountDirectory = @"C:\wf\mount" };
        var execution = new WindowsCustomizationExecutionService(
            runner, new FakeOfflineRegistryService(), new InMemoryLoggerService(), new FakeMountIdentityValidator());

        var plan = new CustomizationPlan();
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "feat|Not-Reviewed",
            OperationType = CustomizationOperationType.DisableOptionalFeature,
            DisplayName = "Not reviewed",
            TargetIdentifier = "Not-Reviewed",
            Risk = RiskClass.Removable,
            IsSelected = true,
        });
        Assert.Empty(plan.Validate());
        var result = await execution.ExecuteAsync(plan, ws);

        Assert.Equal(CustomizationOperationStatus.Skipped, result.Operations.Single().ExecutionStatus);
        Assert.DoesNotContain(runner.Requests, r => r.Arguments.Contains("/Disable-Feature"));
    }

    private static CustomizationOperation Op(
        string id, CustomizationOperationType type, OptimizationAction action, string name,
        string? serviceName = null, ServiceStartType? start = null,
        string? hive = null, string? key = null, string? value = null, string? data = null)
        => new()
        {
            OperationId = id,
            OperationType = type,
            ActionKind = action,
            DisplayName = name,
            IsSelected = true,
            Risk = RiskClass.Safe,
            ServiceName = serviceName,
            ServiceStartType = start,
            RegistryHive = hive,
            RegistryKeyPath = key,
            RegistryValueName = value,
            RegistryValueData = data,
            Scope = type switch
            {
                CustomizationOperationType.DisableOptionalFeature => OptimizationScope.MountedImageFeature,
                CustomizationOperationType.ConfigureOfflineService => OptimizationScope.OfflineMachine,
                _ => OptimizationScope.OfflineMachine,
            },
        };
}

/// <summary>Catalog provider backed by an in-memory definition list (tests).</summary>
internal sealed class InMemoryCatalog : IComponentCatalogProvider
{
    private readonly IReadOnlyList<ComponentDefinition> _defs;
    public InMemoryCatalog(params ComponentDefinition[] defs) => _defs = defs;
    public IReadOnlyList<ComponentDefinition> GetDefinitions() => _defs;
}

/// <summary>Optimization catalog provider backed by an in-memory entry list (tests).</summary>
internal sealed class SyntheticCatalog : IOptimizationCatalogProvider
{
    private readonly IReadOnlyList<OptimizationDefinition> _entries;
    public SyntheticCatalog(params OptimizationDefinition[] entries) => _entries = entries;
    public IReadOnlyList<OptimizationDefinition> GetEntries() => _entries;
}

// ---------------------------------------------------------------------------
// Stage 11.3 REAL-DESKTOP DEFECT regression: "Windows Components tab shows ZERO
// items despite 12 implemented logical components".
// Root cause: the unified Customize Discover only refreshed the Apps knowledge
// VM; the Windows Components knowledge VM (same CI inventory, different category
// filter) was never refreshed and stayed in its pre-discovery empty state.
// ---------------------------------------------------------------------------
public class Stage11p3ComponentsTabDefectTests
{
    private sealed class StaticCiService : IComponentIntelligenceService
    {
        public ComponentInventory Inventory { get; set; } = new();
        public Task<ComponentInventory> DiscoverAsync(
            ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
            => Task.FromResult(Inventory);
    }

    private static ComponentInventory FeatureInventory(params (string Name, FeatureState State)[] features)
        => new()
        {
            Discovered = true,
            Categories = new[]
            {
                new CategoryDiscoveryResult
                {
                    Category = ComponentCategory.OptionalFeature,
                    Status = InventoryStatus.Success,
                    Items = features
                        .Select(f => (IRawInventoryItem)new RawOptionalFeature
                        {
                            Category = ComponentCategory.OptionalFeature,
                            RawIdentity = f.Name,
                            DisplayName = f.Name,
                            FeatureStateValue = f.State,
                            State = f.State.ToString()
                        })
                        .ToList()
                }
            }
        };

    private static ComponentKnowledgeViewModel BuildComponentsTab(AppState state, IComponentIntelligenceService svc)
    {
        var logger = new InMemoryLoggerService();
        var loc = new FakeLocalizationService();
        var ciVm = new ComponentIntelligenceViewModel(state, logger, svc,
            new CompositeComponentCatalog(new CuratedComponentCatalog(), new WindowsFeaturesCatalog()), loc);
        state.CurrentServicingWorkspace = new ImageServicingWorkspace
        {
            State = ServicingWorkspaceState.Mounted,
            MountDirectory = @"C:\wf\mount"
        };
        ciVm.DiscoverAsync().GetAwaiter().GetResult();
        return new ComponentKnowledgeViewModel(ciVm, state, logger, loc,
            new[] { ComponentCategory.OptionalFeature, ComponentCategory.Capability });
    }

    [Fact]
    public void OptionalFeature_Raw_Item_Maps_To_Windows_Components_Row()
    {
        var vm = BuildComponentsTab(new AppState(),
            new StaticCiService { Inventory = FeatureInventory(("Microsoft-Hyper-V", FeatureState.Enabled)) });

        Assert.False(vm.IsEmpty);
        Assert.Single(vm.Items);
        var row = vm.Items[0];
        Assert.Equal("HyperV", row.Entry.Definition?.Id);
        Assert.Equal("Microsoft-Hyper-V", row.RawIdentities[0]);
        Assert.True(row.IsApplySupported);   // feature is on the execution allowlist
        Assert.True(row.IsSelectable);
    }

    [Fact]
    public void Disabled_Feature_Remains_Visible()
    {
        // A Disabled feature is STILL present in the image and meaningful —
        // present states include Enabled AND Disabled (test area 3 / state filter).
        var vm = BuildComponentsTab(new AppState(),
            new StaticCiService { Inventory = FeatureInventory(("Containers-DisposableClientVM", FeatureState.Disabled)) });

        Assert.Single(vm.Items);
        Assert.Equal("WindowsSandbox", vm.Items[0].Entry.Definition?.Id);
        Assert.Equal(FeatureState.Disabled.ToString(), vm.Items[0].Entry.RepresentativeRaw?.State);
    }

    [Fact]
    public void AppX_Filter_Does_Not_Affect_Windows_Components()
    {
        // One CI discovery, two knowledge tabs: Apps shows AppX rows only;
        // Windows Components shows capability/optional-feature rows only.
        var inventory = new ComponentInventory
        {
            Discovered = true,
            Categories = new[]
            {
                new CategoryDiscoveryResult
                {
                    Category = ComponentCategory.AppX,
                    Status = InventoryStatus.Success,
                    Items = new List<IRawInventoryItem>
                    {
                        new RawAppxPackage { Category = ComponentCategory.AppX,
                            RawIdentity = "Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe",
                            DisplayName = "Weather", State = "Provisioned" }
                    }
                },
                new CategoryDiscoveryResult
                {
                    Category = ComponentCategory.OptionalFeature,
                    Status = InventoryStatus.Success,
                    Items = new List<IRawInventoryItem>
                    {
                        new RawOptionalFeature { Category = ComponentCategory.OptionalFeature,
                            RawIdentity = "Microsoft-Hyper-V", DisplayName = "Hyper-V",
                            FeatureStateValue = FeatureState.Enabled }
                    }
                }
            }
        };
        var state = new AppState();
        var logger = new InMemoryLoggerService();
        var loc = new FakeLocalizationService();
        var ciVm = new ComponentIntelligenceViewModel(state, logger, new StaticCiService { Inventory = inventory },
            new CompositeComponentCatalog(new CuratedComponentCatalog(), new WindowsFeaturesCatalog()), loc);
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted, MountDirectory = @"C:\wf\mount" };
        ciVm.DiscoverAsync().GetAwaiter().GetResult();

        var apps = new ComponentKnowledgeViewModel(ciVm, state, logger, loc); // default = AppX only
        var components = new ComponentKnowledgeViewModel(ciVm, state, logger, loc,
            new[] { ComponentCategory.OptionalFeature, ComponentCategory.Capability });

        var appsRows = apps.Items.Select(i => i.Entry.Definition?.Category).ToHashSet();
        var componentsRows = components.Items.Select(i => i.Entry.Definition?.Category).ToHashSet();
        Assert.Contains(ComponentCategory.AppX, appsRows);
        Assert.All(appsRows, c => Assert.Equal(ComponentCategory.AppX, c));
        Assert.Contains(ComponentCategory.OptionalFeature, componentsRows);
        Assert.All(componentsRows, c => Assert.NotEqual(ComponentCategory.AppX, c));
    }

    [Fact]
    public void Execution_Allowlist_Does_Not_Gate_Visibility()
    {
        // A reviewed feature that is NOT on the execution allowlist must stay
        // VISIBLE (knowledge), with the checkbox disabled + explicit reason —
        // it must not be filtered out of the list entirely.
        var def = new ComponentDefinition
        {
            Id = "SyntheticFeat",
            Category = ComponentCategory.OptionalFeature,
            DisplayNameKey = "Feat.SyntheticFeat.DisplayName",
            ShortDescriptionKey = "Feat.SyntheticFeat.Short",
            Recommendation = RecommendationLevel.OptionalRemove,
            Risk = RiskLevel.Low,
            Removal = RemovalSupport.Supported,
            Restore = RestoreSupport.Easy,
            Action = OptimizationAction.Feature,
            Mechanism = OptimizationMechanism.DisableOptionalFeature,
            Scope = OptimizationScope.MountedImageFeature,
            TechnicalTargets = new[] { new TechnicalTarget { Category = ComponentCategory.OptionalFeature, Match = MatchMethod.Exact, Pattern = "Not-Reviewed-Feature" } },
        };
        var inventory = new ComponentInventory
        {
            Discovered = true,
            Categories = new[]
            {
                new CategoryDiscoveryResult
                {
                    Category = ComponentCategory.OptionalFeature,
                    Status = InventoryStatus.Success,
                    Items = new List<IRawInventoryItem>
                    {
                        new RawOptionalFeature { Category = ComponentCategory.OptionalFeature,
                            RawIdentity = "Not-Reviewed-Feature", DisplayName = "X",
                            FeatureStateValue = FeatureState.Enabled }
                    }
                }
            }
        };
        var state = new AppState();
        var logger = new InMemoryLoggerService();
        var loc = new FakeLocalizationService();
        var ciVm = new ComponentIntelligenceViewModel(state, logger, new StaticCiService { Inventory = inventory },
            new InMemoryCatalog(def), loc);
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted, MountDirectory = @"C:\wf\mount" };
        ciVm.DiscoverAsync().GetAwaiter().GetResult();
        var vm = new ComponentKnowledgeViewModel(ciVm, state, logger, loc,
            new[] { ComponentCategory.OptionalFeature, ComponentCategory.Capability });

        var row = Assert.Single(vm.Items);           // VISIBLE
        Assert.False(FeatureConfigPolicy.IsFeatureAllowed("Not-Reviewed-Feature"));
        Assert.False(row.IsApplySupported);          // execution not supported
        Assert.False(row.IsSelectable);              // checkbox disabled
        Assert.Equal("Opt.ApplyUnsupported", row.BlockReason); // explicit reason
    }

    [Fact]
    public async Task Unified_Discover_Populates_Apps_And_Windows_Components_Together()
    {
        // THE regression: after one unified Discover, BOTH knowledge tabs must
        // show rows. Before the fix the Windows Components tab stayed in its
        // pre-discovery empty state (zero rows, "请先发现当前映像中的组件。").
        var inventory = new ComponentInventory
        {
            Discovered = true,
            Categories = new[]
            {
                new CategoryDiscoveryResult
                {
                    Category = ComponentCategory.AppX,
                    Status = InventoryStatus.Success,
                    Items = new List<IRawInventoryItem>
                    {
                        new RawAppxPackage { Category = ComponentCategory.AppX,
                            RawIdentity = "Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe",
                            DisplayName = "Weather", State = "Provisioned" }
                    }
                },
                new CategoryDiscoveryResult
                {
                    Category = ComponentCategory.OptionalFeature,
                    Status = InventoryStatus.Success,
                    Items = new List<IRawInventoryItem>
                    {
                        new RawOptionalFeature { Category = ComponentCategory.OptionalFeature,
                            RawIdentity = "Microsoft-Hyper-V", DisplayName = "Hyper-V",
                            FeatureStateValue = FeatureState.Enabled }
                    }
                }
            }
        };
        var state = new AppState();
        var logger = new InMemoryLoggerService();
        var loc = new FakeLocalizationService();
        var ciVm = new ComponentIntelligenceViewModel(state, logger, new StaticCiService { Inventory = inventory },
            new CompositeComponentCatalog(new CuratedComponentCatalog(), new WindowsFeaturesCatalog()), loc);
        state.CurrentServicingWorkspace = new ImageServicingWorkspace
        {
            State = ServicingWorkspaceState.Mounted,
            MountDirectory = @"C:\wf\mount"
        };

        var components = new ComponentsViewModel(state, logger, new FakeCustomizationDiscoveryService(),
            new FakeCustomizationDefinitionProvider());
        var apps = new ComponentKnowledgeViewModel(ciVm, state, logger, loc);
        var componentsK = new ComponentKnowledgeViewModel(ciVm, state, logger, loc,
            new[] { ComponentCategory.OptionalFeature, ComponentCategory.Capability });
        var customize = new CustomizeStepViewModel(components, apps, componentsK,
            ComponentKnowledgeTestFactory.MakeOptimization(state, logger, OptimizationTab.Services),
            ComponentKnowledgeTestFactory.MakeOptimization(state, logger, OptimizationTab.Privacy),
            ComponentKnowledgeTestFactory.MakeOptimization(state, logger, OptimizationTab.System),
            ComponentKnowledgeTestFactory.MakeOptimization(state, logger, OptimizationTab.Personalization));

        Assert.True(customize.CanDiscover);
        await ((AsyncRelayCommand)customize.DiscoverCommand).ExecuteAsync(null);

        // Apps tab populated…
        Assert.True(apps.HasInventory);
        Assert.Contains(apps.Items, i => i.Entry.Definition?.Id == "Weather");
        // …AND Windows Components tab populated from the SAME single discovery.
        Assert.True(componentsK.HasInventory, "Windows Components tab must have inventory after Discover.");
        Assert.Contains(componentsK.Items, i => i.Entry.Definition?.Id == "HyperV");
        Assert.False(customize.IsDiscovering);
    }

    [Fact]
    public void Catalog_Targets_Match_Documented_25H2_FeatureNames()
    {
        // Pins the 12 logical Windows Components to the exact DISM /Get-Features
        // identities on Windows 11 25H2 (evidence-backed; no fuzzy loosening).
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft-Hyper-V",
            "Microsoft-Hyper-V-Management-PowerShell",
            "Containers-DisposableClientVM",
            "Microsoft-Windows-Subsystem-Linux",
            "VirtualMachinePlatform",
            "OpenSSH.Client",
            "OpenSSH.Server",
            "WindowsMediaPlayer",
            "Internet-Printing-Client",
            "ScanManagementConsole",
            "Printing-XPSServices-Features",
            "MicrosoftWindowsPowerShellV2Root",
            "HypervisorPlatform",
        };

        var catalogTargets = new WindowsFeaturesCatalog().GetDefinitions()
            .SelectMany(d => d.TechnicalTargets)
            .Where(t => t.Category == ComponentCategory.OptionalFeature)
            .Select(t => t.Pattern)
            .ToList();

        Assert.Equal(expected.Count, catalogTargets.Count);
        foreach (var pattern in catalogTargets)
        {
            Assert.Contains(expected, e => string.Equals(e, pattern, StringComparison.OrdinalIgnoreCase));
        }
    }
}
