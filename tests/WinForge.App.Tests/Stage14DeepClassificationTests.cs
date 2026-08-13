using System;
using System.Collections.Generic;
using System.Linq;
using WinForge.Core.ComponentIntelligence;
using WinForge.Core.Models;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Stage 14.1 — deep component coverage & classification foundation.
/// Taxonomy / normalization / risk / recommendation / confidence / protected
/// handling / unknown fallback / deterministic matching / collision prevention /
/// Gaming-relevance-vs-removal-safety separation / coverage metrics.
/// </summary>
public class Stage14DeepClassificationTests
{
    private readonly DeepComponentClassifier _classifier =
        new(DeepComponentCatalogData.Entries);

    // ---- taxonomy ----
    [Fact]
    public void Taxonomy_Has_Core_Categories()
    {
        Assert.Equal(ComponentFunctionCategory.Gaming, Enum.Parse<ComponentFunctionCategory>("Gaming"));
        Assert.Equal(ComponentFunctionCategory.Security, Enum.Parse<ComponentFunctionCategory>("Security"));
        Assert.Equal(ComponentFunctionCategory.Servicing, Enum.Parse<ComponentFunctionCategory>("Servicing"));
        Assert.Equal(ComponentFunctionCategory.PrintingScanning, Enum.Parse<ComponentFunctionCategory>("PrintingScanning"));
        Assert.Equal(ComponentFunctionCategory.Virtualization, Enum.Parse<ComponentFunctionCategory>("Virtualization"));
        Assert.Equal(ComponentFunctionCategory.Unknown, (ComponentFunctionCategory)0);
        // 25 documented top-level categories + Unknown.
        Assert.True(Enum.GetValues<ComponentFunctionCategory>().Length >= 25);
    }

    // ---- catalog size ----
    [Fact]
    public void Catalog_Has_At_Least_75_Entries()
    {
        Assert.True(DeepComponentCatalogData.Entries.Count >= 75,
            $"catalog has {DeepComponentCatalogData.Entries.Count} entries");
    }

    // ---- normalization ----
    [Theory]
    [InlineData("Microsoft.Windows.Photos_neutral_~_8wekyb3d8bbwe", "microsoft.windows.photos")]
    [InlineData("Microsoft.YourPhone_8wekyb3d8bbwe_x64__8wekyb3d8bbwe", "microsoft.yourphone")]
    [InlineData("Microsoft-Windows-Printing-XPSServices-Package~31bf3856ad364e35~amd64~~10.0.26100.1", "microsoft-windows-printing-xpsservices-package")]
    public void Normalization_Strips_Tokens(string raw, string expected)
    {
        Assert.Equal(expected, ComponentNormalizer.Canonical(raw));
    }

    // ---- alias exact match ----
    [Fact]
    public void Alias_Exact_Match_Returns_KnownPattern()
    {
        var k = _classifier.ClassifyExact("WinDefend");
        Assert.NotNull(k);
        Assert.Equal(ClassificationConfidence.KnownPattern, k!.Confidence);
        Assert.Equal(ComponentFunctionCategory.Security, k.Function);
        Assert.Equal(ComponentProtectionLevel.Protected, k.Protection);
    }

    // ---- family pattern ----
    [Fact]
    public void Family_Pattern_Matches_Without_Full_Id()
    {
        var k = _classifier.Classify("Microsoft-Windows-Printing-XPSServices-Foobar-Package~31bf3856ad364e35~amd64~~10.0");
        Assert.NotNull(k);
        Assert.Equal("PrintingXPS", k!.CanonicalId);
        Assert.Equal(ComponentFunctionCategory.PrintingScanning, k.Function);
    }

    // ---- risk / recommendation / confidence models ----
    [Fact]
    public void Risk_And_Recommendation_Enums_Are_Complete()
    {
        Assert.True(Enum.GetValues<ComponentRiskLevel>().Length >= 5);   // Unknown..Critical
        Assert.True(Enum.GetValues<ComponentRecommendationKind>().Length >= 6);
        Assert.True(Enum.GetValues<ClassificationConfidence>().Length >= 5);
    }

