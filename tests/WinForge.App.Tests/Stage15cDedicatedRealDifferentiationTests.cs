using System;
using System.Collections.Generic;
using System.Linq;
using WinForge.Core.ComponentIntelligence;
using WinForge.Core.Models;
using WinForge.Core.Profiles;
using WinForge.Infrastructure.ComponentIntelligence;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Profiles;
using Xunit;

namespace WinForge.App.Tests;

// =====================================================================
// Phase 15 Stage 15.2b — REAL DEDICATED-GAMING DIFFERENTIATION FIX
// (ADR-095 addendum)
//
// The first v2 real validation fixed accounting + Office but exposed the
// remaining wiring defect: on REAL media Gaming PC and Dedicated Gaming were
// IDENTICAL (auto 19 / rec 6 / changes 25, same semanticActionKeys).
//
// Root cause (two defects):
//   1. The gaming policy was dispatched ONLY for deep-knowledge subjects —
//      curated-only inventory objects (OneDrive/Teams/…) bypassed it, so the
//      DedicatedGaming WiderMinimalSteer never ran on them.
//   2. The planner's SelectedProfiles never included the EXTRA SCENARIO
//      profiles, so their data-driven Keep overrides (Xbox services etc.)
//      were dead — Lightweight could auto-disable Xbox services even with the
//      Xbox/Game Pass extra enabled.
//
// Fixes under test:
//   - policy dispatched for curated-only subjects (synthesized knowledge view)
//   - DedicatedGaming profile intent for curated consumer/cloud items
//     (OneDrive/OneDriveSync Keep->Recommend, DevHome Optional->Recommend,
//      Clipchamp Recommend->AutoApply) — real, safe, explainable differences
//   - extras' ExtraScenario profiles joined into SelectedProfiles (extras
//     override profile minimalism for ANY primary, incl. Lightweight)
// =====================================================================

public sealed class Stage15cDedicatedRealDifferentiationTests
{
    private readonly ProfileExecutionService _service = new();
    private readonly ProfileCatalog _catalog = new();
    private readonly CuratedComponentCatalog _curated = new();

    private IReadOnlyList<ProfileDefinition> AllProfiles => _catalog.GetProfiles();

    /// <summary>
    /// The real-shaped stream: deep fixture families + CURATED-ONLY consumer/
    /// cloud AppX (mirroring the real capture's 4 curatedOutsideDeep objects:
    /// OneDrive, Teams, DevHome, Clipchamp) + optimization definitions.
    /// </summary>
    private ProfileCandidateBuildResult BuildRealCuratedStream()
    {
        var inventory = new List<ProfileInventoryInput>();

        // Deep-derived fixture families (as the real capture classifies them).
        inventory.AddRange(RealFixtureInventory());

        // Curated-only inventory objects — deep classification returns null, the
        // curated definition is matched (ComponentMatcher). On the real image
        // these are the CuratedOutsideDeep bucket. (DevHome comes through the DEEP
        // fixture family — the real 25H2 image classifies it deep — so its
        // Dedicated difference is a policy-layer one, not an override.)
        inventory.Add(Curated("Microsoft.OneDriveSync_8wekyb3d8bbwe", "OneDrive"));
        inventory.Add(Curated("Microsoft.Teams_8wekyb3d8bbwe", "Teams"));
        inventory.Add(Curated("Clipchamp.Clipchamp_8wekyb3d8bbwe", "Clipchamp"));

        var optimizations = new OptimizationCatalog().GetEntries();
        return ProfileCandidateService.BuildCandidates(inventory, optimizations);
    }

