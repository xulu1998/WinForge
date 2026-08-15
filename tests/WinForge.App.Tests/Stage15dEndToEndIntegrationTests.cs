using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.Core.ComponentIntelligence;
using WinForge.Core.Models;
using WinForge.Core.Profiles;
using WinForge.Core.Services;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.ComponentIntelligence;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.Profiles;
using Xunit;

namespace WinForge.App.Tests;

// =====================================================================
// Phase 15 Stage 15.3 — END-TO-END Profile → Review → Apply (ADR-096)
//
// The real-stream blocker: BuildPlan failed safe because plan operations were
// built WITHOUT execution payloads (registry targets / service names / package
// identities) — the validator correctly rejected them. Stage 15.3:
//   - OptimizationDefinitionValidator (fail fast, never weaken the validator)
//   - BuildPlan maps complete payloads (svc:/opt:/feat:/appx:/cap: conventions)
//   - six primary profiles produce non-null validated BuildPlans
//   - extras affect the ACTUAL executable plan, not only the delta report
//   - profile → Customize sync uses the shared plan (single authoritative state)
//   - preview automatic count == Review selected count (one semantics)
// =====================================================================

public sealed class Stage15dEndToEndIntegrationTests
{
    private readonly ProfileExecutionService _service = new();
    private readonly ProfileCatalog _catalog = new();
    private readonly OptimizationCatalog _optimizations = new();

    private IReadOnlyList<ProfileDefinition> AllProfiles => _catalog.GetProfiles();

    /// <summary>Real-shaped candidate stream: fixture families + curated-only
    /// consumer/cloud AppX + the full optimization catalog (as the real capture).</summary>
    private ProfileCandidateBuildResult BuildRealStream()
    {
        var inventory = new List<ProfileInventoryInput>();

        var classifier = RealInventoryFixture.Classifier;
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "25H2-Pro-zhCN-component-families.json");
        Assert.True(System.IO.File.Exists(path), $"fixture missing at {path}");
        using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            if (e.GetProperty("classification").GetString() is not ("Curated" or "KnownDeep"))
            {
                continue;
            }

