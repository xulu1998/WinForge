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
// Phase 14 Stage 14.3c — FINAL high-confidence long-tail classification
// (second elevated capture VALIDATED 14.3b: 757 / Known 591 / Unknown 134
// / coverage 82.30%). Tests prove: Wi-Fi/Ethernet driver families never
// auto-remove; DirectX config / SecHealthUI / FOD metadata / storage /
// compatibility infrastructure stay kept; codecs never disappear from a
// normal Gaming PC; Outlook/Office Hub follow supported AppX removal
// rules; Dev Home kept for Developer + optional-only in Gaming;
// HostGuardian never Low-risk auto; LegacyComponents stays ProfileDependent;
// no broad namespace fallback rules; heuristic count unchanged.
// =====================================================================

public class Stage14cCoverageFinalTests
{
    private readonly DeepComponentClassifier _classifier = RealInventoryFixture.Classifier;
    private readonly GamingProfileEvaluationService _service = new();

    private static GamingPolicyInput Input(string rawId, DeepComponentKnowledge k, bool supported = true)
        => new()
        {
            RawIdentity = rawId,
            Source = ComponentCategory.AppX,
            Knowledge = k,
            Extras = new HashSet<GamingExtra>(),
            IsPresent = true,
            SupportedForRemoval = supported,
        };

    // ---- §1: Wi-Fi / Ethernet driver families NEVER auto-remove ----
    [Theory]
    [InlineData("Microsoft.Windows.Wifi.Client.Intel~~~~0.0.1.0")]
    [InlineData("Microsoft.Windows.Wifi.Client.Realtek~~~~0.0.1.0")]
    [InlineData("Microsoft.Windows.Wifi.Client.Broadcom~~~~0.0.1.0")]
    [InlineData("Microsoft.Windows.Wifi.Client.Qualcomm~~~~0.0.1.0")]
    [InlineData("Microsoft.Windows.Wifi.Client.Intel.10.0.26200.1~~~~0.0.1.0")]
    public void Wifi_Driver_Families_Never_Auto_Remove(string id)
    {
        var k = _classifier.Classify(id);
        Assert.NotNull(k);
        Assert.Equal("NetWifiClientFamily", k!.CanonicalId);
        Assert.Equal(ComponentRiskLevel.High, k.Risk);
        Assert.Equal(ComponentFunctionCategory.Networking, k.Function);
        Assert.Equal(ComponentRecommendationKind.RecommendedKeep, k.Recommendation);
        Assert.Equal(ComponentProtectionLevel.Sensitive, k.Protection);

        // Both gaming profiles keep every Wi-Fi vendor family — never a candidate.
        foreach (var kind in new[] { GamingProfileKind.GamingPc, GamingProfileKind.DedicatedGaming })
        {
            var result = _service.Evaluate(new[] { Input(id, k) }, kind, new HashSet<GamingExtra>());
            var item = Assert.Single(result.Items);
            Assert.True(item.Result.IsKeptForCompatibility, $"{id} must be kept by {kind}");
            Assert.NotEqual(GamingVerdict.AutoRemoveCandidate, item.Result.Verdict);
        }
    }