    private List<ProfileInventoryInput> RealFixtureInventory()
    {
        var classifier = RealInventoryFixture.Classifier;
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "25H2-Pro-zhCN-component-families.json");
        Assert.True(File.Exists(path), $"fixture missing at {path}");
        using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
        var inputs = new List<ProfileInventoryInput>();
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
                inputs.Add(new ProfileInventoryInput { RawIdentity = rep, Category = category, Deep = k });
            }
        }

        return inputs;
    }

    private static ProfileInventoryInput Curated(string rawIdentity, string curatedId)
        => new()
        {
            RawIdentity = rawIdentity,
            Category = ComponentCategory.AppX,
            Deep = null,
            Curated = new ComponentDefinition
            {
                Id = curatedId,
                Category = ComponentCategory.AppX,
                Recommendation = curatedId switch
                {
                    "OneDrive" => RecommendationLevel.UsuallyKeep,
                    "Teams" => RecommendationLevel.OptionalRemove,
                    "DevHome" => RecommendationLevel.UsuallyKeep,
                    _ => RecommendationLevel.RecommendedRemove,
                },
                Risk = curatedId switch
                {
                    "OneDrive" => RiskLevel.Medium,
                    _ => RiskLevel.Low,
                },
                Removal = RemovalSupport.Supported,
            },
        };

    private ProfileDeltaReport Report(string profileId, ProfileCandidateBuildResult built,
        IReadOnlySet<GamingExtra>? extras = null)
    {
        var profile = AllProfiles.Single(p => p.Id == profileId);
        var present = built.Subjects.Select(s => s.LogicalId).ToHashSet(StringComparer.Ordinal);
        return _service.GenerateDelta(profile, built.Subjects, extras ?? new HashSet<GamingExtra>(),
            Array.Empty<string>(), present, AllProfiles);
    }

    // =====================================================================
    // 1. REAL SEMANTIC DIFFERENCE — curated-only stream (§3/§7)
    // =====================================================================

    [Fact]
    public void Real_Curated_Stream_Gaming_And_DedicatedGaming_Differ_Semantically()
    {
        var built = BuildRealCuratedStream();
        var gaming = Report("Gaming", built);
        var dedicated = Report("DedicatedGaming", built);

        // Gaming PC stays convenient: OneDrive (cloud) is KEPT, DevHome is OPTIONAL
        // (policy OptionalTags), Clipchamp is RECOMMENDED at most (non-driven).
        var gOne = gaming.Items.Where(i => i.LogicalId == "OneDrive").First();
        var gDev = gaming.Items.Single(i => i.LogicalId == "DevHome");
        var gClip = gaming.Items.Where(i => i.LogicalId == "Clipchamp").First();
        Assert.Equal(ProfileDisposition.Keep, gOne.Disposition);
        Assert.Equal(ProfileDisposition.Optional, gDev.Disposition);
        Assert.Equal(ProfileDisposition.Recommend, gClip.Disposition);

        // Dedicated Gaming: OneDrive -> RECOMMEND (user confirms; Medium risk, never
        // auto), DevHome -> RECOMMEND (policy, Moderate), Clipchamp -> AutoApply
        // (Low + curated + supported). Real product semantics, not fake counts.
        var dOne = dedicated.Items.Where(i => i.LogicalId == "OneDrive").First();
        var dDev = dedicated.Items.Single(i => i.LogicalId == "DevHome");
        var dClip = dedicated.Items.Where(i => i.LogicalId == "Clipchamp").First();
        Assert.Equal(ProfileDisposition.Recommend, dOne.Disposition);
        Assert.Equal("Profile.Reason.Gaming.Dedicated.Optional.Cloud", dOne.ReasonKey);
        Assert.Equal(ProfileDisposition.Recommend, dDev.Disposition);
        Assert.Equal("Profile.Reason.Gaming.Dedicated.Optional.Developer", dDev.ReasonKey);
        Assert.Equal(ProfileDisposition.AutoApply, dClip.Disposition);

        // Semantic action sets differ (exactly the meaningful dedicated actions;
        // OneDriveSync is the optimization-layer "disable cloud sync" registry
        // policy, trimmed only by Dedicated Gaming).
        var dOnly = dedicated.ChangeKeys.Except(gaming.ChangeKeys).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "AppX|Clipchamp|AutoApply", "AppX|DevHome|Recommend", "AppX|OneDrive|Recommend", "RegistryPolicy|OneDriveSync|Recommend" },
            dOnly);
        Assert.True(dedicated.Recommended > gaming.Recommended);
        Assert.True(dedicated.ChangeCount > gaming.ChangeCount);
    }

    // =====================================================================
    // 2. NO UNSAFE DEDICATED AUTO-REMOVAL (§4/§9)
    // =====================================================================

    [Fact]
    public void DedicatedGaming_Never_Auto_Removes_Unsafe_Types()
    {
        var built = BuildRealCuratedStream();
        var dedicated = Report("DedicatedGaming", built);

        // changeCount counts only executable changes; Capability/CBS never appear.
        Assert.DoesNotContain(dedicated.ByOperationType, kv =>
            kv.Key is ExecutionOperationType.Capability or ExecutionOperationType.CbsPackage);
        // Protected / critical infrastructure stays kept or blocked.
        foreach (var item in dedicated.Items.Where(i => i.Disposition is ProfileDisposition.AutoApply))
        {
            Assert.True(ExecutionSupportMatrix.IsExecutable(item.OperationType),
                $"auto-applied '{item.LogicalId}' must be executable");
            Assert.NotEqual(ExecutionOperationType.Driver, item.OperationType);
        }
    }

    // =====================================================================
    // 3. EXTRAS OVERRIDE DEDICATED MINIMALISM (§5)
    // =====================================================================

    [Fact]
    public void DedicatedGaming_Extras_Override_Minimalism_For_Deep_Subjects()
    {
        var built = BuildRealCuratedStream();

        // GamingServices (deep, Gaming function): kept with Xbox extra.
        var withXbox = Report("DedicatedGaming", built, new HashSet<GamingExtra> { GamingExtra.XboxGamePass });
        var gs = withXbox.Items.Where(i => i.LogicalId == "GamingServices").FirstOrDefault();
        if (gs is not null)
        {
            Assert.Equal(ProfileDisposition.Keep, gs.Disposition);
            Assert.Contains("Xbox", gs.ReasonKey, StringComparison.Ordinal);
        }

        // Xbox service definitions (optimization layer): the extra profile's Keep
        // overrides reach the engine now — they are kept, not in the plan.
        foreach (var svc in new[] { "XblAuthManager", "XboxGipSvc", "XboxNetApiSvc", "GameDvr" })
        {
            var item = withXbox.Items.Where(i => i.LogicalId == svc).FirstOrDefault();
            if (item is not null)
            {
                Assert.Equal(ProfileDisposition.Keep, item.Disposition);
            }
        }
    }

    // =====================================================================
    // 4. LIGHTWEIGHT XBOX-SERVICE SAFETY (§6)
    // =====================================================================

    [Fact]
    public void Lightweight_XboxExtra_Upgrades_Xbox_Services_To_Keep()
    {
        var built = BuildRealCuratedStream();

        // Without the extra: Lightweight auto-applies the Xbox service configs.
        var without = Report("Lightweight", built);
        foreach (var svc in new[] { "XblAuthManager", "XboxGipSvc", "XboxNetApiSvc" })
        {
            var item = without.Items.Where(i => i.LogicalId == svc).First();
            Assert.Equal(ProfileDisposition.AutoApply, item.Disposition);
        }

        // With the Xbox/Game Pass extra ON: the extra profile's Keep overrides must
        // upgrade them to Keep and remove them from the executable plan.
        var with = Report("Lightweight", built, new HashSet<GamingExtra> { GamingExtra.XboxGamePass });
        foreach (var svc in new[] { "XblAuthManager", "XboxGipSvc", "XboxNetApiSvc" })
        {
            var item = with.Items.Where(i => i.LogicalId == svc).First();
            Assert.Equal(ProfileDisposition.Keep, item.Disposition);
            Assert.False(item.IsExecutableChange);
            Assert.DoesNotContain($"Service|{svc}|AutoApply", with.ChangeKeys);
            Assert.DoesNotContain($"Service|{svc}|Recommend", with.ChangeKeys);
        }
    }

    [Fact]
    public void Lightweight_Xbox_Service_AutoApply_Is_Non_Destructive_Config()
    {
        // Documented semantics (ADR-095 addendum §6): the Lightweight auto actions
        // for XblAuthManager/XboxGipSvc/XboxNetApiSvc are Service startup-type
        // configs (map to ConfigureOfflineService — restorable), NOT removal and
        // NOT deletion. They are executable only when NO Xbox/Game Pass extra is
        // selected; with the extra ON the extra profile's Keep overrides upgrade
        // them to Keep (covered by Lightweight_XboxExtra_Upgrades_Xbox_Services_To_Keep).
        var built = BuildRealCuratedStream();
        var report = Report("Lightweight", built);
        foreach (var svc in new[] { "XblAuthManager", "XboxGipSvc", "XboxNetApiSvc" })
        {
            var item = report.Items.Where(i => i.LogicalId == svc).First();
            Assert.Equal(ProfileDisposition.AutoApply, item.Disposition);
            Assert.Equal(ExecutionOperationType.Service, item.OperationType);
            // Service configuration is CONDITIONAL support — never destructive.
            Assert.Equal(ExecutionSupportStatus.Conditional, ExecutionSupportMatrix.SupportFor(item.OperationType));
        }

        // The concrete execution path is ConfigureOfflineService (startup-type),
        // and removal-capability operations stay NOT supported.
        Assert.Equal(ExecutionSupportStatus.Conditional,
            ExecutionSupportMatrix.SupportFor(CustomizationOperationType.ConfigureOfflineService));
        Assert.False(ExecutionSupportMatrix.IsExecutable(CustomizationOperationType.RemovePackage));
        Assert.False(ExecutionSupportMatrix.IsExecutable(CustomizationOperationType.RemoveCapability));
    }

    // =====================================================================
    // 5. GAMING PC REMAINS CONVENIENT (§3) — not made aggressive
    // =====================================================================

    [Fact]
    public void GamingPc_Stays_Convenient_On_The_Curated_Stream()
    {
        var built = BuildRealCuratedStream();
        var gaming = Report("Gaming", built);

        // Gaming PC does not trim the curated cloud/productivity conveniences.
        Assert.Equal(ProfileDisposition.Keep,
            gaming.Items.Where(i => i.LogicalId == "OneDrive").First().Disposition);
        Assert.NotEqual(ProfileDisposition.AutoApply,
            gaming.Items.Single(i => i.LogicalId == "DevHome").Disposition);
        Assert.NotEqual(ProfileDisposition.AutoApply,
            gaming.Items.Where(i => i.LogicalId == "Teams").First().Disposition);

        // And it keeps its health/compatibility foundations.
        Assert.Contains(gaming.ChangeKeys, k => k.StartsWith("AppX|", StringComparison.Ordinal));
        Assert.DoesNotContain(gaming.ChangeKeys, k =>
            k.Contains("|Capability|", StringComparison.Ordinal) || k.Contains("|CbsPackage|", StringComparison.Ordinal));
    }

    // =====================================================================
    // 6. POLICY DISPATCH FOR CURATED SUBJECTS — the wiring fix (§1)
    // =====================================================================

    [Fact]
    public void Curated_Only_Subjects_Enter_The_Gaming_Policy_Path()
    {
        // OneDrive is curated-only (no deep entry): the synthesized knowledge view
        // (UsuallyKeep -> RecommendedKeep) makes Gaming PC KEEP it via the POLICY
        // reason — proving the curated subject now flows through the policy, not
        // around it.
        var built = BuildRealCuratedStream();
        var gaming = Report("Gaming", built);
        var oneDrive = gaming.Items.Where(i => i.LogicalId == "OneDrive").First();
        Assert.Equal(ProfileDisposition.Keep, oneDrive.Disposition);
        Assert.Equal("Profile.Reason.Gaming.Keep.Runtime", oneDrive.ReasonKey);
    }
}
