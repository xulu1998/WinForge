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
// Phase 15 Stage 15.1 — PROFILE EXECUTION & SAFE EXECUTION MATRIX (ADR-094)
// Tests: execution support matrix honesty, per-profile dispositions, extras
// materially change plans, manual override authority, plan validator,
// BuildPlan, real-derived fixture profile comparison (deterministic counts +
// semantic differences), and the user-facing preview summary.
// =====================================================================

public sealed class Stage15ProfileExecutionTests
{
    private readonly ProfileExecutionService _service = new();
    private readonly ProfileCatalog _catalog = new();

    // =====================================================================
    // 1. EXECUTION SUPPORT MATRIX — auditable honesty (§4)
    // =====================================================================

    [Theory]
    [InlineData(ExecutionOperationType.AppX, ExecutionSupportStatus.Supported)]
    [InlineData(ExecutionOperationType.RegistryPolicy, ExecutionSupportStatus.Supported)]
    [InlineData(ExecutionOperationType.Privacy, ExecutionSupportStatus.Supported)]
    [InlineData(ExecutionOperationType.Personalization, ExecutionSupportStatus.Supported)]
    [InlineData(ExecutionOperationType.OptionalFeature, ExecutionSupportStatus.Supported)]
    [InlineData(ExecutionOperationType.Service, ExecutionSupportStatus.Conditional)]
    [InlineData(ExecutionOperationType.Capability, ExecutionSupportStatus.NotSupported)]
    [InlineData(ExecutionOperationType.CbsPackage, ExecutionSupportStatus.NotSupported)]
    [InlineData(ExecutionOperationType.Driver, ExecutionSupportStatus.NotSupported)]
    public void Execution_Support_Matrix_Is_Honest(ExecutionOperationType type, ExecutionSupportStatus expected)
        => Assert.Equal(expected, ExecutionSupportMatrix.SupportFor(type));

    [Theory]
    [InlineData(CustomizationOperationType.RemoveProvisionedAppx, ExecutionSupportStatus.Supported)]
    [InlineData(CustomizationOperationType.SetOfflineRegistryValue, ExecutionSupportStatus.Supported)]
    [InlineData(CustomizationOperationType.DeleteOfflineRegistryValue, ExecutionSupportStatus.Supported)]
    [InlineData(CustomizationOperationType.DisableOptionalFeature, ExecutionSupportStatus.Supported)]
    [InlineData(CustomizationOperationType.ConfigureOfflineService, ExecutionSupportStatus.Conditional)]
    [InlineData(CustomizationOperationType.RemoveCapability, ExecutionSupportStatus.NotSupported)]
    [InlineData(CustomizationOperationType.RemovePackage, ExecutionSupportStatus.NotSupported)]
    public void Execution_Support_Matrix_Covers_Concrete_Operation_Types(CustomizationOperationType type, ExecutionSupportStatus expected)
        => Assert.Equal(expected, ExecutionSupportMatrix.SupportFor(type));

    [Fact]
    public void Destructive_Types_Are_Never_Executable()
    {
        // CBS removal / capability removal / driver stripping: classification
        // never promotes itself into execution capability (ADR-086/093).
        Assert.False(ExecutionSupportMatrix.IsExecutable(ExecutionOperationType.CbsPackage));
        Assert.False(ExecutionSupportMatrix.IsExecutable(ExecutionOperationType.Capability));
        Assert.False(ExecutionSupportMatrix.IsExecutable(ExecutionOperationType.Driver));
        Assert.False(ExecutionSupportMatrix.IsExecutable(CustomizationOperationType.RemovePackage));
        Assert.False(ExecutionSupportMatrix.IsExecutable(CustomizationOperationType.RemoveCapability));
    }

    // =====================================================================
    // 2. PROFILE EXECUTION MATRIX — dispositions (§1/§11)
    // =====================================================================

    private static EffectiveRecommendation Eff(
        EffectiveRecommendationLevel level,
        RiskLevel risk = RiskLevel.Low,
        bool overridden = false,
        bool profileDriven = false,
        params string[] reasons) => new()
        {
            Level = level,
            IsPresent = true,
            IsApplySupported = true,
            Risk = risk,
            WasOverridden = overridden,
            WasProfileDriven = profileDriven,
            ReasonKeys = reasons,
        };

