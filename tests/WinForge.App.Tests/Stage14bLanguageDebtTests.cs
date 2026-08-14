using System;
using System.Collections.Generic;
using System.Linq;
using WinForge.Core.ComponentIntelligence;
using WinForge.Core.Models;
using WinForge.Core.Profiles;
using Xunit;

namespace WinForge.App.Tests;

// =====================================================================
// Phase 14 Stage 14.3b — Real Unknown debt reduction (ADR-091):
// six Language capability families, target-language metadata, family
// analyzer granularity, Package_for_* servicing semantics, high-confidence
// real CBS / optional-feature classifications. Safety: classification only,
// KNOWN != REMOVABLE, Gaming never mass-removes languages.
// =====================================================================

public class Stage14bLanguageDebtTests
{
    private readonly DeepComponentClassifier _classifier = new(DeepComponentCatalogData.Entries);
    private readonly GamingProfileEvaluationService _gaming = new();

    private static GamingPolicyInput Input(string rawId, DeepComponentKnowledge k) => new()
    {
        RawIdentity = rawId,
        Source = ComponentCategory.Capability,
        Knowledge = k,
        Extras = new HashSet<GamingExtra>(),
        IsPresent = true,
        SupportedForRemoval = true,
    };

    // ---- 1. all six Language.* families classify ----
    [Theory]
    [InlineData("Language.Basic~~~af-ZA~0.0.1.0", "LanguageBasic")]
    [InlineData("Language.Basic~~~zh-CN~0.0.1.0", "LanguageBasic")]
    [InlineData("Language.Handwriting~~~zh-CN~0.0.1.0", "LanguageHandwriting")]
    [InlineData("Language.TextToSpeech~~~de-DE~0.0.1.0", "LanguageTextToSpeech")]
    [InlineData("Language.OCR~~~fr-FR~0.0.1.0", "LanguageOcr")]
    [InlineData("Language.Fonts~~~ja-JP~0.0.1.0", "LanguageFonts")]
    [InlineData("Language.Speech~~~en-US~0.0.1.0", "LanguageSpeech")]
    public void Language_Families_Classify(string id, string expected)
    {
        var k = _classifier.Classify(id);
        Assert.NotNull(k);
        Assert.Equal(expected, k!.CanonicalId);
        Assert.Equal(ComponentFunctionCategory.Language, k.Function);
        Assert.Equal(ComponentRiskLevel.Moderate, k.Risk);
        Assert.Equal(ComponentRecommendationKind.ProfileDependent, k.Recommendation);
        Assert.Equal(ComponentProtectionLevel.Sensitive, k.Protection);
    }

    [Fact]
    public void Language_Counts_Exactly_Six_Semantic_Families_For_All_Locales()
    {
        var locales = new[] { "af-ZA", "zh-CN", "de-DE", "fr-FR", "ja-JP", "en-US", "es-ES", "ru-RU" };
        var roles = new[] { "Basic", "Handwriting", "TextToSpeech", "OCR", "Fonts", "Speech" };
        var families = new HashSet<string>();
        foreach (var role in roles)
        {
            foreach (var locale in locales)
            {
                var k = _classifier.Classify($"Language.{role}~~~{locale}~0.0.1.0");
                Assert.NotNull(k);
                families.Add(k!.CanonicalId);
            }
        }

        Assert.Equal(6, families.Count);
        Assert.Contains("LanguageBasic", families);
        Assert.Contains("LanguageSpeech", families);
    }

    [Fact]
    public void Different_Locales_Share_One_Semantic_Family()
    {
        var a = _classifier.Classify("Language.Basic~~~af-ZA~0.0.1.0");
        var b = _classifier.Classify("Language.Basic~~~zh-CN~0.0.1.0");
        Assert.Equal(a!.CanonicalId, b!.CanonicalId);
        Assert.Equal("LanguageBasic", a.CanonicalId);
    }

