using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.Core.ComponentIntelligence;
using WinForge.Core.Models;
using WinForge.Core.Profiles;
using WinForge.Core.Services;
using WinForge.Infrastructure.ComponentIntelligence;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.Profiles;
using Xunit;

namespace WinForge.App.Tests;

// =====================================================================
// Phase 15 Stage 15.2 — REAL PROFILE DIFFERENTIATION + PLAN ACCOUNTING
// (ADR-095)
//
// Tests the Stage 15.2 fixes on real-media-shaped streams:
//   - 757 accounting invariant (every inventory object in exactly one bucket)
//   - InventoryBySource vs PlanChangesByOperationType split
//   - unified candidate stream (inventory + optimization definitions, dedup)
//   - Gaming vs Dedicated real semantic difference
//   - Office non-zero meaningful plan, Balanced meaningful baseline
//   - Developer non-inventory actions, changeCount semantics
//   - extras real-plan behavior, v2 semantics, preview same-source-of-truth
// =====================================================================

public sealed class Stage15bRealDifferentiationTests
{
    private readonly ProfileExecutionService _service = new();
    private readonly ProfileCatalog _catalog = new();
    private readonly CuratedComponentCatalog _curated = new();

    // =====================================================================
    // 1. 757 ACCOUNTING INVARIANT — no unexplained loss (§1)
    // =====================================================================

    [Fact]
    public void Real_Inventory_Accounting_Invariant_757_No_Unexplained_Loss()
    {
        // Phase 14 AUTHORITATIVE per-source split: AppX 47 (23 curated / 21 known /
        // 3 unknown), Capability 425 (2/385/38), CbsPackage 149 (0/149/0),
        // OptionalFeature 136 (8/90/38).
        var inputs = new List<ProfileInventoryInput>();
        AddBucket(inputs, ComponentCategory.AppX, deep: 21, curated: 23, unknown: 3);
        AddBucket(inputs, ComponentCategory.Capability, deep: 385, curated: 2, unknown: 38);
        AddBucket(inputs, ComponentCategory.CbsPackage, deep: 149, curated: 0, unknown: 0);
        AddBucket(inputs, ComponentCategory.OptionalFeature, deep: 90, curated: 8, unknown: 38);
        Assert.Equal(757, inputs.Count);

        var built = ProfileCandidateService.BuildCandidates(inputs, Array.Empty<OptimizationDefinition>());
        var a = built.Accounting;

        Assert.True(a.IsBalanced, $"757 invariant broken: accounted={a.Accounted} of {a.TotalInventory}");
        Assert.Equal(757, a.TotalInventory);
        Assert.Equal(645, a.EvaluatedForProfile);              // 21+385+149+90
        Assert.Equal(33, a.CuratedOutsideDeepInventory);        // 23+2+0+8
        Assert.Equal(79, a.ExcludedUnknownKnowledge);           // 3+38+0+38
        Assert.Equal(0, a.ExcludedUnsupportedSource);
        Assert.Equal(0, a.ExcludedFilteredDuplicate);
        Assert.Equal(0, a.ExcludedNotApplicable);
        Assert.Equal(0, a.ExcludedOther);
        Assert.Equal(678, a.Evaluated);

        // InventoryBySource — evaluated objects per source.
        Assert.Equal(44, a.BySource[ComponentCategory.AppX]);
        Assert.Equal(387, a.BySource[ComponentCategory.Capability]);
        Assert.Equal(149, a.BySource[ComponentCategory.CbsPackage]);
        Assert.Equal(98, a.BySource[ComponentCategory.OptionalFeature]);
    }