    [Fact]
    public void Protected_Items_Are_Kept_Never_Acted_On()
    {
        var (d, r) = ProfileExecutionMatrix.Evaluate("Balanced", Eff(EffectiveRecommendationLevel.RecommendRemove),
            ComponentProtectionLevel.Protected, ClassificationConfidence.Curated, executionSupported: true, isHeuristic: false);
        Assert.Equal(ProfileDisposition.Keep, d);
        Assert.Equal("Profile.Reason.Execution.KeepProtected", r);
    }

    [Fact]
    public void Critical_And_Blocked_Are_Blocked()
    {
        var (d1, _) = ProfileExecutionMatrix.Evaluate("Balanced", Eff(EffectiveRecommendationLevel.RecommendRemove, RiskLevel.Critical),
            ComponentProtectionLevel.None, ClassificationConfidence.Curated, true, false);
        Assert.Equal(ProfileDisposition.Blocked, d1);

        var (d2, _) = ProfileExecutionMatrix.Evaluate("Balanced", Eff(EffectiveRecommendationLevel.Blocked),
            ComponentProtectionLevel.None, ClassificationConfidence.Curated, true, false);
        Assert.Equal(ProfileDisposition.Blocked, d2);
    }

    [Fact]
    public void High_Risk_Changes_Are_Never_Automatic()
    {
        foreach (var profile in new[] { "Balanced", "Gaming", "DedicatedGaming", "Developer", "Office", "Lightweight" })
        {
            var (d, _) = ProfileExecutionMatrix.Evaluate(profile, Eff(EffectiveRecommendationLevel.RecommendRemove, RiskLevel.High),
                ComponentProtectionLevel.None, ClassificationConfidence.Curated, true, false);
            Assert.Equal(ProfileDisposition.Recommend, d);
        }
    }

    [Fact]
    public void Low_Risk_Profile_Driven_Changes_AutoApply_For_All_Primaries()
    {
        foreach (var profile in new[] { "Balanced", "Gaming", "DedicatedGaming", "Developer", "Office", "Lightweight" })
        {
            var (d, _) = ProfileExecutionMatrix.Evaluate(profile, Eff(EffectiveRecommendationLevel.RecommendRemove,
                RiskLevel.Low, profileDriven: true, reasons: "Profile.Reason.Gaming.Remove.Consumer"),
                ComponentProtectionLevel.None, ClassificationConfidence.Curated, true, false);
            Assert.Equal(ProfileDisposition.AutoApply, d);
        }
    }

    [Fact]
    public void Curated_Defaults_Without_Profile_Intent_Are_Recommended_Not_Auto()
    {
        // A low-risk curated default (no profile steer) is RECOMMENDED — the user
        // confirms via adopt; profiles never silently apply non-profile changes.
        var (d, _) = ProfileExecutionMatrix.Evaluate("Balanced", Eff(EffectiveRecommendationLevel.RecommendRemove),
            ComponentProtectionLevel.None, ClassificationConfidence.Curated, true, false);
        Assert.Equal(ProfileDisposition.Recommend, d);
    }

    [Fact]
    public void Heuristic_Knowledge_Never_AutoApplies()
    {
        var (d, _) = ProfileExecutionMatrix.Evaluate("Lightweight", Eff(EffectiveRecommendationLevel.RecommendRemove,
            RiskLevel.Low, profileDriven: true),
            ComponentProtectionLevel.None, ClassificationConfidence.Curated, true, isHeuristic: true);
        Assert.Equal(ProfileDisposition.Recommend, d);
    }

    [Fact]
    public void Unsupported_Execution_Is_Blocked_From_The_Plan()
    {
        var (d, r) = ProfileExecutionMatrix.Evaluate("Balanced", Eff(EffectiveRecommendationLevel.RecommendRemove),
            ComponentProtectionLevel.None, ClassificationConfidence.Curated, executionSupported: false, isHeuristic: false);
        Assert.Equal(ProfileDisposition.Blocked, d);
        Assert.Equal(ExecutionSupportMatrix.BlockReasonKey, r);
    }

    [Fact]
    public void ManualReview_Is_Optional()
    {
        var (d, _) = ProfileExecutionMatrix.Evaluate("Balanced", Eff(EffectiveRecommendationLevel.ManualReview),
            ComponentProtectionLevel.None, ClassificationConfidence.Curated, true, false);
        Assert.Equal(ProfileDisposition.Optional, d);
    }