            var rep = e.GetProperty("representative").GetString()!;
            var src = e.GetProperty("source").GetString()!;
            var category = Enum.TryParse<ComponentCategory>(src, out var c) ? c : ComponentCategory.Unknown;
            var k = classifier.Classify(rep);
            if (k is not null)
            {
                inventory.Add(new ProfileInventoryInput { RawIdentity = rep, Category = category, Deep = k });
            }
        }

        inventory.Add(new ProfileInventoryInput
        {
            RawIdentity = "Microsoft.OneDriveSync_8wekyb3d8bbwe",
            Category = ComponentCategory.AppX,
            Deep = null,
            Curated = new ComponentDefinition
            {
                Id = "OneDrive", Category = ComponentCategory.AppX,
                Recommendation = RecommendationLevel.UsuallyKeep, Risk = RiskLevel.Medium,
                Removal = RemovalSupport.Supported,
            },
        });
        inventory.Add(new ProfileInventoryInput
        {
            RawIdentity = "Microsoft.Teams_8wekyb3d8bbwe",
            Category = ComponentCategory.AppX,
            Deep = null,
            Curated = new ComponentDefinition
            {
                Id = "Teams", Category = ComponentCategory.AppX,
                Recommendation = RecommendationLevel.OptionalRemove, Risk = RiskLevel.Low,
                Removal = RemovalSupport.Supported,
            },
        });
        inventory.Add(new ProfileInventoryInput
        {
            RawIdentity = "Clipchamp.Clipchamp_8wekyb3d8bbwe",
            Category = ComponentCategory.AppX,
            Deep = null,
            Curated = new ComponentDefinition
            {
                Id = "Clipchamp", Category = ComponentCategory.AppX,
                Recommendation = RecommendationLevel.RecommendedRemove, Risk = RiskLevel.Low,
                Removal = RemovalSupport.Supported,
            },
        });

        return ProfileCandidateService.BuildCandidates(inventory, _optimizations.GetEntries());
    }

    private ProfileDeltaReport Report(string profileId, ProfileCandidateBuildResult built, IReadOnlySet<GamingExtra>? extras = null)
    {
        var profile = AllProfiles.Single(p => p.Id == profileId);
        var present = built.Subjects.Select(s => s.LogicalId).ToHashSet(StringComparer.Ordinal);
        return _service.GenerateDelta(profile, built.Subjects, extras ?? new HashSet<GamingExtra>(),
            Array.Empty<string>(), present, AllProfiles);
    }

    private (CustomizationPlan? Plan, IReadOnlyList<string> Issues) Build(
        string profileId, ProfileCandidateBuildResult built, IReadOnlySet<GamingExtra>? extras = null,
        IReadOnlyCollection<string>? overrides = null)
    {
        var profile = AllProfiles.Single(p => p.Id == profileId);
        var present = built.Subjects.Select(s => s.LogicalId).ToHashSet(StringComparer.Ordinal);
        return _service.BuildPlan(profile, built.Subjects, extras ?? new HashSet<GamingExtra>(),
            overrides ?? Array.Empty<string>(), present, AllProfiles);
    }

    // =====================================================================
    // 1. DEFINITION VALIDATOR (§2/§5) — the whole catalog is clean
    // =====================================================================

    [Fact]
    public void Optimization_Catalog_All_Definitions_Validate()
    {
        var issues = OptimizationDefinitionValidator.ValidateCatalog(_optimizations.GetEntries());
        Assert.Empty(issues);
    }

    [Fact]
    public void ActivityHistory_Has_A_Valid_Offline_Registry_Target()
    {
        var def = _optimizations.GetEntries().Single(d => d.Id == "ActivityHistory");
        var issues = OptimizationDefinitionValidator.ValidateDefinition(def);
        Assert.Empty(issues);
        var target = Assert.Single(def.RegistryTargets);
        Assert.Equal("SOFTWARE", target.Hive);
        Assert.Equal("Policies\\Microsoft\\Windows\\System", target.KeyPath);
        Assert.Equal("EnableActivityHistory", target.ValueName);
        Assert.Equal(OfflineRegistryValueKind.DWord, target.ValueKind);
        Assert.Equal("0", target.RecommendedData);

        // The profile plan maps the target into a COMPLETE registry operation.
        var built = BuildRealStream();
        var (plan, _) = Build("Developer", built);
        Assert.NotNull(plan);
        var op = plan!.Operations.FirstOrDefault(o => o.OperationId == "opt|ActivityHistory|0");
        Assert.NotNull(op);
        Assert.Equal("SOFTWARE", op!.RegistryHive);
        Assert.Equal("Policies\\Microsoft\\Windows\\System", op.RegistryKeyPath);
        Assert.Equal("EnableActivityHistory", op.RegistryValueName);
        Assert.Equal(OfflineRegistryValueKind.DWord, op.RegistryValueKind);
        Assert.Equal("0", op.RegistryValueData);
    }

    [Fact]
    public void Service_Definitions_Use_Canonical_Service_Identity()
    {
        var defs = _optimizations.GetEntries()
            .Where(d => d.Mechanism == OptimizationMechanism.ServiceStartup).ToList();
        Assert.NotEmpty(defs);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in defs)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.ServiceName), $"{d.Id} must have ServiceName");
            // Every change-eligible service must be on the trusted allowlist with
            // a proposed start type; NeverRemove informational entries (RpcSs) are
            // exempt (they can never become a change).
            if (d.Recommendation != RecommendationLevel.NeverRemove)
            {
                Assert.True(ServiceConfigPolicy.IsConfigurable(d.ServiceName),
                    $"{d.Id} service '{d.ServiceName}' must be on the trusted allowlist");
                Assert.NotNull(d.ProposedStartType);
            }

            var key = "svc|" + d.ServiceName;
            Assert.True(keys.Add(key), $"duplicate canonical service identity '{key}'");
            Assert.False(key == "svc|", "empty canonical service identity");
        }
    }

    // =====================================================================
    // 2. SIX-PROFILE BuildPlan GENERATION (§11) — non-null, validated
    // =====================================================================

    [Fact]
    public void Six_Primary_Profiles_Produce_NonNull_Validated_BuildPlans()
    {
        var built = BuildRealStream();
        foreach (var profileId in new[] { "Balanced", "Gaming", "DedicatedGaming", "Developer", "Office", "Lightweight" })
        {
            var (plan, issues) = Build(profileId, built);
            Assert.True(plan is not null, $"{profileId} BuildPlan must be non-null. Issues: {string.Join("; ", issues)}");
            Assert.Empty(issues);
        }
    }

    [Fact]
    public void BuildPlan_Operations_Carry_Complete_Execution_Payloads()
    {
        var built = BuildRealStream();
        var (plan, _) = Build("Gaming", built);
        Assert.NotNull(plan);
        Assert.NotEmpty(plan!.Operations);

        foreach (var op in plan.Operations)
        {
            switch (op.OperationType)
            {
                case CustomizationOperationType.ConfigureOfflineService:
                    Assert.False(string.IsNullOrWhiteSpace(op.ServiceName), "service op must carry ServiceName");
                    Assert.NotNull(op.ServiceStartType);
                    Assert.True(ServiceConfigPolicy.IsConfigurable(op.ServiceName));
                    break;
                case CustomizationOperationType.SetOfflineRegistryValue:
                    Assert.False(string.IsNullOrWhiteSpace(op.RegistryHive));
                    Assert.False(string.IsNullOrWhiteSpace(op.RegistryKeyPath));
                    Assert.False(string.IsNullOrWhiteSpace(op.RegistryValueName));
                    Assert.NotNull(op.RegistryValueKind);
                    break;
                case CustomizationOperationType.RemoveProvisionedAppx:
                case CustomizationOperationType.DisableOptionalFeature:
                    Assert.False(string.IsNullOrWhiteSpace(op.TargetIdentifier));
                    break;
            }
        }
    }

    [Fact]
    public void BuildPlan_No_Duplicate_Canonical_Operation_Keys()
    {
        var built = BuildRealStream();
        foreach (var profileId in new[] { "Balanced", "Gaming", "DedicatedGaming", "Developer", "Office", "Lightweight" })
        {
            var (plan, issues) = Build(profileId, built);
            Assert.NotNull(plan);
            Assert.Empty(issues);
            var keys = plan!.Operations.Select(o => o.ConflictKey).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
            Assert.All(plan.Operations, o => Assert.False(o.ConflictKey is "svc|" or "reg|||" or "feat|" or "pkg|"));
        }
    }

    // =====================================================================
    // 3. DELTA vs BuildPlan counts (§12) — every difference explainable
    // =====================================================================

    [Fact]
    public void Delta_And_BuildPlan_Counts_Are_Consistent_And_Explainable()
    {
        var built = BuildRealStream();
        foreach (var profileId in new[] { "Balanced", "Gaming", "DedicatedGaming", "Developer", "Office", "Lightweight" })
        {
            var report = Report(profileId, built);
            var (plan, _) = Build(profileId, built);
            Assert.NotNull(plan);
            Assert.True(plan!.SelectedOperations.Count > 0, $"{profileId} must have selected (auto) operations");
            Assert.True(plan.Operations.Count >= plan.SelectedOperations.Count);
            // Every selected op stems from a reported AutoApply change — no growth
            // beyond the delta, and any difference (recommend ops present-unselected,
            // canonical dedup merges) is deterministic and validator-checked.
            Assert.True(plan.SelectedOperations.Count <= report.ChangeCount,
                $"{profileId}: selected ({plan.SelectedOperations.Count}) must not exceed delta ({report.ChangeCount})");
        }
    }

    // =====================================================================
    // 4. EXTRAS AFFECT THE ACTUAL EXECUTABLE PLAN (§8)
    // =====================================================================

    [Fact]
    public void Lightweight_Xbox_Extra_Removes_Xbox_Services_From_Executable_Plan()
    {
        var built = BuildRealStream();
        var (without, _) = Build("Lightweight", built);
        Assert.NotNull(without);
        foreach (var svc in new[] { "XblAuthManager", "XboxGipSvc", "XboxNetApiSvc" })
        {
            Assert.Contains(without!.Operations, o => o.OperationId == "svc|" + svc && o.IsSelected);
        }

        var (with, _) = Build("Lightweight", built, new HashSet<GamingExtra> { GamingExtra.XboxGamePass });
        Assert.NotNull(with);
        foreach (var svc in new[] { "XblAuthManager", "XboxGipSvc", "XboxNetApiSvc" })
        {
            Assert.DoesNotContain(with!.Operations, o => o.OperationId == "svc|" + svc);
        }
    }

    [Fact]
    public void Wsl_Developer_And_Print_And_Remote_Extras_Keep_Their_Ecosystems()
    {
        var built = BuildRealStream();

        // Lightweight + WSL/Docker: virtualization trims leave the executable plan.
        var wslOn = Build("Lightweight", built, new HashSet<GamingExtra> { GamingExtra.WslDocker }).Plan;
        Assert.NotNull(wslOn);
        Assert.DoesNotContain(wslOn!.Operations,
            o => o.OperationId is "feat|Wsl" or "feat|VirtualMachinePlatform" or "feat|WindowsSandbox");

        // Gaming PC + Print/Scan: printing families stay out of the plan.
        var printOn = Build("Gaming", built, new HashSet<GamingExtra> { GamingExtra.PrintScan }).Plan;
        Assert.NotNull(printOn);
        Assert.DoesNotContain(printOn!.Operations,
            o => o.DisplayName.Contains("Print", StringComparison.OrdinalIgnoreCase)
                || o.DisplayName.Contains("Scan", StringComparison.OrdinalIgnoreCase));

        // Gaming PC + Remote Desktop: RDP stack kept.
        var remoteOn = Build("Gaming", built, new HashSet<GamingExtra> { GamingExtra.RemoteDesktop }).Plan;
        Assert.NotNull(remoteOn);
        Assert.DoesNotContain(remoteOn!.Operations,
            o => o.DisplayName.Contains("Remote", StringComparison.OrdinalIgnoreCase));
    }

    // =====================================================================
    // 5. MANUAL OVERRIDE (§7) — authoritative, excluded from the plan
    // =====================================================================

    [Fact]
    public void Manual_Override_Keeps_The_Item_Out_Of_The_Executable_Plan()
    {
        var built = BuildRealStream();
        var (plan, _) = Build("Gaming", built, null, new[] { "PhoneLink" });
        Assert.NotNull(plan);
        Assert.DoesNotContain(plan!.Operations, o => o.DisplayName == "PhoneLink");
        // The rest of the auto set still executes.
        Assert.True(plan.SelectedOperations.Count > 0);
    }

    // =====================================================================
    // 6. PROFILE → CUSTOMIZE SYNC (§6/§9) — single authoritative shared plan
    // =====================================================================

    [Fact]
    public void Profile_Selection_Synchronizes_The_Shared_Plan_And_Counts_Match()
    {
        var state = new AppState
        {
            CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                State = ServicingWorkspaceState.Mounted,
                MountDirectory = @"C:\wf\mount",
            },
        };
        var logger = new InMemoryLoggerService();
        var rm = new System.Resources.ResourceManager(
            "WinForge.App.Resources.Strings", typeof(ComponentKnowledgeViewModel).Assembly);
        var loc = new WinForge.App.Localization.ResourceManagerLocalizationService(
            rm, System.Globalization.CultureInfo.GetCultureInfo("en"));
        var ctx = new RecommendationContextService(new RecommendationEngine(), new ProfileCatalog(), state);
        var components = new ComponentsViewModel(
            state, logger, new FakeCustomizationDiscoveryService(), new FakeCustomizationDefinitionProvider());
        var ciVm = new ComponentIntelligenceViewModel(state, logger,
            new Stage15InventoryCiService(),
            new CompositeComponentCatalog(new CuratedComponentCatalog(), new WindowsFeaturesCatalog()), loc);
        var knowledge = new ComponentKnowledgeViewModel(ciVm, state, logger, loc, null, ctx);
        var componentsKnowledge = new ComponentKnowledgeViewModel(ciVm, state, logger, loc,
            new[] { ComponentCategory.OptionalFeature, ComponentCategory.Capability }, ctx);
        var catalog = new OptimizationCatalog();
        var customize = new CustomizeStepViewModel(
            components, knowledge, componentsKnowledge,
            new OptimizationKnowledgeViewModel(state, logger, loc, catalog, OptimizationTab.Services, ctx),
            new OptimizationKnowledgeViewModel(state, logger, loc, catalog, OptimizationTab.Privacy, ctx),
            new OptimizationKnowledgeViewModel(state, logger, loc, catalog, OptimizationTab.System, ctx),
            new OptimizationKnowledgeViewModel(state, logger, loc, catalog, OptimizationTab.Personalization, ctx),
            ctx, loc);

        // Select Gaming PC → the shared plan is populated from the profile's
        // AutoApply set (single authoritative state — no parallel hidden plan).
        customize.Profiles!.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;
        Assert.True(customize.Profiles.TrimCount > 0,
            $"adopt-eligible (profile-driven Low) subjects must exist for Gaming; TrimCount={customize.Profiles.TrimCount}, preview={customize.Profiles.ProfilePreviewText.Replace("\n", " | ")}");
        Assert.True(state.CurrentCustomizationPlan is not null || customize.SelectedTotal > 0,
            $"plan null AND SelectedTotal={customize.SelectedTotal}");
        Assert.True(state.CurrentCustomizationPlan is not null,
            $"state plan null; SelectedTotal={customize.SelectedTotal}; TrimCount={customize.Profiles.TrimCount}");
        var plan = state.CurrentCustomizationPlan;
        Assert.NotNull(plan);
        Assert.True(plan!.SelectedOperations.Count > 0, "profile selection must adopt AutoApply items");

        // Every selected operation carries a complete payload (single source of
        // truth — the shared plan feeds Review/Apply directly).
        Assert.All(plan.SelectedOperations, op =>
        {
            if (op.OperationType == CustomizationOperationType.ConfigureOfflineService)
            {
                Assert.False(string.IsNullOrWhiteSpace(op.ServiceName));
                Assert.NotNull(op.ServiceStartType);
            }
            else if (op.OperationType == CustomizationOperationType.SetOfflineRegistryValue)
            {
                Assert.False(string.IsNullOrWhiteSpace(op.RegistryHive));
                Assert.False(string.IsNullOrWhiteSpace(op.RegistryKeyPath));
                Assert.False(string.IsNullOrWhiteSpace(op.RegistryValueName));
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(op.TargetIdentifier));
            }
        });

        // The preview's "Automatic changes" count and Review's selected count
        // share ONE semantics (profile-driven Low changes only — §10). The only
        // allowed difference is canonical dedup: SpotlightFeatures (Privacy) and
        // DisableSpotlight (Personalization) both write the same registry value,
        // so the plan merges them into ONE operation with provenance retained.
        var preview = customize.Profiles.ProfilePreviewText;
        var automatic = int.Parse(preview.Split('\n')
            .First(l => l.StartsWith("Automatic changes:", StringComparison.Ordinal))
            ["Automatic changes:".Length..].Trim());
        Assert.True(plan.SelectedOperations.Count <= automatic,
            $"Review selected ({plan.SelectedOperations.Count}) must not exceed preview automatic ({automatic})");
        var mergedSpotlight = plan.Operations.FirstOrDefault(o =>
            o.SourceDefinitionIds.Contains("SpotlightFeatures", StringComparer.Ordinal)
            && o.SourceDefinitionIds.Contains("DisableSpotlight", StringComparer.Ordinal));
        Assert.NotNull(mergedSpotlight);
        Assert.Equal(automatic, plan.SelectedOperations.Count
            + (mergedSpotlight is not null ? 1 : 0));
    }

    // =====================================================================
    // 7. FAILURE PRESENTATION + RESULT SYNC (§15/§16) — no stale/all-success lie
    // =====================================================================

    [Fact]
    public async System.Threading.Tasks.Task Apply_Failure_Is_Shown_Not_All_Success()
    {
        var state = new AppState
        {
            CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                State = ServicingWorkspaceState.Mounted,
                MountDirectory = @"C:\wf\mount",
            },
        };
        var logger = new InMemoryLoggerService();
        var plan = new CustomizationPlan();
        var ok = new CustomizationOperation
        {
            OperationId = "a", DisplayName = "Ok op", OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            TargetIdentifier = "X", Risk = RiskClass.Safe, IsSelected = true,
        };
        var bad = new CustomizationOperation
        {
            OperationId = "b", DisplayName = "Bad op", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K", RegistryValueName = "V",
            Risk = RiskClass.Safe, IsSelected = true, ErrorDetails = "Disk locked",
        };
        plan.AddOperation(ok);
        plan.AddOperation(bad);
        bad.ExecutionStatus = CustomizationOperationStatus.FailedRecoverable;
        ok.ExecutionStatus = CustomizationOperationStatus.Succeeded;
        plan.Validate(); // -> Validated (CanApply requires it)
        state.CurrentCustomizationPlan = plan;

        var exec = new FakeCustomizationExecutionService
        {
            Result = new CustomizationResult { TotalOperations = 2, Succeeded = 1, FailedOperations = 1, Operations = new[] { ok, bad } },
        };
        var vm = new PlanReviewViewModel(state, logger, exec);

        await vm.ApplyAsync();

        Assert.Equal(CustomizationExecutionState.CompletedWithErrors, state.CustomizationExecutionState);
        Assert.True(vm.HasFailedOperations);
        var failed = Assert.Single(vm.FailedOperations);
        Assert.Equal("Bad op", failed.DisplayName);
        Assert.False(string.IsNullOrWhiteSpace(failed.Reason));
        // The successful op is NOT reported as failed; the failed one is NOT
        // silently swallowed (no all-succeeded lie).
        Assert.DoesNotContain(vm.FailedOperations, f => f.DisplayName == "Ok op");
    }
}