    [Fact]
    public void Unsupported_Providers_Are_Explicitly_Accounted()
    {
        var inputs = new[]
        {
            new ProfileInventoryInput { RawIdentity = "svc", Category = ComponentCategory.Service },
            new ProfileInventoryInput { RawIdentity = "drv", Category = ComponentCategory.Driver },
            new ProfileInventoryInput { RawIdentity = "lang", Category = ComponentCategory.Language },
        };
        var built = ProfileCandidateService.BuildCandidates(inputs, Array.Empty<OptimizationDefinition>());
        var a = built.Accounting;
        Assert.Equal(3, a.ExcludedUnsupportedSource);
        Assert.True(a.IsBalanced);
        Assert.Empty(built.Subjects);
    }

    private static void AddBucket(List<ProfileInventoryInput> inputs, ComponentCategory category,
        int deep, int curated, int unknown)
    {
        for (var i = 0; i < deep; i++)
        {
            inputs.Add(new ProfileInventoryInput
            {
                RawIdentity = $"{category}:deep:{i}",
                Category = category,
                Deep = new DeepComponentKnowledge { CanonicalId = $"{category}D{i}" },
            });
        }

        for (var i = 0; i < curated; i++)
        {
            inputs.Add(new ProfileInventoryInput
            {
                RawIdentity = $"{category}:cur:{i}",
                Category = category,
                Curated = new ComponentDefinition { Id = $"{category}C{i}", Category = category },
            });
        }

        for (var i = 0; i < unknown; i++)
        {
            inputs.Add(new ProfileInventoryInput { RawIdentity = $"{category}:unk:{i}", Category = category });
        }
    }

    // =====================================================================
    // 2. UNIFIED CANDIDATE STREAM — inventory + optimization definitions (§7)
    // =====================================================================

    /// <summary>
    /// Real-shaped candidate stream: the real-derived fixture families (deep
    /// knowledge) + the consumer/productivity/cloud/communication AppX that the
    /// profiles actually target + the non-inventory optimization definitions.
    /// </summary>
    private ProfileCandidateBuildResult BuildRealLikeStream()
    {
        var inventory = new List<ProfileInventoryInput>();

        // a) Real-derived fixture families (79 Curated/KnownDeep entries).
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "25H2-Pro-zhCN-component-families.json");
        Assert.True(File.Exists(path), $"fixture missing at {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            if (e.GetProperty("classification").GetString() is not ("Curated" or "KnownDeep"))
            {
                continue;
            }

            var representative = e.GetProperty("representative").GetString()!;
            var source = e.GetProperty("source").GetString()!;
            var category = Enum.TryParse<ComponentCategory>(source, out var c) ? c : ComponentCategory.Unknown;
            var k = RealInventoryFixture.Classifier.Classify(representative);
            if (k is not null)
            {
                inventory.Add(new ProfileInventoryInput { RawIdentity = representative, Category = category, Deep = k });
            }
        }