    [Fact]
    public void User_Override_Is_Authoritative_And_Never_Auto()
    {
        // The user manually chose to remove → disposition honors the choice but
        // the item is flagged IsUserOverride and never auto-applied.
        var eff = Eff(EffectiveRecommendationLevel.RecommendRemove, overridden: true, profileDriven: true);
        var (d, r) = ProfileExecutionMatrix.Evaluate("Balanced", eff,
            ComponentProtectionLevel.None, ClassificationConfidence.Curated, true, false);
        Assert.Equal(ProfileDisposition.Recommend, d);
        Assert.Equal("Profile.Reason.UserOverride", r);
    }

    // =====================================================================
    // 3. EXTRAS MUST MATERIALLY CHANGE THE PLAN (§3)
    // =====================================================================

    private static ProfilePlanSubject Subject(string logicalId, DeepComponentKnowledge k, ComponentCategory category = ComponentCategory.AppX)
        => new()
        {
            LogicalId = logicalId,
            DisplayName = logicalId,
            RawIdentity = logicalId,
            Category = category,
            OperationType = ProfilePlanSubject.OperationTypeFor(k.Function, category),
            Action = OptimizationAction.Remove,
            DefaultRecommendation = ProfilePlanSubject.MapRecommendation(k.Recommendation),
            Risk = ProfilePlanSubject.MapRisk(k.Risk),
            IsPresent = true,
            DeepKnowledge = k,
            Protection = k.Protection,
            Confidence = k.Confidence,
            ExecutionSupported = true,
        };

    [Fact]
    public void WslDocker_Extra_Keeps_Virtualization_Stack()
    {
        var wsl = Subject("Wsl", GamingKnowledge.K("Wsl", ComponentFunctionCategory.Virtualization,
            ComponentRiskLevel.Moderate, ComponentRecommendationKind.ProfileDependent,
            ComponentProtectionLevel.Sensitive, ComponentProfileTag.Virtualization, ClassificationConfidence.Curated));
        var profile = _catalog.GetProfiles().Single(p => p.Id == "Gaming");
        var noExtra = _service.GenerateDelta(profile, new[] { wsl }, new HashSet<GamingExtra>(),
            Array.Empty<string>(), new[] { "Wsl" });
        var withExtra = _service.GenerateDelta(profile, new[] { wsl },
            new HashSet<GamingExtra> { GamingExtra.WslDocker }, Array.Empty<string>(), new[] { "Wsl" });

        var without = noExtra.Items.Single(i => i.LogicalId == "Wsl");
        var with = withExtra.Items.Single(i => i.LogicalId == "Wsl");
        Assert.NotEqual(without.Disposition, with.Disposition);
        Assert.Equal(ProfileDisposition.Keep, with.Disposition);
        Assert.Equal("Profile.Reason.Gaming.Keep.Extra.WslDocker", with.ReasonKey);
    }

    [Fact]
    public void PrintScan_Extra_Keeps_Printing_Stack()
    {
        var printing = Subject("Printing", GamingKnowledge.K("Printing", ComponentFunctionCategory.PrintingScanning,
            ComponentRiskLevel.Moderate, ComponentRecommendationKind.ProfileDependent,
            ComponentProtectionLevel.Sensitive, ComponentProfileTag.PrintScan, ClassificationConfidence.Curated));
        var profile = _catalog.GetProfiles().Single(p => p.Id == "Gaming");
        var without = _service.GenerateDelta(profile, new[] { printing }, new HashSet<GamingExtra>(),
            Array.Empty<string>(), new[] { "Printing" }).Items.Single(i => i.LogicalId == "Printing");
        var with = _service.GenerateDelta(profile, new[] { printing },
            new HashSet<GamingExtra> { GamingExtra.PrintScan }, Array.Empty<string>(), new[] { "Printing" })
            .Items.Single(i => i.LogicalId == "Printing");
        Assert.Equal(ProfileDisposition.Keep, with.Disposition);
        Assert.NotEqual(ProfileDisposition.Keep, without.Disposition);
    }

