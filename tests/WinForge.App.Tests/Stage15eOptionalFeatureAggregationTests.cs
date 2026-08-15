using System;
using System.Collections.Generic;
using System.Linq;
using WinForge.Core.ComponentIntelligence;
using WinForge.Core.Models;
using WinForge.Core.Profiles;
using WinForge.Infrastructure.Profiles;
using Xunit;

namespace WinForge.App.Tests;

// =====================================================================
// Phase 15 Stage 15.3b — OPTIONAL FEATURE CANONICAL AGGREGATION (ADR-096 addendum)
//
// Real structural validation exposed "duplicate change plans" that were NOT
// true duplicates:
//   DedicatedGaming: 'Containers' (4)   — Containers, Containers-HNS,
//     Containers-SDN, Containers-Server-For-Application-Guard (4 real features)
//   Lightweight:     'HyperV' (9)       — 9 real DISM features sharing the
//     profile-facing family id "HyperV"
//   DedicatedMinimal:'MediaPlayer' (2)  — Microsoft.ZuneMusic AppX +
//     WindowsMediaPlayer OptionalFeature (Capability + 7 CBS are NotSupported)
//
// The deep catalog maps MULTIPLE distinct Windows OptionalFeature names to ONE
// profile-facing family id. The plan validator grouped change entries by the
// SEMANTIC family id and falsely rejected distinct real features. Stage 15.3b:
//   - ProfileExecutionItem.ExecutableIdentity = the actual DISM FeatureName
//   - ProfilePlanAggregator merges TRUE same-executable candidates (provenance
//     retained), applies keep-wins precedence, fails on conflicting states
//   - distinct real features stay distinct executable operations
// =====================================================================

public sealed class Stage15eOptionalFeatureAggregationTests
{
    private readonly ProfileExecutionService _service = new();
    private readonly ProfileCatalog _catalog = new();

    private IReadOnlyList<ProfileDefinition> AllProfiles => _catalog.GetProfiles();

    // ---- knowledge factories mirroring DeepComponentCatalogData entries ----

    private static DeepComponentKnowledge Knowledge(
        string canonicalId, ComponentFunctionCategory fn,
        ComponentRiskLevel risk = ComponentRiskLevel.Moderate,
        ComponentRecommendationKind rec = ComponentRecommendationKind.ProfileDependent,
        ComponentProfileTag tag = ComponentProfileTag.None,
        ComponentProtectionLevel protection = ComponentProtectionLevel.Sensitive,
        ClassificationConfidence confidence = ClassificationConfidence.KnownPattern)
        => new()
        {
            CanonicalId = canonicalId,
            DisplayNameFallback = canonicalId,
            Function = fn,
            Risk = risk,
            Recommendation = rec,
            Protection = protection,
            ProfileTag = tag,
            Confidence = confidence,
            DependencyTags = Array.Empty<string>(),
        };

    /// <summary>The real 25H2 Hyper-V family: 9 distinct DISM feature names → one family id.</summary>
    private static readonly string[] HyperVRaws =
    {
        "HyperV-Guest-KernelInt",
        "HyperV-KernelInt-VirtualDevice",
        "Microsoft-Hyper-V",
        "Microsoft-Hyper-V-All",
        "Microsoft-Hyper-V-Hypervisor",
        "Microsoft-Hyper-V-Management-Clients",
        "Microsoft-Hyper-V-Management-PowerShell",
        "Microsoft-Hyper-V-Services",
        "Microsoft-Hyper-V-Tools-All",
    };

    /// <summary>The real 25H2 Containers family: 4 distinct DISM feature names → one family id.</summary>
    private static readonly string[] ContainersRaws =
    {
        "Containers",
        "Containers-HNS",
        "Containers-SDN",
        "Containers-Server-For-Application-Guard",
    };