    // ---- 2. technical locale identities remain distinct objects ----
    [Fact]
    public void Language_Metadata_Keeps_Locale_Identities_Distinct()
    {
        var af = LanguageCapabilityMetadata.Parse("Language.Basic~~~af-ZA~0.0.1.0");
        var zh = LanguageCapabilityMetadata.Parse("Language.Basic~~~zh-CN~0.0.1.0");
        Assert.NotNull(af);
        Assert.NotNull(zh);
        Assert.Equal("Basic", af!.Role);
        Assert.Equal("af-ZA", af.Locale);
        Assert.Equal("zh-CN", zh!.Locale);
        Assert.NotEqual(af.Locale, zh.Locale); // distinct inventory objects
    }

    [Fact]
    public void Target_Default_Language_Is_Recognized_But_Never_Auto_Removed()
    {
        Assert.True(LanguageCapabilityMetadata.IsTargetLocale("Language.Basic~~~zh-CN~0.0.1.0", "zh-CN"));
        Assert.False(LanguageCapabilityMetadata.IsTargetLocale("Language.Basic~~~af-ZA~0.0.1.0", "zh-CN"));
        Assert.False(LanguageCapabilityMetadata.IsTargetLocale("Not.A.Language~~~~0.0.1.0", "zh-CN"));

        // Even the TARGET language is classification-only — never an auto-remove.
        var input = Input("Language.Basic~~~zh-CN~0.0.1.0", _classifier.Classify("Language.Basic~~~zh-CN~0.0.1.0")!);
        foreach (var kind in new[] { GamingProfileKind.GamingPc, GamingProfileKind.DedicatedGaming })
        {
            var result = _gaming.Evaluate(new[] { input }, kind, new HashSet<GamingExtra>());
            var item = Assert.Single(result.Items);
            Assert.True(item.Result.IsKeptForCompatibility,
                $"target language must be kept under {kind}");
        }
    }