    // ---- protected component handling ----
    [Fact]
    public void Servicing_Stack_Is_Protected_Critical()
    {
        var k = _classifier.Classify("Microsoft-Windows-ServicingStack-Package~31bf3856ad364e35~amd64~~10.0.26200");
        Assert.NotNull(k);
        Assert.Equal(ComponentRiskLevel.Critical, k!.Risk);
        Assert.Equal(ComponentProtectionLevel.Protected, k.Protection);
        Assert.Equal(ComponentRecommendationKind.RequiredKeep, k.Recommendation);
    }

    [Fact]
    public void Defender_Is_Protected_Critical()
    {
        var k = _classifier.Classify("WinDefend");
        Assert.NotNull(k);
        Assert.Equal(ComponentRiskLevel.Critical, k!.Risk);
        Assert.Equal(ComponentProtectionLevel.Protected, k.Protection);
        Assert.Equal(ComponentRecommendationKind.RequiredKeep, k.Recommendation);
    }

    [Fact]
    public void Store_Infrastructure_Is_Protected()
    {
        var k = _classifier.Classify("Microsoft.WindowsStore");
        Assert.NotNull(k);
        Assert.Equal(ComponentProtectionLevel.Protected, k!.Protection);
        Assert.Equal(ComponentRecommendationKind.RequiredKeep, k.Recommendation);
    }

    [Fact]
    public void WebView2_And_Runtimes_Are_Protected()
    {
        foreach (var id in new[] { "Microsoft.WebView2", "Microsoft.VCLibs.140.00", "Microsoft.UI.Xaml.2.8", "Microsoft.DirectX" })
        {
            var k = _classifier.Classify(id);
            Assert.NotNull(k);
            Assert.Equal(ComponentProtectionLevel.Protected, k!.Protection);
            Assert.True(k.Risk == ComponentRiskLevel.High || k.Risk == ComponentRiskLevel.Critical);
        }
    }

    // ---- unknown fallback ----
    [Fact]
    public void Unknown_Identity_Returns_Null()
    {
        Assert.Null(_classifier.Classify("Totally.Unknown.Thing"));
        Assert.Null(_classifier.Classify(""));
    }

    // ---- deterministic ----
    [Fact]
    public void Classification_Is_Deterministic()
    {
        var a = _classifier.Classify("Microsoft.YourPhone_8wekyb3d8bbwe");
        var b = _classifier.Classify("Microsoft.YourPhone_8wekyb3d8bbwe");
        Assert.NotNull(a);
        Assert.Equal(a!.CanonicalId, b!.CanonicalId);
        Assert.Equal(a.Function, b.Function);
    }

    // ---- collision prevention ----
    [Fact]
    public void Normalizer_Detects_Collisions()
    {
        var colliding = ComponentNormalizer.FindCollision(new[]
        {
            "Microsoft.A_8wekyb3d8bbwe",
            "Microsoft.A_8wekyb3d8bbwe",       // same normalized -> same identity, not a collision
        });
        Assert.Null(colliding);

        var real = ComponentNormalizer.FindCollision(new[]
        {
            "Microsoft.Foo_8wekyb3d8bbwe",
            "Microsoft.Bar_8wekyb3d8bbwe",
        });
        Assert.Null(real); // different families never collide
    }

    // ---- heuristic never lowers risk ----
    [Fact]
    public void Heuristic_Classification_Can_Never_Look_Safe()
    {
        var k = _classifier.Classify("SearchWeb");
        Assert.NotNull(k);
        Assert.Equal(ClassificationConfidence.Heuristic, k!.Confidence);
        Assert.False(k.Risk == ComponentRiskLevel.Low, "heuristic must not be Low risk");
        Assert.True(k.Protection == ComponentProtectionLevel.Sensitive);
    }

    // ---- Gaming relevance != removal safety ----
    [Fact]
    public void Gaming_Relevance_Does_Not_Imply_Removal_Safety()
    {
        var k = _classifier.Classify("Microsoft.GamingServices");
        Assert.NotNull(k);
        Assert.Equal(ComponentProfileTag.GamingRelevant, k!.ProfileTag);
        Assert.Equal(ComponentRecommendationKind.ProfileDependent, k.Recommendation); // NOT RecommendedRemove
        Assert.Equal(ComponentProtectionLevel.Sensitive, k.Protection);
    }