    /// <summary>
    /// The real multi-member virtualization stream (HyperV x9 + distinct
    /// VirtualMachinePlatform / HypervisorPlatform / WSL / Sandbox) plus the
    /// MediaPlayer family members — mirrors the real 25H2 capture.
    /// </summary>
    private List<ProfilePlanSubject> BuildVirtualizationStream()
    {
        var subjects = new List<ProfilePlanSubject>();
        foreach (var raw in HyperVRaws)
        {
            subjects.Add(ProfilePlanSubject.FromKnowledge(raw, ComponentCategory.OptionalFeature,
                Knowledge("HyperV", ComponentFunctionCategory.Virtualization)));
        }

        subjects.Add(ProfilePlanSubject.FromKnowledge("VirtualMachinePlatform", ComponentCategory.OptionalFeature,
            Knowledge("VirtualMachinePlatform", ComponentFunctionCategory.Virtualization)));
        subjects.Add(ProfilePlanSubject.FromKnowledge("HypervisorPlatform", ComponentCategory.OptionalFeature,
            Knowledge("HypervisorPlatform", ComponentFunctionCategory.Virtualization)));
        // WSL enters the real stream as a CURATED object (def id "Wsl") — that is
        // the id Lightweight's profile intent targets (mirrors the real capture).
        subjects.Add(ProfilePlanSubject.FromCurated("Microsoft-Windows-Subsystem-Linux",
            new ComponentDefinition
            {
                Id = "Wsl",
                Category = ComponentCategory.OptionalFeature,
                Recommendation = RecommendationLevel.OptionalRemove,
                Risk = RiskLevel.Medium,
                Removal = RemovalSupport.Supported,
            },
            ComponentCategory.OptionalFeature));
        subjects.Add(ProfilePlanSubject.FromKnowledge("Containers-DisposableClientVM", ComponentCategory.OptionalFeature,
            Knowledge("Sandbox", ComponentFunctionCategory.Virtualization,
                confidence: ClassificationConfidence.Curated)));
        return subjects;
    }

    private List<ProfilePlanSubject> BuildContainersStream()
    {
        return ContainersRaws
            .Select(raw => ProfilePlanSubject.FromKnowledge(raw, ComponentCategory.OptionalFeature,
                Knowledge("Containers", ComponentFunctionCategory.Virtualization,
                    tag: ComponentProfileTag.DeveloperTool)))
            .ToList();
    }

    private List<ProfilePlanSubject> BuildMediaPlayerStream()
    {
        return new List<ProfilePlanSubject>
        {
            ProfilePlanSubject.FromKnowledge("Microsoft.ZuneMusic_11.2510.7.0_neutral_~_8wekyb3d8bbwe",
                ComponentCategory.AppX,
                Knowledge("MediaPlayer", ComponentFunctionCategory.Media,
                    ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
                    ComponentProfileTag.MediaPlayback, ComponentProtectionLevel.None)),
            ProfilePlanSubject.FromKnowledge("WindowsMediaPlayer", ComponentCategory.OptionalFeature,
                Knowledge("MediaPlayer", ComponentFunctionCategory.Media,
                    ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
                    ComponentProfileTag.MediaPlayback, ComponentProtectionLevel.None,
                    ClassificationConfidence.Curated)),
            // NotSupported providers — must stay blocked, never in the plan.
            ProfilePlanSubject.FromKnowledge("Media.WindowsMediaPlayer~~~~0.0.12.0", ComponentCategory.Capability,
                Knowledge("MediaPlayer", ComponentFunctionCategory.Media,
                    ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
                    ComponentProfileTag.MediaPlayback, ComponentProtectionLevel.None)),
            ProfilePlanSubject.FromKnowledge("Microsoft-Windows-MediaPlayer-Package~31bf3856ad364e35~amd64~~10.0.26100.1742",
                ComponentCategory.CbsPackage,
                Knowledge("MediaPlayer", ComponentFunctionCategory.Media,
                    ComponentRiskLevel.Moderate, ComponentRecommendationKind.OptionalRemove,
                    ComponentProfileTag.MediaPlayback, ComponentProtectionLevel.Sensitive)),
        };
    }

    private (CustomizationPlan? Plan, IReadOnlyList<string> Issues) Build(
        string profileId, IReadOnlyList<ProfilePlanSubject> subjects,
        IReadOnlySet<GamingExtra>? extras = null, IReadOnlyCollection<string>? overrides = null)
    {
        var profile = AllProfiles.Single(p => p.Id == profileId);
        var present = subjects.Select(s => s.LogicalId).ToHashSet(StringComparer.Ordinal);
        return _service.BuildPlan(profile, subjects, extras ?? new HashSet<GamingExtra>(),
            overrides ?? Array.Empty<string>(), present, AllProfiles);
    }

    private static ProfileExecutionItem Item(
        string logicalId, string executableIdentity, ProfileDisposition disposition,
        OptimizationAction action = OptimizationAction.Remove, string? sourceId = null)
        => new()
        {
            LogicalId = logicalId,
            DisplayName = logicalId,
            OperationType = ExecutionOperationType.OptionalFeature,
            Disposition = disposition,
            ExecutableIdentity = executableIdentity,
            ActionKind = action,
            SourceDefinitionIds = new[] { sourceId ?? executableIdentity },
        };