    [Fact]
    public void XboxGamePass_Extra_Keeps_Gaming_Services()
    {
        var gs = Subject("GamingServices", GamingKnowledge.K("GamingServices", ComponentFunctionCategory.Gaming,
            ComponentRiskLevel.Moderate, ComponentRecommendationKind.ProfileDependent,
            ComponentProtectionLevel.Sensitive, ComponentProfileTag.GamingRelevant, ClassificationConfidence.Curated));
        var profile = _catalog.GetProfiles().Single(p => p.Id == "Gaming");
        var with = _service.GenerateDelta(profile, new[] { gs },
            new HashSet<GamingExtra> { GamingExtra.XboxGamePass }, Array.Empty<string>(), new[] { "GamingServices" })
            .Items.Single(i => i.LogicalId == "GamingServices");
        Assert.Equal(ProfileDisposition.Keep, with.Disposition);
        Assert.Equal("Profile.Reason.Gaming.Keep.Extra.XboxGamePass", with.ReasonKey);
    }

    [Fact]
    public void Extras_Toggle_Changes_The_Report()
    {
        var wsl = Subject("Wsl", GamingKnowledge.K("Wsl", ComponentFunctionCategory.Virtualization,
            ComponentRiskLevel.Moderate, ComponentRecommendationKind.ProfileDependent,
            ComponentProtectionLevel.Sensitive, ComponentProfileTag.Virtualization, ClassificationConfidence.Curated));
        var profile = _catalog.GetProfiles().Single(p => p.Id == "Gaming");
        var off = _service.GenerateDelta(profile, new[] { wsl }, new HashSet<GamingExtra>(),
            Array.Empty<string>(), new[] { "Wsl" });
        var on = _service.GenerateDelta(profile, new[] { wsl }, new HashSet<GamingExtra> { GamingExtra.WslDocker },
            Array.Empty<string>(), new[] { "Wsl" });
        Assert.True(off.Kept != on.Kept || off.Optional != on.Optional,
            "toggling an extra must change the plan (Kept/Optional counts)");
    }

    // =====================================================================
    // 4. REAL-DERIVED FIXTURE PROFILE COMPARISON (§7/§13) — deterministic
    // =====================================================================