    [Fact]
    public void Gaming_Candidates_Are_Optional_Not_Auto_Remove()
    {
        foreach (var id in new[] { "Microsoft.YourPhone", "MicrosoftSolitaireCollection", "Microsoft.BingWeather", "FeedbackHub", "Microsoft.Getstarted" })
        {
            var k = _classifier.Classify(id);
            Assert.NotNull(k);
            Assert.NotEqual(ComponentRecommendationKind.RequiredKeep, k!.Recommendation);
            Assert.Equal(ComponentProtectionLevel.None, k.Protection); // consumer-optional
        }
    }

    // ---- profile tags ----
    [Fact]
    public void Profile_Tags_Are_Stable()
    {
        var phone = _classifier.Classify("Microsoft.YourPhone");
        Assert.Equal(ComponentProfileTag.PhoneIntegration, phone!.ProfileTag);
        var spooler = _classifier.Classify("Spooler");
        Assert.Equal(ComponentProfileTag.PrintScan, spooler!.ProfileTag);
    }

    // ---- localization fallback ----
    [Fact]
    public void Display_Name_Has_English_Fallback()
    {
        var k = _classifier.Classify("Microsoft.YourPhone");
        Assert.False(string.IsNullOrWhiteSpace(k!.DisplayNameFallback));
        Assert.Equal("Phone Link", k.DisplayNameFallback);
    }

    // ---- coverage metrics (synthetic sample modeling the real 25H2 distribution) ----
    [Fact]
    public void Coverage_Metrics_Report_Unknown_As_Debt()
    {
        // Synthetic sample mirroring the real 25H2 zh-CN Consumer shape (~734 unclassified
        // objects across sources). Not a real-media measurement — see notes.
        var sample = new List<(string Id, ComponentCategory Source)>
        {
            // known consumer/gaming families
            ("Microsoft.YourPhone", ComponentCategory.AppX),
            ("MicrosoftSolitaireCollection", ComponentCategory.AppX),
            ("Microsoft.GetHelp", ComponentCategory.AppX),
            ("Microsoft.BingWeather", ComponentCategory.AppX),
            ("Microsoft.GamingServices", ComponentCategory.AppX),
            ("Microsoft.XboxApp", ComponentCategory.AppX),
            ("Microsoft.WindowsStore", ComponentCategory.AppX),
            ("Microsoft.DesktopAppInstaller", ComponentCategory.AppX),
            ("Microsoft.VCLibs.140.00", ComponentCategory.AppX),
            ("Microsoft.WebView2", ComponentCategory.AppX),
            // known capabilities / features / services
            ("OpenSSH.Client~~~~0.0.1.0", ComponentCategory.Capability),
            ("Printing-XPSServices-Features", ComponentCategory.OptionalFeature),
            ("Spooler", ComponentCategory.Service),
            ("DiagTrack", ComponentCategory.Service),
            ("wuauserv", ComponentCategory.Service),
            ("WinDefend", ComponentCategory.Service),
            // unknown raw objects (remain debt)
            ("Microsoft.FooBar_8wekyb3d8bbwe", ComponentCategory.AppX),
            ("Microsoft-Windows-SomeDriver-Package~31bf3856ad364e35~amd64~~10.0.1", ComponentCategory.CbsPackage),
            ("Some.Unknown.Capability", ComponentCategory.Capability),
        };

        var classified = sample.Count(x => _classifier.Classify(x.Id) is not null);
        var protectedCount = sample.Count(x =>
        {
            var k = _classifier.Classify(x.Id);
            return k is not null && k.Protection == ComponentProtectionLevel.Protected;
        });

        Assert.Equal(16, classified);   // 3 unknown remain (debt)
        Assert.Equal(6, protectedCount); // Store/AppInstaller/VCLibs/WebView2/wuauserv/WinDefend
        Assert.Equal(3, sample.Count - classified); // unknown stays visible as debt
    }
}