    // =====================================================================
    // 1. CONTAINERS — multiple semantic candidates → distinct valid feature ops
    // =====================================================================

    [Fact]
    public void Containers_Four_Candidates_Produce_Four_Distinct_Valid_Feature_Operations()
    {
        var (plan, issues) = Build("DedicatedGaming", BuildContainersStream());
        Assert.NotNull(plan);
        Assert.Empty(issues);

        var featKeys = plan!.Operations
            .Where(o => o.OperationType == CustomizationOperationType.DisableOptionalFeature)
            .Select(o => o.ConflictKey)
            .ToList();
        Assert.Equal(ContainersRaws.Length, featKeys.Count);
        foreach (var raw in ContainersRaws)
        {
            Assert.Contains($"feat|{raw}", featKeys);
        }

        Assert.Equal(featKeys.Count, featKeys.Distinct(StringComparer.Ordinal).Count());
    }

    // =====================================================================
    // 2. HYPER-V — 9 candidates → correct canonical aggregation (9 distinct ops)
    // =====================================================================

    [Fact]
    public void HyperV_Nine_Candidates_Produce_Nine_Distinct_Valid_Feature_Operations()
    {
        var (plan, issues) = Build("Lightweight", BuildVirtualizationStream());
        Assert.NotNull(plan);
        Assert.Empty(issues);

        var featKeys = plan!.Operations
            .Where(o => o.OperationType == CustomizationOperationType.DisableOptionalFeature)
            .Select(o => o.ConflictKey)
            .ToList();
        foreach (var raw in HyperVRaws)
        {
            Assert.Contains($"feat|{raw}", featKeys);
        }

        Assert.Equal(featKeys.Count, featKeys.Distinct(StringComparer.Ordinal).Count());
        // Hyper-V family is NOT collapsed into one "HyperV" op.
        Assert.DoesNotContain(featKeys, k => k == "feat|HyperV");
    }

    // =====================================================================
    // 3. MEDIA PLAYER — AppX + OptionalFeature distinct; Capability/CBS blocked
    // =====================================================================

    [Fact]
    public void MediaPlayer_AppX_And_Feature_Are_Distinct_Executable_Ops_Unsupported_Blocked()
    {
        var (plan, issues) = Build("DedicatedMinimal", BuildMediaPlayerStream());
        Assert.NotNull(plan);
        Assert.Empty(issues);

        var ops = plan!.Operations;
        Assert.Contains(ops, o => o.ConflictKey == "feat|WindowsMediaPlayer");
        // AppX removals use the pkg| conflict-key convention (as in the real
        // profile-buildplans.json canonicalOperationKeys).
        Assert.Contains(ops, o => o.ConflictKey == "pkg|Microsoft.ZuneMusic_11.2510.7.0_neutral_~_8wekyb3d8bbwe");
        // Capability + CbsPackage providers are NotSupported — never in the plan.
        Assert.DoesNotContain(ops, o => o.ConflictKey.Contains("Media.WindowsMediaPlayer", StringComparison.Ordinal));
        Assert.DoesNotContain(ops, o => o.ConflictKey.Contains("Microsoft-Windows-MediaPlayer-Package", StringComparison.Ordinal));
    }

    // =====================================================================
    // 4. DISTINCT virtualization FeatureNames remain distinct (never collapsed)
    // =====================================================================