        // b) Real-like AppX the profiles steer (Office trims, gaming differentiation).
        inventory.Add(Deep("Solitaire", ComponentCategory.AppX, ComponentFunctionCategory.Communication,
            ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
            ComponentProtectionLevel.None, ComponentProfileTag.ConsumerContent));
        inventory.Add(Deep("GetHelp", ComponentCategory.AppX, ComponentFunctionCategory.Communication,
            ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
            ComponentProtectionLevel.None, ComponentProfileTag.ConsumerContent));
        inventory.Add(Deep("FeedbackHub", ComponentCategory.AppX, ComponentFunctionCategory.Communication,
            ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
            ComponentProtectionLevel.None, ComponentProfileTag.ConsumerContent));
        inventory.Add(Deep("BingNews", ComponentCategory.AppX, ComponentFunctionCategory.Communication,
            ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
            ComponentProtectionLevel.None, ComponentProfileTag.ConsumerContent));
        inventory.Add(Deep("BingSearch", ComponentCategory.AppX, ComponentFunctionCategory.Communication,
            ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
            ComponentProtectionLevel.None, ComponentProfileTag.ConsumerContent));
        inventory.Add(Deep("DevHome", ComponentCategory.AppX, ComponentFunctionCategory.Developer,
            ComponentRiskLevel.Moderate, ComponentRecommendationKind.ProfileDependent,
            ComponentProtectionLevel.Sensitive, ComponentProfileTag.DeveloperTool));
        inventory.Add(Deep("OneDrive", ComponentCategory.AppX, ComponentFunctionCategory.Communication,
            ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
            ComponentProtectionLevel.None, ComponentProfileTag.CloudStorage));
        inventory.Add(Deep("Teams", ComponentCategory.AppX, ComponentFunctionCategory.Productivity,
            ComponentRiskLevel.Moderate, ComponentRecommendationKind.ProfileDependent,
            ComponentProtectionLevel.Sensitive, ComponentProfileTag.None));
        inventory.Add(Deep("MediaPlayerX", ComponentCategory.AppX, ComponentFunctionCategory.Media,
            ComponentRiskLevel.Moderate, ComponentRecommendationKind.ProfileDependent,
            ComponentProtectionLevel.Sensitive, ComponentProfileTag.None));
        inventory.Add(Deep("GamingServices", ComponentCategory.AppX, ComponentFunctionCategory.Gaming,
            ComponentRiskLevel.Moderate, ComponentRecommendationKind.ProfileDependent,
            ComponentProtectionLevel.Sensitive, ComponentProfileTag.GamingRelevant));
        inventory.Add(Deep("WslStack", ComponentCategory.AppX, ComponentFunctionCategory.Virtualization,
            ComponentRiskLevel.Moderate, ComponentRecommendationKind.ProfileDependent,
            ComponentProtectionLevel.Sensitive, ComponentProfileTag.Virtualization));