    [Theory]
    [InlineData("Microsoft.Windows.Ethernet.Client.Intel~~~~0.0.1.0")]
    [InlineData("Microsoft.Windows.Ethernet.Client.Intel.E1i68x64~~~~0.0.1.0")]
    [InlineData("Microsoft.Windows.Ethernet.Client.Realtek.RTK2x64~~~~0.0.1.0")]
    public void Ethernet_Driver_Families_Never_Auto_Remove(string id)
    {
        var k = _classifier.Classify(id);
        Assert.NotNull(k);
        Assert.Equal("NetEthernetClientFamily", k!.CanonicalId);
        Assert.Equal(ComponentRiskLevel.High, k.Risk);

        var result = _service.Evaluate(new[] { Input(id, k) }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        var item = Assert.Single(result.Items);
        Assert.True(item.Result.IsKeptForCompatibility);
    }

    // ---- §2: critical / important system items ----
    [Fact]
    public void DirectX_Configuration_Always_Kept_In_Gaming()
    {
        var k = _classifier.Classify("DirectX.Configuration.Database~~~~0.0.1.0");
        Assert.NotNull(k);
        Assert.Equal("DirectXConfigurationDatabase", k!.CanonicalId);
        Assert.Equal(ComponentRiskLevel.Critical, k.Risk);
        Assert.Equal(ComponentRecommendationKind.RequiredKeep, k.Recommendation);
        Assert.Equal(ComponentProfileTag.GamingRelevant, k.ProfileTag);

        foreach (var kind in new[] { GamingProfileKind.GamingPc, GamingProfileKind.DedicatedGaming })
        {
            var result = _service.Evaluate(
                new[] { Input("DirectX.Configuration.Database~~~~0.0.1.0", k) }, kind, new HashSet<GamingExtra>());
            var item = Assert.Single(result.Items);
            Assert.True(item.Result.IsKeptForCompatibility, $"DirectX config must be kept by {kind}");
            Assert.Equal("Profile.Reason.Gaming.Keep.Runtime", item.Result.ReasonKey);
        }
    }

    [Fact]
    public void SecHealthUi_Is_Critical_And_Protected()
    {
        var k = _classifier.Classify("Microsoft.SecHealthUI_8wekyb3d8bbwe");
        Assert.NotNull(k);
        Assert.Equal("SecHealthUi", k!.CanonicalId);
        Assert.Equal(ComponentRiskLevel.Critical, k.Risk);
        Assert.Equal(ComponentProtectionLevel.Protected, k.Protection);
        Assert.Equal(ComponentRecommendationKind.RequiredKeep, k.Recommendation);

        var result = _service.Evaluate(
            new[] { Input("Microsoft.SecHealthUI_8wekyb3d8bbwe", k) }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        var item = Assert.Single(result.Items);
        Assert.True(item.Result.IsKeptForCompatibility);
        Assert.Equal("Profile.Reason.Gaming.Keep.Protection", item.Result.ReasonKey);
    }

    [Fact]
    public void FodMetadata_Package_Is_Critical_And_Protected()
    {
        var k = _classifier.Classify("Microsoft-Windows-FodMetadata-Package~31bf3856ad364e35~amd64~~10.0.26200");
        Assert.NotNull(k);
        Assert.Equal("FodMetadataPackage", k!.CanonicalId);
        Assert.Equal(ComponentRiskLevel.Critical, k.Risk);
        Assert.Equal(ComponentProtectionLevel.Protected, k.Protection);
        Assert.Equal(ComponentRecommendationKind.RequiredKeep, k.Recommendation);

        var result = _service.Evaluate(
            new[] { Input("Microsoft-Windows-FodMetadata-Package~31bf3856ad364e35~amd64~~10.0.26200", k) },
            GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        var item = Assert.Single(result.Items);
        Assert.True(item.Result.IsKeptForCompatibility);
    }

    [Fact]
    public void StorageManagement_Is_Not_Auto_Removable()
    {
        var k = _classifier.Classify("Microsoft.Onecore.StorageManagement~~~~0.0.1.0");
        Assert.NotNull(k);
        Assert.Equal("OnecoreStorageManagement", k!.CanonicalId);
        Assert.Equal(ComponentRiskLevel.High, k.Risk);
        Assert.Equal(ComponentFunctionCategory.SystemCore, k.Function);

        foreach (var kind in new[] { GamingProfileKind.GamingPc, GamingProfileKind.DedicatedGaming })
        {
            var result = _service.Evaluate(
                new[] { Input("Microsoft.Onecore.StorageManagement~~~~0.0.1.0", k) }, kind, new HashSet<GamingExtra>());
            var item = Assert.Single(result.Items);
            Assert.True(item.Result.IsKeptForCompatibility, $"storage mgmt must be kept by {kind}");
        }
    }

    [Theory]
    [InlineData("Microsoft.ApplicationCompatibilityEnhancements_8wekyb3d8bbwe")]
    [InlineData("Microsoft-ApplicationCompatibilityEnhancements-Package~31bf3856ad364e35~amd64~~10.0.26200")]
    public void ApplicationCompatibility_Is_Not_Auto_Removable(string id)
    {
        var k = _classifier.Classify(id);
        Assert.NotNull(k);
        Assert.Equal("ApplicationCompatibilityEnhancements", k!.CanonicalId);
        Assert.Equal(ComponentRiskLevel.High, k.Risk);
        Assert.Equal(ComponentRecommendationKind.RecommendedKeep, k.Recommendation);
        Assert.Equal(ComponentProtectionLevel.Sensitive, k.Protection);

        foreach (var kind in new[] { GamingProfileKind.GamingPc, GamingProfileKind.DedicatedGaming })
        {
            var result = _service.Evaluate(new[] { Input(id, k) }, kind, new HashSet<GamingExtra>());
            var item = Assert.Single(result.Items);
            Assert.True(item.Result.IsKeptForCompatibility, $"compat infra must be kept by {kind}");
        }
    }

    [Fact]
    public void HelloFace_Classifies_Conservative()
    {
        var k = _classifier.Classify("Hello.Face~~~~0.0.1.0");
        Assert.NotNull(k);
        Assert.Equal("HelloFaceCapability", k!.CanonicalId);
        Assert.Equal(ComponentRiskLevel.High, k.Risk);
        Assert.Equal(ComponentProtectionLevel.Sensitive, k.Protection);
        Assert.Equal(ComponentRecommendationKind.ProfileDependent, k.Recommendation);

        var result = _service.Evaluate(
            new[] { Input("Hello.Face~~~~0.0.1.0", k) }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        var item = Assert.Single(result.Items);
        Assert.True(item.Result.IsKeptForCompatibility);
    }

    // ---- §3: media codecs never disappear automatically ----
    [Theory]
    [InlineData("Microsoft.HEIFImageExtension_8wekyb3d8bbwe")]
    [InlineData("Microsoft.HEVCVideoExtension_8wekyb3d8bbwe")]
    [InlineData("Microsoft.MPEG2VideoExtension_8wekyb3d8bbwe")]
    [InlineData("Microsoft.RawImageExtension_8wekyb3d8bbwe")]
    [InlineData("Microsoft.VP9VideoExtensions_8wekyb3d8bbwe")]
    [InlineData("Microsoft.WebMediaExtensions_8wekyb3d8bbwe")]
    [InlineData("Microsoft.WebpImageExtension_8wekyb3d8bbwe")]
    public void Codecs_Do_Not_Disappear_Automatically_From_Gaming_Pc(string id)
    {
        var k = _classifier.Classify(id);
        Assert.NotNull(k);
        Assert.Equal(ComponentFunctionCategory.Media, k!.Function);
        Assert.Equal(ComponentRiskLevel.Low, k.Risk);

        var result = _service.Evaluate(
            new[] { Input(id, k) }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        var item = Assert.Single(result.Items);
        Assert.False(item.Result.IsAutoRecommended, $"{id} must never auto-remove from Gaming PC");
        Assert.NotEqual(GamingVerdict.AutoRemoveCandidate, item.Result.Verdict);
        // Optional, user-confirmed at most — the codec stays unless the user asks.
        Assert.True(item.Result.IsOptionalSuggestion);
        Assert.Equal(GateVerdict.AllowOptional, item.Result.Gate);
    }

    [Theory]
    [InlineData("Microsoft.HEIFImageExtension_8wekyb3d8bbwe")]
    [InlineData("Microsoft.HEVCVideoExtension_8wekyb3d8bbwe")]
    [InlineData("Microsoft.MPEG2VideoExtension_8wekyb3d8bbwe")]
    [InlineData("Microsoft.RawImageExtension_8wekyb3d8bbwe")]
    [InlineData("Microsoft.VP9VideoExtensions_8wekyb3d8bbwe")]
    [InlineData("Microsoft.WebMediaExtensions_8wekyb3d8bbwe")]
    [InlineData("Microsoft.WebpImageExtension_8wekyb3d8bbwe")]
    public void Dedicated_Gaming_Codecs_Remain_Optional_At_Most(string id)
    {
        var k = _classifier.Classify(id);
        Assert.NotNull(k);

        var result = _service.Evaluate(
            new[] { Input(id, k) }, GamingProfileKind.DedicatedGaming, new HashSet<GamingExtra>());
        var item = Assert.Single(result.Items);
        Assert.False(item.Result.IsAutoRecommended, $"{id} must never auto-remove from Dedicated Gaming");
        Assert.NotEqual(GamingVerdict.AutoRemoveCandidate, item.Result.Verdict);
        Assert.NotEqual(GateVerdict.AllowAuto, item.Result.Gate);
    }

    // ---- §4: user-facing AppX ----
    [Theory]
    [InlineData("Microsoft.OutlookForWindows_8wekyb3d8bbwe")]
    [InlineData("Microsoft.MicrosoftOfficeHub_8wekyb3d8bbwe")]
    public void Outlook_And_OfficeHub_Follow_Supported_Removal_Rules(string id)
    {
        var k = _classifier.Classify(id);
        Assert.NotNull(k);
        Assert.Equal(ComponentRiskLevel.Low, k.Risk);
        Assert.Equal(ComponentProfileTag.ConsumerContent, k.ProfileTag);

        // Supported AppX removal exists → Gaming PC may recommend removal.
        var supported = _service.Evaluate(
            new[] { Input(id, k, supported: true) }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        var ok = Assert.Single(supported.Items);
        Assert.Equal(GamingVerdict.AutoRemoveCandidate, ok.Result.Verdict);
        Assert.True(ok.Result.IsAutoRecommended, "supported AppX removal → Gaming PC auto-recommends");

        // No supported removal mechanism → classification never becomes removal.
        var unsupported = _service.Evaluate(
            new[] { Input(id, k, supported: false) }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        var blocked = Assert.Single(unsupported.Items);
        Assert.Equal(GateVerdict.Block, blocked.Result.Gate);
        Assert.Equal("Profile.Reason.Gaming.Gate.Unsupported", blocked.Result.GateReasonKey);
        Assert.False(blocked.Result.IsAutoRecommended);
    }

    [Fact]
    public void DevHome_Is_Optional_Only_In_Gaming()
    {
        var k = _classifier.Classify("Microsoft.Windows.DevHome_8wekyb3d8bbwe");
        Assert.NotNull(k);
        Assert.Equal("DevHome", k!.CanonicalId);
        Assert.Equal(ComponentFunctionCategory.Developer, k.Function);
        Assert.Equal(ComponentProfileTag.DeveloperTool, k.ProfileTag);

        // Gaming PC: Dev Home stays OPTIONAL-ONLY (convenient default, never auto).
        var result = _service.Evaluate(
            new[] { Input("Microsoft.Windows.DevHome_8wekyb3d8bbwe", k) }, GamingProfileKind.GamingPc,
            new HashSet<GamingExtra>());
        var item = Assert.Single(result.Items);
        Assert.Equal(GamingVerdict.OptionalRemoveCandidate, item.Result.Verdict);
        Assert.False(item.Result.IsAutoRecommended, "Dev Home is optional-only in Gaming PC, never auto");
        Assert.Equal(GateVerdict.AllowOptional, item.Result.Gate);

        // Stage 15.2b (ADR-095 addendum): Dedicated Gaming RECOMMENDS Dev Home
        // removal (Moderate → user-confirmed, never automatic) — the wider-minimal
        // steer that makes the two gaming profiles differ on real media.
        var dedicated = _service.Evaluate(
            new[] { Input("Microsoft.Windows.DevHome_8wekyb3d8bbwe", k) }, GamingProfileKind.DedicatedGaming,
            new HashSet<GamingExtra>());
        var dItem = Assert.Single(dedicated.Items);
        Assert.Equal(GamingVerdict.AutoRemoveCandidate, dItem.Result.Verdict);
        Assert.False(dItem.Result.IsAutoRecommended, "Moderate risk never auto-applies, even in Dedicated");
        Assert.Equal(GateVerdict.AllowOptional, dItem.Result.Gate);
    }

    [Fact]
    public void DevHome_Is_Kept_For_Developer_Profile()
    {
        var profiles = new ProfileCatalog().GetProfiles().Where(p => p.Id == "Developer").ToList();
        var engine = new RecommendationEngine();
        var result = engine.Evaluate(
            new RecommendationInput
            {
                LogicalId = "DevHome",
                Action = OptimizationAction.Remove,
                DefaultRecommendation = RecommendationLevel.OptionalRemove,
                Risk = RiskLevel.Low,
                IsPresent = true,
                IsApplySupported = true,
            },
            new RecommendationContext
            {
                SelectedProfiles = profiles,
                UserOverrides = new HashSet<string>(),
                PresentIds = new HashSet<string>(),
            });

        Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, result.Level);
        Assert.True(result.WasProfileDriven);
        Assert.Contains("Profile.Reason.Developer.DevHome", result.ReasonKeys);
    }

    // ---- §5: remaining clear capabilities ----
    [Theory]
    [InlineData("Microsoft.Windows.Console.Legacy~~~~0.0.1.0", "ConsoleLegacy", ComponentFunctionCategory.LegacyCompatibility)]
    [InlineData("Microsoft.WebDriver~~~~0.0.1.0", "WebDriver", ComponentFunctionCategory.Developer)]
    [InlineData("MathRecognizer~~~~0.0.1.0", "MathRecognizer", ComponentFunctionCategory.Accessibility)]
    [InlineData("App.WirelessDisplay.Connect~~~~0.0.1.0", "WirelessDisplayConnect", ComponentFunctionCategory.Media)]
    public void Clear_Capabilities_Classify_ProfileDependent_And_Never_Auto(string id, string canonical, ComponentFunctionCategory fn)
    {
        var k = _classifier.Classify(id);
        Assert.NotNull(k);
        Assert.Equal(canonical, k!.CanonicalId);
        Assert.Equal(fn, k.Function);
        Assert.Equal(ComponentRecommendationKind.ProfileDependent, k.Recommendation);

        // No profile steer in Gaming PC — never an automatic removal, never even a
        // suggestion (EvaluateItem returns null for NoOpinion/kept items). Dedicated
        // Gaming may legitimately suggest media-adjacent capabilities as OPTIONAL
        // (never automatic).
        Assert.Null(_service.EvaluateItem(Input(id, k), GamingProfileKind.GamingPc));
        var dedicated = _service.EvaluateItem(Input(id, k), GamingProfileKind.DedicatedGaming);
        Assert.True(dedicated is null || dedicated.Verdict != GamingVerdict.AutoRemoveCandidate,
            $"{id} must never auto-remove in Dedicated Gaming");
    }

    [Fact]
    public void Wallpapers_Extended_Is_Optional_Consumer_Not_Auto()
    {
        var k = _classifier.Classify("Microsoft.Wallpapers.Extended~~~~0.0.1.0");
        Assert.NotNull(k);
        Assert.Equal("WallpapersExtended", k!.CanonicalId);
        Assert.Equal(ComponentFunctionCategory.ShellExperience, k.Function);
        Assert.Equal(ComponentProfileTag.ConsumerContent, k.ProfileTag);

        var result = _service.Evaluate(
            new[] { Input("Microsoft.Wallpapers.Extended~~~~0.0.1.0", k) }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        var item = Assert.Single(result.Items);
        Assert.Equal(GamingVerdict.OptionalRemoveCandidate, item.Result.Verdict);
        Assert.False(item.Result.IsAutoRecommended);
        Assert.Equal(GateVerdict.AllowOptional, item.Result.Gate);
    }

    // ---- §6: clear high-confidence OptionalFeatures ----
    [Fact]
    public void HostGuardian_Never_Becomes_LowRisk_Auto_Remove()
    {
        var k = _classifier.Classify("HostGuardian");
        Assert.NotNull(k);
        Assert.Equal("HostGuardian", k!.CanonicalId);
        Assert.Equal(ComponentRiskLevel.High, k.Risk);
        Assert.Equal(ComponentFunctionCategory.Security, k.Function);
        Assert.Equal(ComponentRecommendationKind.ProfileDependent, k.Recommendation);

        foreach (var kind in new[] { GamingProfileKind.GamingPc, GamingProfileKind.DedicatedGaming })
        {
            var result = _service.Evaluate(new[] { Input("HostGuardian", k) }, kind, new HashSet<GamingExtra>());
            var item = Assert.Single(result.Items);
            Assert.True(item.Result.IsKeptForCompatibility, $"Host Guardian must be kept by {kind}");
        }
    }

    [Fact]
    public void LegacyComponents_Remains_ProfileDependent()
    {
        var k = _classifier.Classify("LegacyComponents");
        Assert.NotNull(k);
        Assert.Equal("LegacyComponents", k!.CanonicalId);
        Assert.Equal(ComponentFunctionCategory.LegacyCompatibility, k.Function);
        Assert.Equal(ComponentRiskLevel.Moderate, k.Risk);
        Assert.Equal(ComponentRecommendationKind.ProfileDependent, k.Recommendation);

        // No profile steer — falls through to defaults, never an auto removal.
        Assert.Null(_service.EvaluateItem(Input("LegacyComponents", k), GamingProfileKind.GamingPc));
    }

    [Theory]
    [InlineData("ClientForNFS-Infrastructure", "ClientForNfsInfrastructure")]
    [InlineData("DataCenterBridging", "DataCenterBridging")]
    [InlineData("DirectoryServices-ADAM-Client", "DirectoryServicesAdamClient")]
    public void Enterprise_Networking_Features_Classify_Conservative(string id, string canonical)
    {
        var k = _classifier.Classify(id);
        Assert.NotNull(k);
        Assert.Equal(canonical, k!.CanonicalId);
        Assert.True(k.Risk >= ComponentRiskLevel.Moderate, $"{id} risk must be >= Moderate");
        Assert.Equal(ComponentProtectionLevel.Sensitive, k.Protection);

        // Networking/Enterprise features: Gaming PC keeps the networking ones and
        // leaves enterprise-only ones at defaults — NEVER an automatic removal.
        var decision = _service.EvaluateItem(Input(id, k), GamingProfileKind.GamingPc);
        Assert.True(decision is null || decision.Verdict == GamingVerdict.KeepForCompatibility,
            $"{id} must be kept (or defaulted) by Gaming PC — never a removal candidate");
    }

    // ---- §8/§9: coverage-quality rules ----
    [Fact]
    public void No_Broad_Namespace_Fallback_Patterns()
    {
        foreach (var e in DeepComponentCatalogData.Entries)
        {
            foreach (var p in e.Patterns)
            {
                var key = ComponentNormalizer.NormalizePattern(p);
                // §9: NO broad namespace fallback rules — a bare prefix pattern
                // ("Microsoft.*", "Windows.*", "Client-*", "Package-*") would
                // contain-match everything below it. Full multi-segment names
                // (Microsoft.XboxApp, Microsoft.WindowsStore, Client-EmbeddedBootExp,
                // Storage-*/USB-* — the pre-approved 14.2 hardware families) are
                // specific enough and fine.
                Assert.False(key is "microsoft" or "windows" or "microsoft.windows" or "microsoft-windows"
                    or "client" or "package" or "openssh" or "hello" or "microsoft." or "windows.",
                    $"broad fallback pattern '{p}' in entry '{e.Id}'");
                Assert.False(key is "client-" or "package-" or "openssh-" or "windows-",
                    $"bare vendor prefix pattern '{p}' in entry '{e.Id}'");
            }
        }
    }

    [Fact]
    public void Heuristic_Count_Unchanged_And_New_Entries_Are_Curated_Known_Families()
    {
        // No heuristic entries were added to reduce debt (ADR-091 §8 principle).
        Assert.Equal(1, DeepComponentCatalogData.Entries.Count(e => e.Confidence == ClassificationConfidence.Heuristic));
        // Catalog grew 177 -> 203 with high-confidence entries only (27 new).
        Assert.True(DeepComponentCatalogData.Entries.Count >= 200,
            $"catalog must hold the 14.3c additions (was {DeepComponentCatalogData.Entries.Count})");
        Assert.All(DeepComponentCatalogData.Entries.Where(e => e.Confidence != ClassificationConfidence.Heuristic),
            e => Assert.True(e.Risk != ComponentRiskLevel.Unknown, $"entry '{e.Id}' must carry an explicit risk"));
    }

    [Fact]
    public void New_Fixture_Entries_Classify_Stably()
    {
        foreach (var canonical in new[]
        {
            "NetWifiClientFamily", "NetEthernetClientFamily", "DirectXConfigurationDatabase",
            "SecHealthUi", "FodMetadataPackage", "OnecoreStorageManagement", "HelloFaceCapability",
            "HeifImageExtension", "HevcVideoExtension", "Mpeg2VideoExtension", "RawImageExtension",
            "Vp9VideoExtensions", "WebMediaExtensions", "WebpImageExtension",
            "OutlookForWindows", "MicrosoftOfficeHub", "DevHome", "ApplicationCompatibilityEnhancements",
            "ConsoleLegacy", "WebDriver", "WallpapersExtended", "MathRecognizer",
            "ClientForNfsInfrastructure", "DataCenterBridging", "DirectoryServicesAdamClient",
            "HostGuardian", "LegacyComponents",
        })
        {
            Assert.Contains(DeepComponentCatalogData.Entries, e => e.Id == canonical);
        }
    }
}