    [Fact]
    public void Distinct_Virtualization_FeatureNames_Remain_Distinct()
    {
        var (plan, issues) = Build("Lightweight", BuildVirtualizationStream());
        Assert.NotNull(plan);
        Assert.Empty(issues);

        var featKeys = plan!.Operations
            .Where(o => o.OperationType == CustomizationOperationType.DisableOptionalFeature)
            .Select(o => o.ConflictKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // The whole virtualization ecosystem is NOT one feature: HyperV x9,
        // VirtualMachinePlatform, HypervisorPlatform and WSL each keep their own
        // executable identity (never collapsed into one "HyperV" operation).
        foreach (var raw in HyperVRaws)
        {
            Assert.Contains($"feat|{raw}", featKeys);
        }

        Assert.Contains("feat|VirtualMachinePlatform", featKeys);
        Assert.Contains("feat|HypervisorPlatform", featKeys);
        Assert.Contains("feat|Microsoft-Windows-Subsystem-Linux", featKeys);
        Assert.DoesNotContain(featKeys, k => k == "feat|HyperV");
    }

    // =====================================================================
    // 5. PROVENANCE — N candidates → one executable op, no information loss
    // =====================================================================

    [Fact]
    public void Same_Executable_Target_Merges_With_Provenance_Retained()
    {
        var items = new[]
        {
            Item("MediaPlayer", "WindowsMediaPlayer", ProfileDisposition.AutoApply,
                OptimizationAction.Remove, "MediaPlayer"),
            Item("WindowsMediaPlayer", "WindowsMediaPlayer", ProfileDisposition.Recommend,
                OptimizationAction.Remove, "WindowsMediaPlayer"),
        };

        var result = ProfilePlanAggregator.Aggregate(items);
        Assert.True(result.IsValid);
        Assert.Equal(1, result.MergedDuplicateCount);
        var merged = Assert.Single(result.Items);
        Assert.Single(result.MergeGroups);

        // AutoApply > Recommend (deterministic superset).
        Assert.Equal(ProfileDisposition.AutoApply, merged.Disposition);
        Assert.Equal(2, merged.MergedSourceCount);
        // Both sources survive the merge (registry-SourceDefinitionIds behavior).
        Assert.Contains("MediaPlayer", merged.SourceDefinitionIds);
        Assert.Contains("WindowsMediaPlayer", merged.SourceDefinitionIds);
        Assert.Equal("OptionalFeature|WindowsMediaPlayer", result.MergeGroups[0].CanonicalKey);
        Assert.Equal(2, result.MergeGroups[0].SourceCount);
    }

    // =====================================================================
    // 6. KEEP vs REMOVE — Keep wins (required keep / protected / override > removal)
    // =====================================================================

    [Fact]
    public void Keep_Wins_Over_Removal_At_Semantic_Level()
    {
        var items = new[]
        {
            Item("HyperV", "Microsoft-Hyper-V", ProfileDisposition.Keep),
            Item("HyperV", "HyperV-Guest-KernelInt", ProfileDisposition.AutoApply),
            Item("HyperV", "HyperV-KernelInt-VirtualDevice", ProfileDisposition.Recommend),
        };

        var result = ProfilePlanAggregator.Aggregate(items);
        Assert.True(result.IsValid);
        Assert.Equal(2, result.DroppedKeepWins);
        var kept = Assert.Single(result.Items);
        Assert.Equal(ProfileDisposition.Keep, kept.Disposition);
    }

    // =====================================================================
    // 7. MANUAL OVERRIDE — survives aggregation (authoritative, excluded from plan)
    // =====================================================================

    [Fact]
    public void Manual_Override_Survives_Aggregation()
    {
        // Lightweight trims the Hyper-V family; the user override keeps it out of
        // the executable plan while the rest of the profile still executes.
        var (plan, issues) = Build("Lightweight", BuildVirtualizationStream(), overrides: new[] { "HyperV" });
        Assert.NotNull(plan);
        Assert.Empty(issues);
        // The whole overridden family stays out of the executable plan…
        Assert.DoesNotContain(plan!.Operations,
            o => o.OperationType == CustomizationOperationType.DisableOptionalFeature
                && o.TargetIdentifier is not null
                && HyperVRaws.Contains(o.TargetIdentifier, StringComparer.Ordinal));
        // …while the rest of the profile still executes (virtualization-family
        // changes that were NOT overridden remain in the plan).
        Assert.True(plan.Operations.Count > 0);
        Assert.Contains(plan.Operations, o => o.ConflictKey == "feat|VirtualMachinePlatform");
    }

    // =====================================================================
    // 8. EXTRAS — survive aggregation (family-level keep protects the ecosystem)
    // =====================================================================

    [Fact]
    public void Wsl_Extra_Keeps_Virtualization_Family_While_HyperV_Members_Remain_Distinct()
    {
        var (plan, issues) = Build("Lightweight", BuildVirtualizationStream(),
            new HashSet<GamingExtra> { GamingExtra.WslDocker });
        Assert.NotNull(plan);
        Assert.Empty(issues);

        // WSL/Docker extra keeps the VM-platform family out of the plan…
        Assert.DoesNotContain(plan!.Operations, o => o.ConflictKey == "feat|VirtualMachinePlatform");
        // …while the remaining Hyper-V members still collapse to distinct ops.
        var featKeys = plan.Operations
            .Where(o => o.OperationType == CustomizationOperationType.DisableOptionalFeature)
            .Select(o => o.ConflictKey)
            .ToList();
        Assert.Contains("feat|HyperV-Guest-KernelInt", featKeys);
        Assert.Contains("feat|Microsoft-Hyper-V-Services", featKeys);
        Assert.Equal(featKeys.Count, featKeys.Distinct(StringComparer.Ordinal).Count());
    }

    // =====================================================================
    // 9. NO duplicate final OptionalFeature canonical keys — six profiles
    // =====================================================================

    [Fact]
    public void No_Duplicate_Final_OptionalFeature_Canonical_Keys_Six_Profiles()
    {
        var subjects = BuildVirtualizationStream();
        subjects.AddRange(BuildContainersStream());
        subjects.AddRange(BuildMediaPlayerStream());

        foreach (var profileId in new[] { "Balanced", "Gaming", "DedicatedGaming", "Developer", "Office", "Lightweight" })
        {
            var (plan, issues) = Build(profileId, subjects);
            Assert.True(plan is not null, $"{profileId} BuildPlan must be non-null. Issues: {string.Join("; ", issues)}");
            Assert.Empty(issues);
            var keys = plan!.Operations.Select(o => o.ConflictKey).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
            Assert.All(plan.Operations, o => Assert.False(o.ConflictKey is "feat|" or "svc|" or "reg|||" or "pkg|"));
        }
    }

    // =====================================================================
    // 10. CONFLICTING executable states — explicit failure, never silent merge
    // =====================================================================

    [Fact]
    public void Conflicting_Executable_States_Fail_With_Explicit_Conflict()
    {
        var items = new[]
        {
            Item("Containers", "Containers-HNS", ProfileDisposition.AutoApply, OptimizationAction.Remove),
            Item("Containers", "Containers-HNS", ProfileDisposition.AutoApply, OptimizationAction.Disable),
        };

        var result = ProfilePlanAggregator.Aggregate(items);
        Assert.False(result.IsValid);
        var issue = Assert.Single(result.Issues);
        Assert.Contains("Conflicting executable intents", issue);
        Assert.Contains("OptionalFeature|Containers-HNS", issue);
        // No silent merge: a deterministic representative stays, the conflict is
        // surfaced and fails validation.
        Assert.Single(result.Items);
        Assert.Equal(0, result.MergedDuplicateCount);
    }

    [Fact]
    public void Conflicting_Executable_States_Fail_Through_BuildPlan()
    {
        var profile = AllProfiles.Single(p => p.Id == "DedicatedMinimal");
        var subjects = new List<ProfilePlanSubject>
        {
            ProfilePlanSubject.FromKnowledge("WindowsMediaPlayer", ComponentCategory.OptionalFeature,
                Knowledge("MediaPlayer", ComponentFunctionCategory.Media,
                    ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
                    ComponentProfileTag.MediaPlayback, ComponentProtectionLevel.None)),
            // Same executable target from a second semantic candidate.
            ProfilePlanSubject.FromKnowledge("WindowsMediaPlayer", ComponentCategory.OptionalFeature,
                Knowledge("MediaPlayer", ComponentFunctionCategory.Media,
                    ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
                    ComponentProfileTag.MediaPlayback, ComponentProtectionLevel.None,
                    ClassificationConfidence.Curated)),
        };
        // Force the second candidate into a Disable action so the aggregator sees
        // Remove vs Disable for the same executable target (direct subject
        // construction cannot set Action differently per member).
        var report = _service.GenerateDelta(profile, subjects, new HashSet<GamingExtra>(),
            new HashSet<string>(), subjects.Select(s => s.LogicalId).ToHashSet(StringComparer.Ordinal), AllProfiles);
        var conflictItems = report.Items
            .Where(i => i.IsExecutableChange)
            .Select((i, n) => n == 0 ? i : new ProfileExecutionItem
            {
                LogicalId = i.LogicalId, DisplayName = i.DisplayName, OperationType = i.OperationType,
                Disposition = i.Disposition, ReasonKey = i.ReasonKey, ProfileId = i.ProfileId,
                IsPresent = i.IsPresent, IsUserOverride = i.IsUserOverride, WasProfileDriven = i.WasProfileDriven,
                ExecutableIdentity = i.ExecutableIdentity, ActionKind = OptimizationAction.Disable,
                SourceDefinitionIds = i.SourceDefinitionIds, MergedSourceCount = i.MergedSourceCount,
            })
            .ToList();
        Assert.NotEmpty(conflictItems);
        var result = ProfilePlanAggregator.Aggregate(conflictItems);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains("Conflicting executable intents", StringComparison.Ordinal));
    }
}