        // c) Non-inventory optimization definitions (registry/privacy/personalization/service).
        var optimizations = new OptimizationCatalog().GetEntries();
        return ProfileCandidateService.BuildCandidates(inventory, optimizations);
    }

    private static ProfileInventoryInput Deep(string id, ComponentCategory category, ComponentFunctionCategory fn,
        ComponentRiskLevel risk, ComponentRecommendationKind rec, ComponentProtectionLevel prot, ComponentProfileTag tag)
        => new()
        {
            RawIdentity = id + "~~~~0.0.1.0",
            Category = category,
            Deep = GamingKnowledge.K(id, fn, risk, rec, prot, tag, ClassificationConfidence.Curated),
        };

    private ProfileDeltaReport Report(string profileId, ProfileCandidateBuildResult built,
        IReadOnlySet<GamingExtra>? extras = null)
    {
        var profile = _catalog.GetProfiles().Single(p => p.Id == profileId);
        var present = built.Subjects.Select(s => s.LogicalId).ToHashSet(StringComparer.Ordinal);
        return _service.GenerateDelta(profile, built.Subjects, extras ?? new HashSet<GamingExtra>(),
            Array.Empty<string>(), present);
    }

    // =====================================================================
    // 3. GAMING VS DEDICATED — REAL semantic difference (§3)
    // =====================================================================

    [Fact]
    public void Gaming_And_DedicatedGaming_Differ_On_Real_Like_Stream()
    {
        var built = BuildRealLikeStream();
        var gaming = Report("Gaming", built);
        var dedicated = Report("DedicatedGaming", built);

        // OneDrive: Gaming keeps convenience (optional); Dedicated AUTO-removes it.
        var gOne = gaming.Items.Single(i => i.LogicalId == "OneDrive");
        var dOne = dedicated.Items.Single(i => i.LogicalId == "OneDrive");
        Assert.Equal(ProfileDisposition.Optional, gOne.Disposition);
        Assert.Equal(ProfileDisposition.AutoApply, dOne.Disposition);
        Assert.Equal("Profile.Reason.Gaming.Dedicated.Optional.Cloud", dOne.ReasonKey);

        // Teams: Gaming leaves at default (optional); Dedicated RECOMMENDS removal.
        var gTeams = gaming.Items.Single(i => i.LogicalId == "Teams");
        var dTeams = dedicated.Items.Single(i => i.LogicalId == "Teams");
        Assert.Equal(ProfileDisposition.Optional, gTeams.Disposition);
        Assert.Equal(ProfileDisposition.Recommend, dTeams.Disposition);

        // Semantic action sets differ — exactly the meaningful dedicated actions
        // (set semantics; the fixture may carry its own DevHome family row).
        // OneDriveSync = the optimization-layer "disable cloud sync" registry
        // policy, trimmed only by Dedicated Gaming.
        var dOnly = dedicated.ChangeKeys.Except(gaming.ChangeKeys)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "AppX|DevHome|Recommend", "AppX|OneDrive|AutoApply", "AppX|Teams|Recommend", "RegistryPolicy|OneDriveSync|Recommend" },
            dOnly);
        Assert.True(dedicated.ChangeCount > gaming.ChangeCount);
        Assert.True(dedicated.AutoApply > gaming.AutoApply);
        Assert.True(dedicated.Recommended > gaming.Recommended);
    }

    // =====================================================================
    // 4. OFFICE — meaningful conservative plan (§4)
    // =====================================================================

    [Fact]
    public void Office_Produces_Meaningful_NonZero_Plan_On_Real_Like_Stream()
    {
        var built = BuildRealLikeStream();
        var office = Report("Office", built);

        // changeCount > 0 — the unified stream made Office's privacy/consumer
        // trims land on real media (was 0 in the Stage 15.2 real capture).
        Assert.True(office.ChangeCount > 0, $"Office changeCount must be > 0 (was {office.ChangeCount})");

        // Conservative: never Capability/CBS changes in the executable plan.
        Assert.DoesNotContain(office.ByOperationType, kv =>
            kv.Key is ExecutionOperationType.Capability or ExecutionOperationType.CbsPackage);

        // Keeps its productivity stack: OneDrive (override) even though present.
        var oneDrive = office.Items.Single(i => i.LogicalId == "OneDrive");
        Assert.Equal(ProfileDisposition.Keep, oneDrive.Disposition);
        Assert.Equal("Profile.Reason.Office.OneDrive", oneDrive.ReasonKey);

        // Office != Lightweight: Office keeps more (e.g. DevHome not in its keep
        // list but productivity apps are) — semantic direction preserved.
        var lightweight = Report("Lightweight", built);
        Assert.NotEmpty(lightweight.ChangeKeys.Except(office.ChangeKeys));
        Assert.NotEmpty(office.ChangeKeys.Except(lightweight.ChangeKeys));
    }

    // =====================================================================
    // 5. BALANCED — meaningful baseline, not near-noop (§5)
    // =====================================================================

    [Fact]
    public void Balanced_Produces_Meaningful_Baseline_On_Real_Like_Stream()
    {
        var built = BuildRealLikeStream();
        var balanced = Report("Balanced", built);

        Assert.True(balanced.ChangeCount > 0, $"Balanced changeCount must be > 0 (was {balanced.ChangeCount})");
        // Includes non-inventory privacy actions (unified stream), not only AppX.
        Assert.Contains(balanced.ByOperationType, kv => kv.Key == ExecutionOperationType.Privacy);
        Assert.True(balanced.ChangeCount < Report("Lightweight", built).ChangeCount,
            "Balanced stays conservative — strictly fewer changes than Lightweight");
    }

    // =====================================================================
    // 6. DEVELOPER — supported non-inventory actions (§6)
    // =====================================================================

    [Fact]
    public void Developer_Plan_Includes_Supported_Non_Inventory_Actions()
    {
        var built = BuildRealLikeStream();
        var developer = Report("Developer", built);

        Assert.True(developer.ChangeCount > 0);
        // Registry/privacy/personalization candidates are integrated — the real
        // capture previously saw only inventory-derived AppX operations.
        Assert.Contains(developer.ByOperationType, kv =>
            kv.Key is ExecutionOperationType.Privacy or ExecutionOperationType.Personalization
                or ExecutionOperationType.Service or ExecutionOperationType.RegistryPolicy);
        // Dev Home is kept for the Developer profile (override), even though present
        // (the fixture may carry its own DevHome family row — every row with this
        // logical id must stay kept, none may be a change).
        var devHomes = developer.Items.Where(i => i.LogicalId == "DevHome").ToList();
        Assert.NotEmpty(devHomes);
        Assert.All(devHomes, i => Assert.Equal(ProfileDisposition.Keep, i.Disposition));
    }

    // =====================================================================
    // 7. CANONICAL DEDUP — component vs optimization (§7)
    // =====================================================================

    [Fact]
    public void Canonical_Dedup_Component_Vs_Optimization_Single_Subject()
    {
        var inventory = new[]
        {
            new ProfileInventoryInput
            {
                RawIdentity = "PrintingStack~~~~0.0.1.0",
                Category = ComponentCategory.OptionalFeature,
                Deep = GamingKnowledge.K("PrintingStack", ComponentFunctionCategory.PrintingScanning,
                    ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
                    ComponentProtectionLevel.None, ComponentProfileTag.PrintScan, ClassificationConfidence.Curated),
            },
        };
        var optimizations = new[]
        {
            new OptimizationDefinition
            {
                Id = "PrintingStackOpt",
                Tab = OptimizationTab.WindowsComponents,
                Mechanism = OptimizationMechanism.DisableOptionalFeature,
                Action = OptimizationAction.Disable,
                Recommendation = RecommendationLevel.OptionalRemove,
                Risk = RiskLevel.Low,
                TargetIdentifier = "PrintingStack",
            },
        };

        var built = ProfileCandidateService.BuildCandidates(inventory, optimizations);
        Assert.Single(built.Subjects);
        Assert.Equal(1, built.OptimizationDuplicates);
        Assert.Equal(0, built.OptimizationCandidates);
    }

    // =====================================================================
    // 8. changeCount / byOperationType — EXECUTABLE CHANGES ONLY (§2/§8)
    // =====================================================================

    [Fact]
    public void ChangeCount_And_ByOperationType_Count_Only_Executable_Changes()
    {
        var capability = Deep("PrintDriverDownload", ComponentCategory.Capability, ComponentFunctionCategory.Communication,
            ComponentRiskLevel.Low, ComponentRecommendationKind.RecommendedRemove,
            ComponentProtectionLevel.None, ComponentProfileTag.None);
        var appx = Deep("PhoneLink", ComponentCategory.AppX, ComponentFunctionCategory.Communication,
            ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
            ComponentProtectionLevel.None, ComponentProfileTag.PhoneIntegration);
        var built = ProfileCandidateService.BuildCandidates(
            new[] { capability, appx }, Array.Empty<OptimizationDefinition>());

        var report = Report("Gaming", built);
        var cap = report.Items.Single(i => i.LogicalId == "PrintDriverDownload");
        var phone = report.Items.Single(i => i.LogicalId == "PhoneLink");

        // Known-but-unsupported Capability → BLOCKED, never a planned operation.
        Assert.Equal(ProfileDisposition.Blocked, cap.Disposition);
        Assert.Equal(ExecutionSupportMatrix.BlockReasonKey, cap.ReasonKey);
        Assert.Equal(ProfileDisposition.AutoApply, phone.Disposition);

        // changeCount = executable changes only; ByOperationType mirrors it.
        Assert.Equal(1, report.ChangeCount);
        Assert.DoesNotContain(report.ByOperationType, kv => kv.Key == ExecutionOperationType.Capability);
        Assert.Equal(1, report.ByOperationType[ExecutionOperationType.AppX]);
        Assert.Equal(report.ChangeCount, report.PlanChangesByOperationType.Values.Sum());
        Assert.Equal(report.ChangeCount, report.ChangeKeys.Count);
    }

    [Fact]
    public void Unsupported_Optional_Is_Blocked_Not_Counted()
    {
        // Stage 15.2: an unsupported "optional" suggestion must be Blocked — an
        // optional item must always be executable (ADR-095 §4/§11).
        var optional = Deep("LegacyFeatureX", ComponentCategory.Capability, ComponentFunctionCategory.Communication,
            ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
            ComponentProtectionLevel.None, ComponentProfileTag.None);
        var built = ProfileCandidateService.BuildCandidates(new[] { optional }, Array.Empty<OptimizationDefinition>());
        var report = Report("Balanced", built);
        var item = report.Items.Single(i => i.LogicalId == "LegacyFeatureX");
        Assert.Equal(ProfileDisposition.Blocked, item.Disposition);
        Assert.Equal(0, report.ChangeCount);
    }

    // =====================================================================
    // 9. EXTRAS — real-plan behavior (§10)
    // =====================================================================

    [Fact]
    public void Extras_Change_The_Real_Like_Plan()
    {
        var built = BuildRealLikeStream();
        var off = Report("Gaming", built);
        var on = Report("Gaming", built, new HashSet<GamingExtra> { GamingExtra.WslDocker });

        // WSL/virtualization stack: without the extra it is optional at most;
        // with the WSL/Docker extra it is KEPT for compatibility (extras must
        // materially change the real plan — ADR-095 §10).
        var wslOff = off.Items.Where(i => i.LogicalId == "WslStack").First();
        var wslOn = on.Items.Where(i => i.LogicalId == "WslStack").First();
        Assert.NotEqual(ProfileDisposition.Keep, wslOff.Disposition);
        Assert.Equal(ProfileDisposition.Keep, wslOn.Disposition);
        Assert.Equal("Profile.Reason.Gaming.Keep.Extra.WslDocker", wslOn.ReasonKey);
        Assert.True(off.Kept != on.Kept || off.Optional != on.Optional || off.ChangeCount != on.ChangeCount,
            "toggling an extra must change the real-like plan");
    }

    // =====================================================================
    // 10. PROFILE DIFFERENTIATION on the real-like stream (§9)
    // =====================================================================

    [Fact]
    public void Real_Like_Stream_Profiles_Differ_Semantically()
    {
        var built = BuildRealLikeStream();
        var reports = _catalog.GetProfiles()
            .Where(p => p.Kind == ProfileKind.Primary && p.Id != "Custom")
            .ToDictionary(p => p.Id, p => Report(p.Id, built));

        // Balanced != Gaming — Gaming ⊋ Balanced (extra consumer + UI trims).
        Assert.NotEmpty(reports["Gaming"].ChangeKeys.Except(reports["Balanced"].ChangeKeys));
        // Developer != Office — both directions carry real semantic differences
        // (Developer: telemetry/explorer registry actions; Office: DevHome/consumer trims).
        Assert.NotEmpty(reports["Developer"].ChangeKeys.Except(reports["Office"].ChangeKeys));
        Assert.NotEmpty(reports["Office"].ChangeKeys.Except(reports["Developer"].ChangeKeys));
        // Lightweight != Balanced — Lightweight ⊋ Balanced, strictly more changes.
        Assert.NotEmpty(reports["Lightweight"].ChangeKeys.Except(reports["Balanced"].ChangeKeys));
        Assert.True(reports["Lightweight"].ChangeCount > reports["Balanced"].ChangeCount,
            $"Lightweight changes ({reports["Lightweight"].ChangeCount}) must exceed Balanced ({reports["Balanced"].ChangeCount})");
    }

    // =====================================================================
    // 11. PREVIEW — same source of truth as the planner (§13)
    // =====================================================================

    [Fact]
    public void Profile_Preview_Counts_Match_The_Planner_Report_For_The_VM_Universe()
    {
        // The VM preview (Office) must show EXACTLY the planner's AutoApply count
        // for the same subject universe: the discovered curated AppX (defaults from
        // the curated definition, deep knowledge attached) + every optimization
        // definition — mirroring ProfileViewModel.ToPlanSubject. Catalog-only rows
        // are not present → NotApplicable → never counted.
        var preview = BuildPreview("Office");

        var curatedCatalog = new CuratedComponentCatalog().GetDefinitions();
        var testIds = new[] { "Microsoft.BingWeather_8wekyb3d8bbwe", "Microsoft.YourPhone_8wekyb3d8bbwe",
            "MicrosoftSolitaireCollection_8wekyb3d8bbwe", "Microsoft.WindowsMaps_8wekyb3d8bbwe" };
        var subjects = new List<ProfilePlanSubject>();
        foreach (var id in testIds)
        {
            var raw = RealInventoryFixture.Raw(ComponentCategory.AppX, id);
            var def = ComponentMatcher.FindMatchingDefinition(raw, curatedCatalog);
            var deep = RealInventoryFixture.Classifier.Classify(id);
            if (def is not null)
            {
                subjects.Add(new ProfilePlanSubject
                {
                    LogicalId = def.Id,
                    DisplayName = def.Id,
                    Category = ComponentCategory.AppX,
                    OperationType = ExecutionOperationType.AppX,
                    Action = OptimizationAction.Remove,
                    DefaultRecommendation = def.Recommendation,
                    Risk = def.Risk,
                    Removal = def.Removal,
                    IsPresent = true,
                    IsApplySupported = true,
                    Dependencies = def.Dependencies,
                    DeepKnowledge = deep,
                    Protection = ComponentProtectionLevel.None,
                    Confidence = ClassificationConfidence.Curated,
                    ExecutionSupported = true,
                });
            }
            else
            {
                // Unclassified present row (mirrors the VM): default values Unknown,
                // logical id = raw identity — no profile override can target it.
                subjects.Add(new ProfilePlanSubject
                {
                    LogicalId = id,
                    DisplayName = id,
                    Category = ComponentCategory.AppX,
                    OperationType = ExecutionOperationType.AppX,
                    Action = OptimizationAction.Remove,
                    DefaultRecommendation = RecommendationLevel.Unknown,
                    Risk = RiskLevel.Unknown,
                    IsPresent = true,
                    IsApplySupported = true,
                    DeepKnowledge = deep,
                    Protection = ComponentProtectionLevel.None,
                    Confidence = ClassificationConfidence.Unknown,
                    ExecutionSupported = true,
                });
            }
        }

        subjects.AddRange(new OptimizationCatalog().GetEntries().Select(ProfilePlanSubject.FromOptimization));
        var office = _catalog.GetProfiles().Single(p => p.Id == "Office");
        var present = subjects.Select(s => s.LogicalId).ToHashSet(StringComparer.Ordinal);
        var report = _service.GenerateDelta(office, subjects, new HashSet<GamingExtra>(),
            Array.Empty<string>(), present);

        var parsed = ParseCount(preview, "Automatic changes:");
        Assert.Equal(report.AutoApply, parsed);
        Assert.True(report.AutoApply > 0, "Office preview must show a non-zero automatic count on the VM universe");
    }

    private static int ParseCount(string preview, string label)
    {
        var line = preview.Split('\n').FirstOrDefault(l => l.StartsWith(label, StringComparison.Ordinal));
        Assert.NotNull(line);
        var value = line![label.Length..].Trim();
        return int.Parse(value);
    }

    private static string BuildPreview(string profileId)
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

        customize.Profiles!.Profiles.Single(p => p.Definition.Id == profileId).IsSelected = true;
        return customize.Profiles.ProfilePreviewText;
    }
}