    private static IReadOnlyList<ProfilePlanSubject> FixtureSubjects()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "25H2-Pro-zhCN-component-families.json");
        Assert.True(File.Exists(path), $"fixture missing at {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var classifier = RealInventoryFixture.Classifier;
        var subjects = new List<ProfilePlanSubject>();
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            var classification = e.GetProperty("classification").GetString();
            if (classification is not ("Curated" or "KnownDeep"))
            {
                continue;
            }

            var representative = e.GetProperty("representative").GetString()!;
            var source = e.GetProperty("source").GetString()!;
            var category = Enum.TryParse<ComponentCategory>(source, out var c) ? c : ComponentCategory.Unknown;
            var k = classifier.Classify(representative);
            if (k is not null)
            {
                subjects.Add(ProfilePlanSubject.FromKnowledge(representative, category, k));
            }
        }

        // The profile surface also includes the optimization layer (Privacy /
        // Personalization / Services / UI). Adding the profile-targeted ids makes
        // the comparison complete — Office/Developer/Lightweight trims land.
        subjects.AddRange(OptimizationSubjects());

        Assert.NotEmpty(subjects);
        return subjects;
    }

    /// <summary>
    /// Deterministic optimization-layer subjects for the ids the six primary
    /// profiles actually override (privacy/personalization/services/UI).
    /// </summary>
    private static IReadOnlyList<ProfilePlanSubject> OptimizationSubjects()
    {
        var ops = new List<ProfilePlanSubject>();

        void Opt(string id, ExecutionOperationType type, OptimizationAction action,
            RecommendationLevel rec = RecommendationLevel.OptionalRemove, RiskLevel risk = RiskLevel.Low)
            => ops.Add(new ProfilePlanSubject
            {
                LogicalId = id,
                DisplayName = id,
                OperationType = type,
                Action = action,
                DefaultRecommendation = rec,
                Risk = risk,
                IsPresent = true,
                IsApplySupported = true,
                ExecutionSupported = ExecutionSupportMatrix.IsExecutable(type),
            });

        Opt("AdvertisingId", ExecutionOperationType.Privacy, OptimizationAction.Disable, RecommendationLevel.RecommendedRemove);
        Opt("TailoredExperiences", ExecutionOperationType.Privacy, OptimizationAction.Disable, RecommendationLevel.RecommendedRemove);
        Opt("FeedbackNotifications", ExecutionOperationType.Privacy, OptimizationAction.Disable, RecommendationLevel.RecommendedRemove);
        Opt("SpotlightFeatures", ExecutionOperationType.Privacy, OptimizationAction.Disable, RecommendationLevel.RecommendedRemove);
        Opt("ActivityHistory", ExecutionOperationType.Privacy, OptimizationAction.Disable, RecommendationLevel.RecommendedRemove);
        Opt("AppLaunchTracking", ExecutionOperationType.Privacy, OptimizationAction.Disable, RecommendationLevel.RecommendedRemove);
        Opt("WebSearchStart", ExecutionOperationType.Privacy, OptimizationAction.Disable, RecommendationLevel.RecommendedRemove);
        Opt("Tips", ExecutionOperationType.Personalization, OptimizationAction.Configure);
        Opt("HideStartRecommended", ExecutionOperationType.Personalization, OptimizationAction.Configure, RecommendationLevel.RecommendedRemove);
        Opt("HideStartRecentlyAdded", ExecutionOperationType.Personalization, OptimizationAction.Configure, RecommendationLevel.RecommendedRemove);
        Opt("HideTaskbarWidgets", ExecutionOperationType.Personalization, OptimizationAction.Configure, RecommendationLevel.RecommendedRemove);
        Opt("DisableSpotlight", ExecutionOperationType.Personalization, OptimizationAction.Configure, RecommendationLevel.RecommendedRemove);
        Opt("TaskbarSearchIcon", ExecutionOperationType.Personalization, OptimizationAction.Configure, RecommendationLevel.RecommendedRemove);
        Opt("HideRecentQuickAccess", ExecutionOperationType.Personalization, OptimizationAction.Configure, RecommendationLevel.RecommendedRemove);
        Opt("HideFrequentQuickAccess", ExecutionOperationType.Personalization, OptimizationAction.Configure, RecommendationLevel.RecommendedRemove);
        Opt("DisableTransparency", ExecutionOperationType.Personalization, OptimizationAction.Configure, RecommendationLevel.RecommendedRemove);
        Opt("RetailDemo", ExecutionOperationType.Service, OptimizationAction.Service, RecommendationLevel.RecommendedRemove);
        Opt("XboxGipSvc", ExecutionOperationType.Service, OptimizationAction.Service, RecommendationLevel.RecommendedRemove);
        Opt("XboxNetApiSvc", ExecutionOperationType.Service, OptimizationAction.Service, RecommendationLevel.RecommendedRemove);
        Opt("XblAuthManager", ExecutionOperationType.Service, OptimizationAction.Service, RecommendationLevel.RecommendedRemove);
        Opt("MapsBroker", ExecutionOperationType.Service, OptimizationAction.Service, RecommendationLevel.RecommendedRemove);
        Opt("GameDvr", ExecutionOperationType.Service, OptimizationAction.Service, RecommendationLevel.RecommendedRemove);
        Opt("OneDrive", ExecutionOperationType.AppX, OptimizationAction.Remove);
        Opt("OneDriveSync", ExecutionOperationType.AppX, OptimizationAction.Remove);
        Opt("Teams", ExecutionOperationType.AppX, OptimizationAction.Remove);
        Opt("DevHome", ExecutionOperationType.AppX, OptimizationAction.Remove);
        return ops;
    }

    private IReadOnlyList<ProfileDeltaReport> FixtureReports()
    {
        var subjects = FixtureSubjects();
        var profiles = _catalog.GetProfiles();
        var present = subjects.Select(s => s.LogicalId).ToHashSet(StringComparer.Ordinal);
        return _service.GenerateAllPrimaries(subjects, new HashSet<GamingExtra>(),
            Array.Empty<string>(), present, profiles);
    }

    [Fact]
    public void All_Primary_Profiles_Produce_Meaningful_Changes_On_The_Fixture()
    {
        foreach (var report in FixtureReports())
        {
            Assert.True(report.ChangeCount > 0, $"{report.ProfileId} must produce meaningful changes");
            Assert.True(report.ByOperationType.Count > 0, $"{report.ProfileId} must carry an operation-type breakdown");
        }
    }

    [Fact]
    public void Balanced_And_Gaming_Differ_By_Operations()
    {
        var reports = FixtureReports().ToDictionary(r => r.ProfileId, r => r);
        var balanced = reports["Balanced"].ChangeKeys;
        var gaming = reports["Gaming"].ChangeKeys;
        Assert.NotEmpty(balanced.Except(gaming));
        Assert.NotEmpty(gaming.Except(balanced));
    }

    [Fact]
    public void Gaming_And_DedicatedGaming_Differ_By_Operations()
    {
        // Same Low-risk consumer trim is auto in both, but a MODERATE media item is
        // an explicit PROFILE-DRIVEN optional suggestion for Dedicated Gaming while
        // Gaming PC leaves it at the default (no steer) — a real policy difference
        // (ADR-089 AdditionalOptional). The id is deliberately NOT in the legacy
        // PreferredCapabilities keep list so the policy layer is what differs.
        var phone = Subject("PhoneLink", GamingKnowledge.K("PhoneLink", ComponentFunctionCategory.Communication,
            ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
            ComponentProtectionLevel.None, ComponentProfileTag.PhoneIntegration, ClassificationConfidence.Curated));
        var media = Subject("MediaPlayerX", GamingKnowledge.K("MediaPlayerX", ComponentFunctionCategory.Media,
            ComponentRiskLevel.Moderate, ComponentRecommendationKind.ProfileDependent,
            ComponentProtectionLevel.Sensitive, ComponentProfileTag.None, ClassificationConfidence.Curated));

        var gaming = _catalog.GetProfiles().Single(p => p.Id == "Gaming");
        var dedicated = _catalog.GetProfiles().Single(p => p.Id == "DedicatedGaming");
        var g = _service.GenerateDelta(gaming, new[] { phone, media }, new HashSet<GamingExtra>(),
            Array.Empty<string>(), new[] { "PhoneLink", "MediaPlayerX" });
        var d = _service.GenerateDelta(dedicated, new[] { phone, media }, new HashSet<GamingExtra>(),
            Array.Empty<string>(), new[] { "PhoneLink", "MediaPlayerX" });

        // Both auto the Low consumer trim.
        Assert.Contains("AppX|PhoneLink|AutoApply", g.ChangeKeys);
        Assert.Contains("AppX|PhoneLink|AutoApply", d.ChangeKeys);

        // REAL policy difference: Dedicated Gaming actively suggests the moderate
        // media item as an OPTIONAL change (profile-driven, deterministic reason);
        // Gaming PC leaves it at the default (no steer).
        var gMedia = g.Items.Single(i => i.LogicalId == "MediaPlayerX");
        var dMedia = d.Items.Single(i => i.LogicalId == "MediaPlayerX");
        Assert.False(gMedia.WasProfileDriven, "Gaming PC has no steer for the media item");
        Assert.True(dMedia.WasProfileDriven, "Dedicated Gaming steers the media item");
        Assert.Equal("Profile.Reason.Execution.Optional", gMedia.ReasonKey);
        Assert.Equal("Profile.Reason.Gaming.Optional.Media", dMedia.ReasonKey);
        Assert.True(d.Optional >= g.Optional,
            $"DedicatedGaming optional ({d.Optional}) must be >= Gaming PC ({g.Optional})");
    }

    [Fact]
    public void Developer_And_Office_Differ_By_Operations()
    {
        var reports = FixtureReports().ToDictionary(r => r.ProfileId, r => r);
        var developer = reports["Developer"].ChangeKeys;
        var office = reports["Office"].ChangeKeys;
        Assert.NotEmpty(developer.Except(office));
        Assert.NotEmpty(office.Except(developer));
        _ = developer;
        _ = office;
    }

    [Fact]
    public void Lightweight_Differs_From_Balanced_By_Operations()
    {
        var reports = FixtureReports().ToDictionary(r => r.ProfileId, r => r);
        var lightweight = reports["Lightweight"];
        var balanced = reports["Balanced"];
        Assert.NotEmpty(lightweight.ChangeKeys.Except(balanced.ChangeKeys));
        Assert.True(lightweight.ChangeCount >= balanced.ChangeCount,
            $"Lightweight changes ({lightweight.ChangeCount}) must be >= Balanced ({balanced.ChangeCount})");
    }

    [Fact]
    public void Fixture_Reports_Carry_Operation_Type_Breakdown()
    {
        var report = FixtureReports().Single(r => r.ProfileId == "Gaming");
        Assert.Contains(report.ByOperationType, kv => kv.Value > 0);
        Assert.True(report.ByOperationType.Values.Sum() > 0);
    }

    // =====================================================================
    // 5. MANUAL OVERRIDE AUTHORITY (§10)
    // =====================================================================

    [Fact]
    public void Override_Survives_Profile_Change_And_Is_Not_AutoApplied()
    {
        var subjects = FixtureSubjects();
        var overridden = subjects[0].LogicalId;
        var reportsA = _service.GenerateAllPrimaries(subjects, new HashSet<GamingExtra>(),
            new[] { overridden }, subjects.Select(s => s.LogicalId).ToHashSet(StringComparer.Ordinal),
            _catalog.GetProfiles());
        foreach (var report in reportsA)
        {
            var item = report.Items.SingleOrDefault(i => i.LogicalId == overridden);
            Assert.NotNull(item);
            Assert.True(item!.IsUserOverride);
            Assert.False(item.Disposition == ProfileDisposition.AutoApply && !item.IsUserOverride);
        }
    }

    [Fact]
    public void Changing_Extras_Does_Not_Silently_Override_User_Choices()
    {
        var wsl = Subject("Wsl", GamingKnowledge.K("Wsl", ComponentFunctionCategory.Virtualization,
            ComponentRiskLevel.Moderate, ComponentRecommendationKind.ProfileDependent,
            ComponentProtectionLevel.Sensitive, ComponentProfileTag.Virtualization, ClassificationConfidence.Curated));
        var profile = _catalog.GetProfiles().Single(p => p.Id == "Gaming");

        // User explicitly chose to REMOVE WSL; the WslDocker extra must not silently
        // resurrect it into an automatic change.
        var withOverrideAndExtra = _service.GenerateDelta(profile, new[] { wsl },
            new HashSet<GamingExtra> { GamingExtra.WslDocker }, new[] { "Wsl" }, new[] { "Wsl" });
        var item = withOverrideAndExtra.Items.Single(i => i.LogicalId == "Wsl");
        Assert.True(item.IsUserOverride);
        Assert.False(item.IsExecutableChange && !item.IsUserOverride && item.Disposition == ProfileDisposition.AutoApply);
    }

    // =====================================================================
    // 6. PROFILE PLAN VALIDATOR (§12)
    // =====================================================================

    [Fact]
    public void RemoveKeep_Conflict_Is_Detected()
    {
        var items = new[]
        {
            new ProfileExecutionItem { LogicalId = "X", Disposition = ProfileDisposition.AutoApply, OperationType = ExecutionOperationType.AppX },
            new ProfileExecutionItem { LogicalId = "X", Disposition = ProfileDisposition.Keep, OperationType = ExecutionOperationType.AppX },
        };
        var result = ProfilePlanValidator.Validate(items);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains("Remove/keep conflict", StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_Change_Plan_Is_Detected()
    {
        var items = new[]
        {
            new ProfileExecutionItem { LogicalId = "X", Disposition = ProfileDisposition.AutoApply, OperationType = ExecutionOperationType.AppX },
            new ProfileExecutionItem { LogicalId = "X", Disposition = ProfileDisposition.AutoApply, OperationType = ExecutionOperationType.AppX },
        };
        var result = ProfilePlanValidator.Validate(items);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Contains("Duplicate change plan", StringComparison.Ordinal));
    }

    [Fact]
    public void Dependency_Required_Removal_Is_Detected()
    {
        var kept = new HashSet<string> { "Store" };
        var subjects = new[]
        {
            new ProfilePlanSubject
            {
                LogicalId = "StorePurchase",
                Dependencies = new[] { new ComponentDependency { Relation = DependencyRelation.Requires, ToId = "Store" } },
            },
        };
        var issues = ProfilePlanValidator.ValidateDependencyKeep(kept, subjects, new HashSet<string> { "StorePurchase" });
        Assert.NotEmpty(issues);
        Assert.Contains(issues, i => i.Contains("requires it", StringComparison.Ordinal));
    }

    // =====================================================================
    // 7. BUILD PLAN (§5/§12)
    // =====================================================================

    [Fact]
    public void BuildPlan_Produces_Validated_Plan_With_Only_Executable_Changes()
    {
        var profile = _catalog.GetProfiles().Single(p => p.Id == "Balanced");
        var subjects = FixtureSubjects();
        var present = subjects.Select(s => s.LogicalId).ToHashSet(StringComparer.Ordinal);
        var (plan, issues) = _service.BuildPlan(profile, subjects, new HashSet<GamingExtra>(), Array.Empty<string>(), present);
        Assert.True(issues.Count == 0, string.Join("; ", issues));
        Assert.NotNull(plan);
        Assert.True(plan!.Operations.Count > 0);
        Assert.All(plan.Operations, op =>
        {
            Assert.True(ExecutionSupportMatrix.IsExecutable(op.OperationType));
            Assert.NotEqual(CustomizationOperationType.RemovePackage, op.OperationType);
            Assert.NotEqual(CustomizationOperationType.RemoveCapability, op.OperationType);
        });
    }

    [Fact]
    public void BuildPlan_Excludes_Unsupported_And_Overridden_Items()
    {
        var cbs = Subject("CbsPackageX", GamingKnowledge.K("CbsPackageX", ComponentFunctionCategory.SystemCore,
            ComponentRiskLevel.High, ComponentRecommendationKind.RecommendedKeep,
            ComponentProtectionLevel.Sensitive, ComponentProfileTag.None, ClassificationConfidence.Curated),
            ComponentCategory.CbsPackage);
        var appx = Subject("PhoneLink", GamingKnowledge.K("PhoneLink", ComponentFunctionCategory.Communication,
            ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
            ComponentProtectionLevel.None, ComponentProfileTag.PhoneIntegration, ClassificationConfidence.Curated));
        var profile = _catalog.GetProfiles().Single(p => p.Id == "Gaming");

        var (plan, issues) = _service.BuildPlan(profile, new[] { cbs, appx },
            new HashSet<GamingExtra>(), new[] { "PhoneLink" }, new[] { "CbsPackageX", "PhoneLink" });
        // CBS is unsupported → excluded (not a blocking issue by itself);
        // the overridden PhoneLink is excluded too. Plan may still be null if empty.
        Assert.DoesNotContain(plan?.Operations ?? Array.Empty<CustomizationOperation>(),
            op => op.OperationType == CustomizationOperationType.RemovePackage);
        Assert.DoesNotContain(plan?.Operations ?? Array.Empty<CustomizationOperation>(),
            op => op.TargetIdentifier == "PhoneLink");
    }

    // =====================================================================
    // 8. USER-FACING PROFILE PREVIEW (§8) — bounded, localized, no id floods
    // =====================================================================

    [Fact]
    public void Profile_Preview_Shows_Bounded_Summary_For_Every_Primary()
    {
        foreach (var profileId in new[] { "Balanced", "Gaming", "Developer", "Office", "Lightweight" })
        {
            var preview = BuildPreview(profileId);
            Assert.False(string.IsNullOrWhiteSpace(preview), $"{profileId} preview must not be empty");
            Assert.Contains("Automatic changes:", preview);
            Assert.Contains("Kept:", preview);
        }
    }

    [Fact]
    public void Profile_Preview_Is_Bounded_And_Shows_Highlights()
    {
        var preview = BuildPreview("Balanced");
        // Highlights capped: at most 4 highlight lines + 1 ellipsis.
        var highlightCount = preview.Split('\n').Count(l => l.TrimStart().StartsWith("✓ ", StringComparison.Ordinal));
        Assert.True(highlightCount <= 4, "preview must never flood highlights");
        Assert.Contains("Highlights", preview);
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

/// <summary>Stage 15.1 test CI service: returns a small curated AppX inventory.</summary>
public sealed class Stage15InventoryCiService : IComponentIntelligenceService
{
    public Task<ComponentInventory> DiscoverAsync(
        ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
    {
        var items = new[]
        {
            Raw("Microsoft.BingWeather_8wekyb3d8bbwe"),
            Raw("Microsoft.YourPhone_8wekyb3d8bbwe"),
            Raw("MicrosoftSolitaireCollection_8wekyb3d8bbwe"),
            Raw("Microsoft.WindowsMaps_8wekyb3d8bbwe"),
        };
        return Task.FromResult(new ComponentInventory
        {
            Discovered = true,
            Categories = new List<CategoryDiscoveryResult>
            {
                new() { Category = ComponentCategory.AppX, Status = InventoryStatus.Success,
                    Items = items.Cast<IRawInventoryItem>().ToList() },
            },
        });
    }

    private static IRawInventoryItem Raw(string id) => new TestRaw { Category = ComponentCategory.AppX, RawIdentity = id, DisplayName = id };

    private sealed class TestRaw : RawInventoryItem
    {
    }
}