    // ---- 3. Gaming never mass-removes foreign languages ----
    [Fact]
    public void GamingPc_Does_Not_Mass_Remove_Foreign_Language_Capabilities()
    {
        foreach (var locale in new[] { "af-ZA", "de-DE", "fr-FR", "ja-JP", "ru-RU" })
        {
            foreach (var role in new[] { "Basic", "Handwriting", "TextToSpeech", "OCR", "Fonts", "Speech" })
            {
                var id = $"Language.{role}~~~{locale}~0.0.1.0";
                var input = Input(id, _classifier.Classify(id)!);
                var result = _gaming.Evaluate(new[] { input }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
                var item = Assert.Single(result.Items);
                Assert.True(item.Result.IsKeptForCompatibility, $"{id} must be kept, not removed");
            }
        }
    }

    [Fact]
    public void DedicatedGaming_Does_Not_Bypass_Language_Safety()
    {
        var id = "Language.Handwriting~~~ar-SA~0.0.1.0";
        var input = Input(id, _classifier.Classify(id)!);
        var result = _gaming.Evaluate(new[] { input }, GamingProfileKind.DedicatedGaming, new HashSet<GamingExtra>());
        var item = Assert.Single(result.Items);
        Assert.True(item.Result.IsKeptForCompatibility);
        Assert.Equal(0, result.OptionalChoices);
        Assert.Equal(0, result.RecommendedChanges);
    }

    // ---- 4. family analyzer granularity (real report fixes) ----
    [Fact]
    public void MicrosoftWindows_Capability_Families_Are_No_Longer_Collapsed()
    {
        var console = UnknownFamilyAnalyzer.FamilyOf("Microsoft.Windows.Console.Legacy~~~~0.0.1.0");
        var intel = UnknownFamilyAnalyzer.FamilyOf("Microsoft.Windows.Ethernet.Client.Intel.E1i68x64~~~~0.0.1.0");
        var realtek = UnknownFamilyAnalyzer.FamilyOf("Microsoft.Windows.Ethernet.Client.Realtek.RTK2x64~~~~0.0.1.0");

        Assert.Equal("microsoft.windows.console.legacy", console);
        Assert.Equal("microsoft.windows.ethernet.client.intel", intel);
        Assert.Equal("microsoft.windows.ethernet.client.realtek", realtek);
        Assert.NotEqual(console, intel);
        Assert.NotEqual(intel, realtek);
        Assert.All(new[] { console, intel, realtek },
            f => Assert.NotEqual("microsoft.windows", f));
    }

    [Fact]
    public void Wifi_Client_Subfamily_Is_Extracted()
        => Assert.Equal("microsoft.windows.wifi.client",
            UnknownFamilyAnalyzer.FamilyOf("Microsoft.Windows.Wifi.Client.WlanSvc~~~~0.0.1.0"));

    [Fact]
    public void NonWindows_Short_Capabilities_Keep_Two_Segments()
    {
        Assert.Equal("microsoft.baz", UnknownFamilyAnalyzer.FamilyOf("Microsoft.Baz.Qux_8wekyb3d8bbwe"));
        Assert.Equal("openssh.client", UnknownFamilyAnalyzer.FamilyOf("OpenSSH.Client~~~~0.0.1.0"));
    }

    // ---- 5. Package_for_* servicing semantics ----
    [Fact]
    public void PackageFor_Families_Are_Distinct_And_Semantic()
    {
        var dotnet = UnknownFamilyAnalyzer.FamilyOf("Package_for_DotNetRollup_481~31bf3856ad364e35~amd64~~10.0.26200");
        var kb = UnknownFamilyAnalyzer.FamilyOf("Package_for_KB5054156~31bf3856ad364e35~amd64~~10.0.26200");
        var rollupFix = UnknownFamilyAnalyzer.FamilyOf("Package_for_RollupFix~31bf3856ad364e35~amd64~~10.0.26200");

        Assert.Equal("package-for-dotnetrollup", dotnet);
        Assert.Equal("package-for-kb", kb);
        Assert.Equal("package-for-rollupfix", rollupFix);
        Assert.NotEqual(dotnet, kb);
        Assert.NotEqual(kb, rollupFix);
        Assert.NotEqual(dotnet, rollupFix);
    }

    [Theory]
    [InlineData("Package_for_DotNetRollup_481~31bf3856ad364e35~amd64~~10.0.26200", "PackageForDotNetRollup")]
    [InlineData("Package_for_KB5054156~31bf3856ad364e35~amd64~~10.0.26200", "PackageForKb")]
    [InlineData("Package_for_RollupFix~31bf3856ad364e35~amd64~~10.0.26200", "PackageForRollupFix")]
    public void PackageFor_Identities_Classify_Conservatively(string id, string expected)
    {
        var k = _classifier.Classify(id);
        Assert.NotNull(k);
        Assert.Equal(expected, k!.CanonicalId);
        Assert.True(k.Risk >= ComponentRiskLevel.High, "servicing packages must never be Low/Moderate");
        Assert.True(k.Protection >= ComponentProtectionLevel.Sensitive);
    }

    [Fact]
    public void PackageFor_Kb_Is_Kept_By_Gaming_Not_Removed()
    {
        var id = "Package_for_KB5054156~31bf3856ad364e35~amd64~~10.0.26200";
        var input = Input(id, _classifier.Classify(id)!);
        var result = _gaming.Evaluate(new[] { input }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        var item = Assert.Single(result.Items);
        Assert.True(item.Result.IsKeptForCompatibility);
    }

    // ---- 6. high-confidence real CBS families ----
    [Theory]
    [InlineData("Microsoft-Windows-Licenses-Package~31bf3856ad364e35~amd64~~10.0.26200", "CbsLicenses",
        ComponentRiskLevel.Critical, ComponentRecommendationKind.RequiredKeep, ComponentProtectionLevel.Protected)]
    [InlineData("Microsoft-Windows-FodMetadataServicing-Package~31bf3856ad364e35~amd64~~10.0.26200", "CbsFodMetadataServicing",
        ComponentRiskLevel.Critical, ComponentRecommendationKind.RequiredKeep, ComponentProtectionLevel.Protected)]
    [InlineData("Microsoft-Windows-Kernel-Package~31bf3856ad364e35~amd64~~10.0.26200", "CbsKernel",
        ComponentRiskLevel.Critical, ComponentRecommendationKind.RequiredKeep, ComponentProtectionLevel.Protected)]
    public void Critical_Cbs_Families_Are_Critical_Protected_RequiredKeep(
        string id, string expected, ComponentRiskLevel risk, ComponentRecommendationKind rec, ComponentProtectionLevel prot)
    {
        var k = _classifier.Classify(id);
        Assert.NotNull(k);
        Assert.Equal(expected, k!.CanonicalId);
        Assert.Equal(risk, k.Risk);
        Assert.Equal(rec, k.Recommendation);
        Assert.Equal(prot, k.Protection);
    }

    [Fact]
    public void DirectX_Database_Stays_Kept_For_Gaming()
    {
        var id = "Microsoft-OneCore-DirectX-Database-Package~31bf3856ad364e35~amd64~~10.0.26200";
        var k = _classifier.Classify(id);
        Assert.NotNull(k);
        Assert.Equal("CbsOneCoreDirectX", k!.CanonicalId);
        Assert.Equal(ComponentProfileTag.GamingRelevant, k.ProfileTag);

        var input = Input(id, k);
        var result = _gaming.Evaluate(new[] { input }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        var item = Assert.Single(result.Items);
        Assert.True(item.Result.IsKeptForCompatibility, "DirectX runtime must stay kept for Gaming");
    }

    [Fact]
    public void OpenSsh_Client_Package_Is_ProfileDependent_Not_Removable()
    {
        var id = "OpenSSH-Client-Package~31bf3856ad364e35~amd64~~10.0.26200";
        var k = _classifier.Classify(id);
        Assert.NotNull(k);
        Assert.Equal("CbsOpenSshClient", k!.CanonicalId);
        Assert.Equal(ComponentRecommendationKind.ProfileDependent, k.Recommendation);
        Assert.Equal(ComponentFunctionCategory.Developer, k.Function);
    }

    [Fact]
    public void Vbscript_Is_LegacyCompatibility_ProfileDependent()
    {
        var id = "Microsoft-Windows-VBSCRIPT-Package~31bf3856ad364e35~amd64~~10.0.26200";
        var k = _classifier.Classify(id);
        Assert.NotNull(k);
        Assert.Equal("CbsVbscript", k!.CanonicalId);
        Assert.Equal(ComponentFunctionCategory.LegacyCompatibility, k.Function);
        Assert.Equal(ComponentRecommendationKind.ProfileDependent, k.Recommendation);
        Assert.Equal(ComponentProtectionLevel.Sensitive, k.Protection);
    }

    [Fact]
    public void SenseClient_And_Hello_Are_Security_Conservative()
    {
        var sense = _classifier.Classify("Microsoft-Windows-SenseClient-Package~31bf3856ad364e35~amd64~~10.0.26200");
        Assert.NotNull(sense);
        Assert.Equal(ComponentFunctionCategory.Security, sense!.Function);
        Assert.Equal(ComponentRiskLevel.High, sense.Risk);

        var hello = _classifier.Classify("Microsoft-Windows-Hello-Face-Package~31bf3856ad364e35~amd64~~10.0.26200");
        Assert.NotNull(hello);
        Assert.Equal("CbsHello", hello!.CanonicalId);
        Assert.Equal(ComponentFunctionCategory.Security, hello.Function);
        Assert.Equal(ComponentRecommendationKind.ProfileDependent, hello.Recommendation);
    }

    [Fact]
    public void Notepad_Classifies_But_Never_Enables_Removal()
    {
        var k = _classifier.Classify("Microsoft-Windows-Notepad-Package~31bf3856ad364e35~amd64~~10.0.26200");
        Assert.NotNull(k);
        Assert.Equal("CbsNotepad", k!.CanonicalId);
        Assert.Equal(ComponentRecommendationKind.ProfileDependent, k.Recommendation);
        Assert.Equal(ComponentRiskLevel.Moderate, k.Risk);
    }

    // ---- 7. small optional features — never automatic Gaming removal ----
    [Theory]
    [InlineData("Client-DeviceLockdown")]
    [InlineData("Client-EmbeddedBootExp")]
    [InlineData("Client-EmbeddedLogon")]
    [InlineData("Client-EmbeddedShellLauncher")]
    [InlineData("Client-KeyboardFilter")]
    [InlineData("Client-UnifiedWriteFilter")]
    public void Embedded_Lockdown_Features_Never_Become_LowRisk_Auto_Removals(string id)
    {
        var k = _classifier.Classify(id);
        Assert.NotNull(k);
        Assert.Equal(ComponentRiskLevel.High, k!.Risk);

        var input = Input(id, k);
        foreach (var kind in new[] { GamingProfileKind.GamingPc, GamingProfileKind.DedicatedGaming })
        {
            var result = _gaming.Evaluate(new[] { input }, kind, new HashSet<GamingExtra>());
            var item = Assert.Single(result.Items);
            Assert.False(item.Result.IsAutoRecommended, $"{id} must never auto-remove under {kind}");
            Assert.False(item.Result.IsOptionalSuggestion, $"{id} must not even be optional under {kind}");
        }
    }

    [Fact]
    public void ProjFs_And_AzureArc_Are_ProfileDependent_High()
    {
        foreach (var (id, expected) in new[]
        {
            ("Client-ProjFS", "ClientProjFs"),
            ("AzureArcSetup", "AzureArcSetup"),
        })
        {
            var k = _classifier.Classify(id);
            Assert.NotNull(k);
            Assert.Equal(expected, k!.CanonicalId);
            Assert.Equal(ComponentRiskLevel.High, k.Risk);
            Assert.Equal(ComponentRecommendationKind.ProfileDependent, k.Recommendation);
        }
    }

    [Fact]
    public void Braille_And_WirelessDisplay_Are_Accessibility_Media_ProfileDependent()
    {
        var braille = _classifier.Classify("Accessibility.Braille~~~~0.0.1.0");
        Assert.NotNull(braille);
        Assert.Equal(ComponentFunctionCategory.Accessibility, braille!.Function);

        var wd = _classifier.Classify("App.WirelessDisplay.Connect~~~~0.0.1.0");
        Assert.NotNull(wd);
        Assert.Equal(ComponentFunctionCategory.Media, wd!.Function);
        Assert.Equal(ComponentRecommendationKind.ProfileDependent, wd.Recommendation);
    }

    // ---- 8. catalog remains collision-free and heuristic count stays zero ----
    [Fact]
    public void Expanded_Catalog_Has_No_Cross_Entry_Collisions()
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in DeepComponentCatalogData.Entries)
        {
            foreach (var key in e.Patterns.Concat(e.Aliases)
                         .Select(ComponentNormalizer.Canonical)
                         .Where(k => k.Length > 0))
            {
                Assert.False(seen.TryGetValue(key, out var other) && other != e.Id,
                    $"collision: '{key}' between '{other}' and '{e.Id}'");
                seen[key] = e.Id;
            }
        }
    }

    [Fact]
    public void No_Heuristic_Entries_Were_Added_For_Coverage()
    {
        // Exactly ONE heuristic entry exists in the whole catalog (pre-existing,
        // from Stage 14.1). Stage 14.3b added ZERO heuristic entries — debt is
        // reduced with real semantic knowledge only, never weak heuristics.
        Assert.Equal(1, DeepComponentCatalogData.Entries.Count(e => e.Confidence == ClassificationConfidence.Heuristic));
    }
}
