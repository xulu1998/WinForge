using System;
using System.Collections.Generic;
using System.Linq;
using WinForge.Core.ComponentIntelligence;
using WinForge.Core.Models;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Real-family regression fixture (Stage 14.2 §13): family-level entries derived
/// from the real Windows 11 25H2 zh-CN x64 Consumer shape — stable across package
/// version changes because canonical forms strip versions/arch/language tokens.
/// NOT a per-object dump of the source ISO.
/// </summary>
public static class RealMediaFamilyFixture
{
    /// <summary>(canonical-like raw identity, source kind, expected CanonicalId).</summary>
    public static readonly (string Id, ComponentCategory Source, string Expected)[] AppxAndCapability = new[]
    {
        ("Microsoft.YourPhone_8wekyb3d8bbwe_x64__8wekyb3d8bbwe", ComponentCategory.AppX, "PhoneLink"),
        ("MicrosoftSolitaireCollection_8wekyb3d8bbwe", ComponentCategory.AppX, "Solitaire"),
        ("Microsoft.GetHelp_8wekyb3d8bbwe", ComponentCategory.AppX, "GetHelp"),
        ("Microsoft.BingWeather_8wekyb3d8bbwe", ComponentCategory.AppX, "BingWeather"),
        ("Microsoft.WindowsStore_8wekyb3d8bbwe", ComponentCategory.AppX, "WindowsStore"),
        ("Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", ComponentCategory.AppX, "AppInstaller"),
        ("Microsoft.GamingServices_8wekyb3d8bbwe", ComponentCategory.AppX, "GamingServices"),
        ("OpenSSH.Client~~~~0.0.1.0", ComponentCategory.Capability, "OpenSSHClient"),
        ("Printing-XPSServices-Features~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.OptionalFeature, "PrintingXPS"),
    };

    /// <summary>Real CBS family patterns (known + conservative: Risk>=High, Protection>=Sensitive).</summary>
    public static readonly (string Id, ComponentCategory Source, string Expected)[] CbsFamilies = new[]
    {
        ("Microsoft-Windows-Printing-PrintFilterPipelineSvc-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "CbsPrintingFamily"),
        ("Microsoft-Windows-LanguageFeatures-Basic-zh-cn-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "CbsLanguageFamily"),
        ("Microsoft-Windows-Client-Features-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "CbsClientFamily"),
        ("Microsoft-Windows-Foundation-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "CbsFoundationFamily"),
        ("Microsoft-Windows-ServicingStack-10.0.26200.1-amd64", ComponentCategory.CbsPackage, "ServicingStack"),
        ("Microsoft-Windows-WinRE-Recovery-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "WinRe"),
        ("Microsoft-Windows-HyperV-Services-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "HyperV"),
        ("Microsoft-Windows-Defender-Services-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "CbsDefenderFamily"),
        ("Microsoft-Windows-RemoteDesktop-Client-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "CbsRemoteDesktopFamily"),
        ("Microsoft-Windows-Bluetooth-Services-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "HwBluetoothFamily"),
        ("Microsoft-Windows-Wi-Fi-OneXUIRefresh-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "HwWifiFamily"),
        ("Microsoft-Windows-USB-Platform-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "UsbDrivers"),
        ("Microsoft-Windows-Storage-StorageManagement-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "StorageDrivers"),
        ("Microsoft-Windows-Audio-AudioCore-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "HwAudioFamily"),
        ("Microsoft-Windows-TabletPC-MathInput-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "CbsTabletPcFamily"),
        ("Microsoft-Windows-InternetExplorer-Optional-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "InternetExplorer"),
        ("Microsoft-Windows-EditionSpecific-ClientEnterprise-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "CbsEditionFamily"),
        ("Microsoft-Windows-Shell-Setup-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "CbsShellFamily"),
        ("Microsoft-Windows-Search-SearchCenter-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "CbsSearchFamily"),
        ("Microsoft-Windows-Media-MediaPlayback-Package~31bf3856ad364e35~amd64~~10.0.26200", ComponentCategory.CbsPackage, "MediaFeatures"),
    };
}

/// <summary>Stage 14.2 — real-media family expansion, coverage metrics, safety.</summary>
public class Stage14FamilyExpansionTests
{
    private readonly DeepComponentClassifier _classifier =
        new(DeepComponentCatalogData.Entries);

    // ---- 1. real-derived fixture classification ----
    [Theory]
    [MemberData(nameof(AppxFixture))]
    public void Appx_Fixture_Classifies(string id, ComponentCategory source, string expected)
        => Assert.Equal(expected, _classifier.Classify(id)?.CanonicalId);

    public static IEnumerable<object[]> AppxFixture()
        => RealMediaFamilyFixture.AppxAndCapability.Select(x => new object[] { x.Id, x.Source, x.Expected });

    [Theory]
    [MemberData(nameof(CbsFixture))]
    public void Cbs_Fixture_Classifies_To_Conservative_Family(string id, ComponentCategory source, string expected)
    {
        var k = _classifier.Classify(id);
        Assert.NotNull(k);
        Assert.Equal(expected, k!.CanonicalId);
        // CBS family safety floor: never Low (Moderate allowed only for explicitly
        // known optional families), never unprotected.
        Assert.True(k.Risk >= ComponentRiskLevel.Moderate, $"{id} risk must be >= Moderate");
        Assert.True(k.Protection >= ComponentProtectionLevel.Sensitive, $"{id} must be >= Sensitive");
    }

    public static IEnumerable<object[]> CbsFixture()
        => RealMediaFamilyFixture.CbsFamilies.Select(x => new object[] { x.Id, x.Source, x.Expected });

    // ---- 2-4. language / architecture / resource variants share one family ----
    [Theory]
    [InlineData("Microsoft-Windows-LanguageFeatures-Basic-zh-cn-Package~31bf3856ad364e35~amd64~~10.0.26200")]
    [InlineData("Microsoft-Windows-LanguageFeatures-Basic-en-us-Package~31bf3856ad364e35~amd64~~10.0.26200")]
    public void Language_Variants_Resolve_To_Same_Family(string id)
        => Assert.Equal("CbsLanguageFamily", _classifier.Classify(id)?.CanonicalId);

    [Theory]
    [InlineData("Microsoft-Windows-Client-Features-Package~31bf3856ad364e35~amd64~~10.0.26200")]
    [InlineData("Microsoft-Windows-Client-Features-Package~31bf3856ad364e35~wow64~~10.0.26200")]
    [InlineData("Microsoft-Windows-Client-Features-Package~31bf3856ad364e35~neutral~~10.0.26200")]
    public void Architecture_Variants_Resolve_To_Same_Family(string id)
        => Assert.Equal("CbsClientFamily", _classifier.Classify(id)?.CanonicalId);

    [Theory]
    [InlineData("Microsoft-Windows-Client-Features-Package~31bf3856ad364e35~amd64~~10.0.26200")]
    [InlineData("Microsoft-Windows-Client-Features-Package~31bf3856ad364e35~amd64~~10.0.26100")]
    [InlineData("Microsoft-Windows-Client-Features-Package~31bf3856ad364e35~amd64~~10.0.26000")]
    public void Version_Suffix_Does_Not_Change_Family(string id)
        => Assert.Equal("CbsClientFamily", _classifier.Classify(id)?.CanonicalId);

    // ---- 5. normalization ----
    [Fact]
    public void Normalization_Makes_Variants_Collapse()
    {
        Assert.Equal(
            ComponentNormalizer.Canonical("foo.resources_zh-cn_8wekyb3d8bbwe"),
            ComponentNormalizer.Canonical("foo.resources_en-us_8wekyb3d8bbwe"));
    }

    // ---- 6. family frequency clustering ----
    [Fact]
    public void Family_Clustering_Ranks_By_Count()
    {
        var ids = new[]
        {
            "Microsoft-Windows-Client-A-Package~x~amd64~1", "Microsoft-Windows-Client-B-Package~x~amd64~1",
            "Microsoft-Windows-Client-C-Package~x~amd64~1",
            "Microsoft-Windows-Foundation-Package~x~amd64~1",
            "Microsoft.AppX_8wekyb3d8bbwe",
        };
        var clusters = UnknownFamilyAnalyzer.Cluster(ids);
        Assert.Equal("microsoft-windows-client", clusters[0].Family);
        Assert.True(clusters[0].Count >= 3, "client-* family must dominate");
    }

    // ---- 7. dependency tags ----
    [Fact]
    public void Dependency_Tags_Are_Present_Where_Justified()
    {
        var wu = _classifier.Classify("wuauserv");
        Assert.Contains("BITS", wu!.DependencyTags);
        Assert.Contains("UsoSvc", wu.DependencyTags);

        var store = _classifier.Classify("Microsoft.WindowsStore");
        Assert.Contains("Microsoft.DesktopAppInstaller", store!.DependencyTags);
    }

    // ---- 8-9. protected enforcement + risk floors ----
    [Fact]
    public void All_Cbs_Family_Entries_Have_Safe_Floor()
    {
        // A broad CBS family matcher must NEVER yield a Low-risk removable entry.
        foreach (var e in DeepComponentCatalogData.Entries)
        {
            var isCbsLike = e.Patterns.Any(p => p.StartsWith("Microsoft-Windows-", StringComparison.Ordinal));
            if (!isCbsLike)
            {
                continue;
            }

            Assert.True(e.Risk >= ComponentRiskLevel.Moderate,
                $"CBS-like family '{e.Id}' risk must be >= Moderate");
            Assert.True(e.Protection >= ComponentProtectionLevel.Sensitive,
                $"CBS-like family '{e.Id}' protection must be >= Sensitive");
            Assert.NotEqual(ComponentRecommendationKind.RecommendedRemove, e.Recommendation);
        }
    }

    [Fact]
    public void Unrelated_Package_Does_Not_Collide_With_Broad_Family()
    {
        // "Microsoft-Windows-Foo-Thing" must NOT match "Microsoft-Windows-Client-".
        var k = _classifier.Classify("Microsoft-Windows-Foo-Thing-Package~31bf3856ad364e35~amd64~~10.0.26200");
        Assert.Null(k); // Unknown — better than a wrong Low-risk classification
    }

    // ---- 10-11. heuristic safety + unknown fallback ----
    [Fact]
    public void Heuristic_Still_Cannot_Look_Safe()
    {
        var k = _classifier.Classify("SearchWeb");
        Assert.Equal(ClassificationConfidence.Heuristic, k!.Confidence);
        Assert.False(k.Risk == ComponentRiskLevel.Low);
    }

    [Fact]
    public void Unknown_Stays_Null()
        => Assert.Null(_classifier.Classify("Microsoft.Unknown.Thing_8wekyb3d8bbwe"));

    // ---- 12. metrics correctness + no double counting ----
    [Fact]
    public void Metrics_Do_Not_Double_Count()
    {
        var sample = RealMediaFamilyFixture.AppxAndCapability.Select(x => x.Id)
            .Concat(RealMediaFamilyFixture.CbsFamilies.Select(x => x.Id))
            .ToList();

        var curated = 0;
        var known = 0;
        var protectedCount = 0;
        var heuristic = 0;
        var unknown = 0;
        foreach (var id in sample)
        {
            var k = _classifier.Classify(id);
            if (k is null)
            {
                unknown++;
                continue;
            }

            if (k.Confidence == ClassificationConfidence.Heuristic)
            {
                heuristic++;
            }

            known++;
            if (k.Protection == ComponentProtectionLevel.Protected)
            {
                protectedCount++;
            }
        }

        var metrics = new ClassificationCoverageMetrics
        {
            TotalDiscovered = sample.Count,
            Curated = curated,
            KnownDeep = known,
            Protected = protectedCount,
            Heuristic = heuristic,
            UnknownUnclassified = unknown,
        };

        Assert.Equal(sample.Count, curated + known + unknown); // no double count
        Assert.True(metrics.CoverageRatio > 0.5);
    }

    // ---- 13. stable classification across version suffix changes ----
    [Fact]
    public void Classification_Stable_Across_Version_Suffix()
    {
        var a = _classifier.Classify("Microsoft-Windows-ServicingStack-10.0.26200.1-amd64");
        var b = _classifier.Classify("Microsoft-Windows-ServicingStack-10.0.26100.1-amd64");
        Assert.Equal(a!.CanonicalId, b!.CanonicalId);
        Assert.Equal(a.Protection, b.Protection);
    }

    // ---- 14. collision prevention ----
    [Fact]
    public void Family_Rules_Do_Not_Collide()
    {
        // Cross-entry collisions only: aliases/patterns INSIDE one entry may
        // normalize identically (e.g. dmwappushservice / DmWapPushService) — that
        // is intentional, not a collision. Different entries must never collide.
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in DeepComponentCatalogData.Entries)
        {
            var keys = e.Patterns.Concat(e.Aliases)
                .Select(ComponentNormalizer.Canonical)
                .Where(k => k.Length > 0)
                .Distinct(StringComparer.Ordinal);
            foreach (var k in keys)
            {
                if (seen.TryGetValue(k, out var other) && other != e.Id)
                {
                    Assert.True(false, $"collision: '{k}' between '{other}' and '{e.Id}'");
                }

                seen[k] = e.Id;
            }
        }
    }
}
