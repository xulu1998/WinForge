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
using WinForge.Infrastructure.Profiles;
using Xunit;

namespace WinForge.App.Tests;

// =====================================================================
// Phase 14 Stage 14.3 — Elevated real inventory validation + Gaming Profile 2.0
// (ADR-087..090). Tests: real-derived fixture, exact coverage accounting, per-
// source breakdown, Unknown family clustering, Gaming PC / Dedicated Gaming
// policies, extras behavior, safety gates, manual overrides, dependency/runtime/
// Store preservation, deterministic reasons, profile summary counts, no placebo.
// =====================================================================

public static class GamingKnowledge
{
    public static DeepComponentKnowledge K(
        string canonicalId,
        ComponentFunctionCategory function,
        ComponentRiskLevel risk,
        ComponentRecommendationKind recommendation,
        ComponentProtectionLevel protection,
        ComponentProfileTag profileTag,
        ClassificationConfidence confidence,
        params string[] dependencyTags) => new()
        {
            CanonicalId = canonicalId,
            DisplayNameFallback = canonicalId,
            Function = function,
            Risk = risk,
            Recommendation = recommendation,
            Protection = protection,
            ProfileTag = profileTag,
            Confidence = confidence,
            DependencyTags = dependencyTags,
        };
}

/// <summary>Deterministic real-inventory fixtures (identities recorded from the real 25H2 media).</summary>
public static class RealInventoryFixture
{
    public sealed class TestRawItem : RawInventoryItem
    {
    }

    public static TestRawItem Raw(ComponentCategory source, string identity) => new()
    {
        Category = source,
        RawIdentity = identity,
        DisplayName = identity,
    };

    /// <summary>The production classifier over the production deep catalog.</summary>
    public static readonly DeepComponentClassifier Classifier =
        new(DeepComponentCatalogData.Entries);
}

/// <summary>A fake gaming-evaluation subject (App row) for profile-level tests.</summary>
public sealed class FakeGamingSubject : IGamingEvaluationSubject, IRecommendationSubject
{
    public string LogicalId { get; init; } = string.Empty;
    public string RawIdentity { get; init; } = string.Empty;
    public ComponentCategory SourceCategory { get; init; } = ComponentCategory.AppX;
    public DeepComponentKnowledge? DeepKnowledge { get; init; }
    public bool IsPresent { get; init; } = true;
    public bool IsApplySupported { get; init; } = true;
    public bool WasOverridden { get; init; }
    public string DisplayName { get; init; } = string.Empty;

    // ---- IRecommendationSubject (required for the profile selector) ----
    public OptimizationTab Tab => OptimizationTab.Apps;
    public bool IsSelectable => true;
    public bool IsSelected { get; set; }
    public bool HasConflict => false;
    public EffectiveRecommendation Effective =>
        EffectiveRecommendation.FromDefault(RecommendationLevel.OptionalRemove);
    public string RecommendationCaption => string.Empty;
    public string ReasonText => string.Empty;
    public string ConflictText => string.Empty;
    public string SelectionOriginText => string.Empty;
    public void RefreshRecommendation(RecommendationContextService context) { }
    public void SetSelectedForAdoption(bool selected) => IsSelected = selected;
}

// =====================================================================
// 1. REAL-DERIVED REGRESSION FIXTURE (Part A §5)
// =====================================================================
public class Stage14RealFixtureTests
{
    private readonly DeepComponentClassifier _classifier = RealInventoryFixture.Classifier;

    private static IEnumerable<FixtureEntry> LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "25H2-Pro-zhCN-component-families.json");
        Assert.True(File.Exists(path), $"fixture missing at {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            yield return new FixtureEntry
            {
                Source = e.GetProperty("source").GetString()!,
                Family = e.GetProperty("family").GetString()!,
                Representative = e.GetProperty("representative").GetString()!,
                Classification = e.GetProperty("classification").GetString()!,
                CanonicalId = e.TryGetProperty("canonicalId", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : null,
            };
        }
    }

    private sealed class FixtureEntry
    {
        public string Source { get; init; } = string.Empty;
        public string Family { get; init; } = string.Empty;
        public string Representative { get; init; } = string.Empty;
        public string Classification { get; init; } = string.Empty;
        public string? CanonicalId { get; init; }
    }

    [Fact]
    public void Fixture_Loads_With_All_Sources_Represented()
    {
        var entries = LoadFixture().ToList();
        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.Source == "AppX");
        Assert.Contains(entries, e => e.Source == "Capability");
        Assert.Contains(entries, e => e.Source == "OptionalFeature");
        Assert.Contains(entries, e => e.Source == "CbsPackage");
        Assert.Contains(entries, e => e.Classification == "Unknown");
    }

    [Theory]
    [MemberData(nameof(KnownFixtures))]
    public void Known_Fixture_Representatives_Classify_Stably(string representative, string expectedCanonicalId)
    {
        var k = _classifier.Classify(representative);
        Assert.NotNull(k);
        Assert.Equal(expectedCanonicalId, k!.CanonicalId);
        // Version-stripped fixture must be stable across rebuilds (deterministic).
        Assert.Equal(k.CanonicalId, _classifier.Classify(representative)!.CanonicalId);
    }

    public static IEnumerable<object[]> KnownFixtures()
        => LoadFixture()
            .Where(e => e.Classification is "Curated" or "KnownDeep")
            .Select(e => new object[] { e.Representative, e.CanonicalId! });

    [Theory]
    [MemberData(nameof(UnknownFixtures))]
    public void Unknown_Fixture_Representatives_Stay_Unclassified(string representative)
    {
        Assert.Null(_classifier.Classify(representative));
    }

    public static IEnumerable<object[]> UnknownFixtures()
        => LoadFixture()
            .Where(e => e.Classification == "Unknown")
            .Select(e => new object[] { e.Representative });

    [Fact]
    public void Fixture_Representatives_Contain_No_Host_Paths_Or_Temp_Mounts()
    {
        foreach (var e in LoadFixture())
        {
            Assert.DoesNotContain("C:", e.Representative, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("mount", e.Representative, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Users\\", e.Representative, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("10.0.26", e.Representative, StringComparison.Ordinal); // no build versions
        }
    }

    [Fact]
    public void Fixture_Families_Match_The_Analyzer_Output()
    {
        foreach (var e in LoadFixture())
        {
            // FamilyOf must reproduce the recorded family from the stripped identity.
            Assert.Equal(e.Family, UnknownFamilyAnalyzer.FamilyOf(e.Representative));
        }
    }
}

// =====================================================================
// 2. EXACT COVERAGE ACCOUNTING (Part A §3, §18)
// =====================================================================
public class Stage14CoverageAccountingTests
{
    private static ComponentInventory Raw(params (ComponentCategory Source, string Id)[] items)
    {
        var categories = items
            .GroupBy(x => x.Source)
            .Select(g => new CategoryDiscoveryResult
            {
                Category = g.Key,
                Status = InventoryStatus.Success,
                Items = g.Select(x => RealInventoryFixture.Raw(g.Key, x.Id)).Cast<IRawInventoryItem>().ToList(),
            })
            .ToList();
        return new ComponentInventory { Discovered = true, Categories = categories };
    }

    private static ComponentInventory Classified(params (string Id, ComponentClassification Class)[] entries)
    {
        var list = entries.Select(e => new ComponentInventoryEntry
        {
            Classification = e.Class,
            RawItems = new List<IRawInventoryItem> { RealInventoryFixture.Raw(ComponentCategory.AppX, e.Id) },
        }).ToList();
        return new ComponentInventory { Entries = list };
    }

    [Fact]
    public void Exact_Accounting_No_Double_Count_And_Reconciles()
    {
        var raw = Raw(
            (ComponentCategory.AppX, "Microsoft.YourPhone_8wekyb3d8bbwe"), // Curated (matcher)
            (ComponentCategory.AppX, "Microsoft.GetHelp_8wekyb3d8bbwe"),   // Curated
            (ComponentCategory.AppX, "Microsoft.BingNews_8wekyb3d8bbwe"),  // KnownDeep (News)
            (ComponentCategory.Capability, "OpenSSH.Client~~~~0.0.1.0"),   // KnownDeep (OpenSSHClient)
            (ComponentCategory.CbsPackage, "Microsoft-Windows-ServicingStack-10.0.26200.1-amd64"), // KnownDeep + matcher Protected
            (ComponentCategory.CbsPackage, "Microsoft-Windows-Defender-Services-Package~31bf3856ad364e35~amd64~~10.0.26200"), // KnownDeep + deep Protected
            (ComponentCategory.AppX, "Contoso.Unknown.Thing"));            // Unknown

        var classified = Classified(
            ("Microsoft.YourPhone_8wekyb3d8bbwe", ComponentClassification.Curated),
            ("Microsoft.GetHelp_8wekyb3d8bbwe", ComponentClassification.Curated),
            ("Microsoft-Windows-ServicingStack-10.0.26200.1-amd64", ComponentClassification.Protected));

        var metrics = CoverageAccountingService.Compute(raw, classified, RealInventoryFixture.Classifier);

        Assert.Equal(7, metrics.TotalDiscovered);
        Assert.Equal(2, metrics.Curated);
        Assert.Equal(4, metrics.KnownDeep);   // BingNews, OpenSSH, ServicingStack, Defender
        Assert.Equal(0, metrics.Heuristic);
        Assert.Equal(1, metrics.UnknownUnclassified);
        // No double counting: exclusive buckets sum to total.
        Assert.Equal(metrics.TotalDiscovered,
            metrics.Curated + metrics.KnownDeep + metrics.Heuristic + metrics.UnknownUnclassified);
        // Protected is a property count (subset of known): ServicingStack + Defender.
        Assert.Equal(2, metrics.Protected);
        Assert.Equal(1, metrics.MatcherProtected);

        // Per-source slices.
        Assert.Equal(4, metrics.BySource[ComponentCategory.AppX].Total);
        Assert.Equal(2, metrics.BySource[ComponentCategory.AppX].Curated);
        Assert.Equal(1, metrics.BySource[ComponentCategory.AppX].Known);
        Assert.Equal(1, metrics.BySource[ComponentCategory.AppX].Unknown);
        Assert.Equal(2, metrics.BySource[ComponentCategory.CbsPackage].Total);
        Assert.Equal(2, metrics.BySource[ComponentCategory.CbsPackage].Known);
        Assert.Equal(2, metrics.BySource[ComponentCategory.CbsPackage].Protected);
        Assert.Equal(1, metrics.BySource[ComponentCategory.Capability].Total);
        Assert.Equal(1, metrics.BySource[ComponentCategory.Capability].Known);
        Assert.Equal(0, metrics.BySource[ComponentCategory.Capability].Unknown);

        // Exact knowledge coverage (heuristic excluded).
        Assert.Equal(6.0 / 7.0, metrics.CoverageRatio, 6);
    }

    [Fact]
    public void Heuristic_Classification_Is_Counted_Separately_Never_As_Known()
    {
        var heuristicCatalog = new List<DeepCatalogEntry>
        {
            new()
            {
                Id = "HeurThing",
                Aliases = new[] { "Heur.Thing" },
                Function = ComponentFunctionCategory.Productivity,
                Risk = ComponentRiskLevel.Low,
                Recommendation = ComponentRecommendationKind.OptionalRemove,
                Protection = ComponentProtectionLevel.None,
                ProfileTag = ComponentProfileTag.ConsumerContent,
                Confidence = ClassificationConfidence.Heuristic,
            },
        };
        var classifier = new DeepComponentClassifier(heuristicCatalog);

        var raw = Raw((ComponentCategory.AppX, "Heur.Thing"));
        var metrics = CoverageAccountingService.Compute(raw, Classified(), classifier);

        Assert.Equal(1, metrics.TotalDiscovered);
        Assert.Equal(0, metrics.KnownDeep);
        Assert.Equal(1, metrics.Heuristic);
        Assert.Equal(0, metrics.UnknownUnclassified);
        // Heuristic must NOT inflate knowledge coverage.
        Assert.Equal(0.0, metrics.CoverageRatio, 6);
        Assert.Equal(1.0, metrics.TotalClassifiedRatio, 6);
        Assert.Equal("Heuristic", metrics.Buckets["Heur.Thing"]);
    }

    [Fact]
    public void Unknown_Family_Clustering_Ranks_Real_Families()
    {
        var unknowns = new[]
        {
            "Microsoft-Windows-FooBar-Package~31bf3856ad364e35~amd64~~10.0.26200",
            "Microsoft-Windows-FooBar-Wow64-Package~31bf3856ad364e35~wow64~~10.0.26200",
            "Microsoft-Windows-FooBar-Neutral-Package~31bf3856ad364e35~neutral~~10.0.26200",
            "Microsoft.Baz.Qux_8wekyb3d8bbwe",
            "Microsoft.Baz.Zap_8wekyb3d8bbwe",
        };
        var clusters = UnknownFamilyAnalyzer.Cluster(unknowns);

        Assert.Equal("microsoft-windows-foobar", clusters[0].Family);
        Assert.Equal(3, clusters[0].Count);
        Assert.Equal("microsoft.baz", clusters[1].Family);
        Assert.Equal(2, clusters[1].Count);
    }
}

// =====================================================================
// 3. GAMING PROFILE 2.0 — POLICY + SAFETY GATE (Part C §7-§12)
// =====================================================================
public class Stage14GamingPolicyTests
{
    private readonly GamingProfileEvaluationService _service = new();

    private static GamingPolicyInput Input(
        string rawId,
        DeepComponentKnowledge k,
        bool supported = true,
        bool overridden = false,
        params GamingExtra[] extras) => new()
        {
            RawIdentity = rawId,
            Source = ComponentCategory.AppX,
            Knowledge = k,
            Extras = new HashSet<GamingExtra>(extras),
            IsPresent = true,
            SupportedForRemoval = supported,
            HasUserOverride = overridden,
        };

    // ---- Gaming PC: automatic LOW-RISK consumer removals ----
    [Theory]
    [InlineData("PhoneLink", ComponentProfileTag.PhoneIntegration)]
    [InlineData("GetHelp", ComponentProfileTag.ConsumerContent)]
    [InlineData("Solitaire", ComponentProfileTag.ConsumerContent)]
    public void GamingPc_Auto_Removes_LowRisk_Consumer_Content(string id, ComponentProfileTag tag)
    {
        var input = Input(id, GamingKnowledge.K(id, ComponentFunctionCategory.Productivity,
            ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
            ComponentProtectionLevel.None, tag, ClassificationConfidence.Curated));
        var decision = _service.EvaluateItem(input, GamingProfileKind.GamingPc);
        Assert.NotNull(decision);
        Assert.Equal(GamingVerdict.AutoRemoveCandidate, decision!.Verdict);
        Assert.StartsWith("Profile.Reason.Gaming.Remove.", decision.ReasonKey);
    }

    [Fact]
    public void GamingPc_Auto_Removes_Web_Search_Integration()
    {
        var input = Input("SearchWeb", GamingKnowledge.K("SearchWeb", ComponentFunctionCategory.Search,
            ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
            ComponentProtectionLevel.None, ComponentProfileTag.None, ClassificationConfidence.Curated));
        var decision = _service.EvaluateItem(input, GamingProfileKind.GamingPc);
        Assert.NotNull(decision);
        Assert.Equal(GamingVerdict.AutoRemoveCandidate, decision!.Verdict);
        Assert.Equal("Profile.Reason.Gaming.Remove.Search", decision.ReasonKey);
    }

    // ---- Gaming PC: keep list (§8) ----
    [Theory]
    [InlineData("WindowsStore", ComponentFunctionCategory.StoreInfrastructure)]
    [InlineData("GamingServices", ComponentFunctionCategory.Gaming)]
    [InlineData("DefenderFamily", ComponentFunctionCategory.Security)]
    [InlineData("ServicingStack", ComponentFunctionCategory.Servicing)]
    [InlineData("AudioCore", ComponentFunctionCategory.HardwareSupport)]
    [InlineData("NetworkingCore", ComponentFunctionCategory.Networking)]
    [InlineData("UsbDrivers", ComponentFunctionCategory.HardwareSupport)]
    [InlineData("ShellLogin", ComponentFunctionCategory.SystemCore)]
    public void GamingPc_Keeps_Infrastructure(string id, ComponentFunctionCategory fn)
    {
        var input = Input(id, GamingKnowledge.K(id, fn,
            ComponentRiskLevel.Moderate, ComponentRecommendationKind.ProfileDependent,
            ComponentProtectionLevel.Sensitive, ComponentProfileTag.None, ClassificationConfidence.Curated));
        var result = _service.Evaluate(
            new[] { input }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        var item = Assert.Single(result.Items);
        Assert.True(item.Result.IsKeptForCompatibility);
        Assert.Equal("Profile.Reason.Gaming.Keep.Infrastructure", item.Result.ReasonKey);
    }

    [Fact]
    public void GamingPc_Keeps_Protected_Runtimes_And_Store_Ecosystem()
    {
        // WindowsStore is Protected + RequiredKeep → kept by protection.
        var store = Input("Microsoft.WindowsStore", GamingKnowledge.K("WindowsStore",
            ComponentFunctionCategory.StoreInfrastructure, ComponentRiskLevel.Moderate,
            ComponentRecommendationKind.RequiredKeep, ComponentProtectionLevel.Protected,
            ComponentProfileTag.StoreInfrastructure, ClassificationConfidence.Curated));
        var result = _service.Evaluate(new[] { store }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        var item = Assert.Single(result.Items);
        Assert.True(item.Result.IsKeptForCompatibility);
        Assert.Equal("Profile.Reason.Gaming.Keep.Protection", item.Result.ReasonKey);
    }

    [Fact]
    public void GamingPc_Keeps_Runtime_Dependencies_And_Codecs()
    {
        // Codec / runtime (RecommendedKeep) → kept.
        var codec = Input("AV1", GamingKnowledge.K("AV1VideoExtension", ComponentFunctionCategory.Media,
            ComponentRiskLevel.Low, ComponentRecommendationKind.RecommendedKeep,
            ComponentProtectionLevel.Sensitive, ComponentProfileTag.GamingRelevant, ClassificationConfidence.Curated));
        var result = _service.Evaluate(new[] { codec }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        var item = Assert.Single(result.Items);
        Assert.True(item.Result.IsKeptForCompatibility);
        Assert.Equal("Profile.Reason.Gaming.Keep.Runtime", item.Result.ReasonKey);
    }

    [Fact]
    public void Dependency_Preservation_Keeps_Items_Other_Kept_Infrastructure_Depends_On()
    {
        var dep = Input("StorePurchase", GamingKnowledge.K("StorePurchase",
            ComponentFunctionCategory.Productivity, ComponentRiskLevel.Moderate,
            ComponentRecommendationKind.ProfileDependent, ComponentProtectionLevel.Sensitive,
            ComponentProfileTag.None, ClassificationConfidence.Curated,
            "Microsoft.WindowsStore", "Microsoft.DesktopAppInstaller"));
        var result = _service.Evaluate(new[] { dep }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        var item = Assert.Single(result.Items);
        Assert.True(item.Result.IsKeptForCompatibility);
        Assert.Equal("Profile.Reason.Gaming.Keep.Dependency", item.Result.ReasonKey);
    }

    // ---- Gaming PC: optional "never assume" set (§7) ----
    [Theory]
    [InlineData("OneDrive", ComponentProfileTag.CloudStorage, "Profile.Reason.Gaming.Optional.CloudStorage")]
    [InlineData("Printing", ComponentProfileTag.PrintScan, "Profile.Reason.Gaming.Optional.PrintScan")]
    [InlineData("RdpClient", ComponentProfileTag.RemoteAccess, "Profile.Reason.Gaming.Optional.RemoteAccess")]
    [InlineData("DevTools", ComponentProfileTag.DeveloperTool, "Profile.Reason.Gaming.Optional.Developer")]
    [InlineData("HyperV", ComponentProfileTag.Virtualization, "Profile.Reason.Gaming.Optional.Virtualization")]
    public void GamingPc_Optional_Never_Assumed(string id, ComponentProfileTag tag, string reasonKey)
    {
        var input = Input(id, GamingKnowledge.K(id, ComponentFunctionCategory.Productivity,
            ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
            ComponentProtectionLevel.None, tag, ClassificationConfidence.Curated));
        var decision = _service.EvaluateItem(input, GamingProfileKind.GamingPc);
        Assert.NotNull(decision);
        Assert.Equal(GamingVerdict.OptionalRemoveCandidate, decision!.Verdict);
        Assert.Equal(reasonKey, decision.ReasonKey);
    }

    // ---- Extras (§9) ----
    [Theory]
    [InlineData(GamingExtra.XboxGamePass, "GamingServices", ComponentFunctionCategory.Gaming, "Profile.Reason.Gaming.Keep.Extra.XboxGamePass")]
    [InlineData(GamingExtra.WslDocker, "Wsl", ComponentFunctionCategory.Virtualization, "Profile.Reason.Gaming.Keep.Extra.WslDocker")]
    [InlineData(GamingExtra.PrintScan, "PrintingXPS", ComponentFunctionCategory.PrintingScanning, "Profile.Reason.Gaming.Keep.Extra.PrintScan")]
    [InlineData(GamingExtra.RemoteDesktop, "Rdp", ComponentFunctionCategory.RemoteAccess, "Profile.Reason.Gaming.Keep.Extra.RemoteDesktop")]
    public void Extras_Force_Their_Ecosystem_To_Keep(GamingExtra extra, string id, ComponentFunctionCategory fn, string reasonKey)
    {
        var input = Input(id, GamingKnowledge.K(id, fn,
            ComponentRiskLevel.Moderate, ComponentRecommendationKind.OptionalRemove,
            ComponentProtectionLevel.Sensitive, ComponentProfileTag.None, ClassificationConfidence.Curated),
            extras: extra);
        var result = _service.Evaluate(new[] { input }, GamingProfileKind.GamingPc, new HashSet<GamingExtra> { extra });
        var item = Assert.Single(result.Items);
        Assert.True(item.Result.IsKeptForCompatibility);
        Assert.Equal(reasonKey, item.Result.ReasonKey);
        Assert.Equal(extra, item.Result.KeptByExtra);
    }

    // ---- Safety gate (§11) ----
    [Fact]
    public void Gate_Blocks_Protected_And_Critical_And_High()
    {
        var protectedInput = Input("Store", GamingKnowledge.K("WindowsStore",
            ComponentFunctionCategory.StoreInfrastructure, ComponentRiskLevel.Moderate,
            ComponentRecommendationKind.RequiredKeep, ComponentProtectionLevel.Protected,
            ComponentProfileTag.StoreInfrastructure, ClassificationConfidence.Curated));
        Assert.Null(_service.EvaluateItem(protectedInput, GamingProfileKind.GamingPc));

        // The policy itself keeps Protected/Critical/High families, so the gate's
        // Critical/High/Protected rules are exercised DIRECTLY (the gate has final
        // authority and must block even a would-be automatic candidate).
        var critical = ProfileSafetyGate.Evaluate(
            new GamingPolicyDecision { Kind = GamingProfileKind.GamingPc, Verdict = GamingVerdict.AutoRemoveCandidate, ReasonKey = "Profile.Reason.Gaming.Remove.Consumer" },
            Input("Defender", GamingKnowledge.K("DefenderFamily",
                ComponentFunctionCategory.Security, ComponentRiskLevel.Critical,
                ComponentRecommendationKind.OptionalRemove, ComponentProtectionLevel.None,
                ComponentProfileTag.None, ClassificationConfidence.Curated)));
        Assert.Equal(GateVerdict.Block, critical.Gate);
        Assert.Equal("Profile.Reason.Gaming.Gate.Critical", critical.GateReasonKey);

        var high = ProfileSafetyGate.Evaluate(
            new GamingPolicyDecision { Kind = GamingProfileKind.GamingPc, Verdict = GamingVerdict.AutoRemoveCandidate, ReasonKey = "Profile.Reason.Gaming.Remove.Consumer" },
            Input("HighThing", GamingKnowledge.K("HighThing",
                ComponentFunctionCategory.Productivity, ComponentRiskLevel.High,
                ComponentRecommendationKind.OptionalRemove, ComponentProtectionLevel.None,
                ComponentProfileTag.ConsumerContent, ClassificationConfidence.Curated)));
        Assert.Equal(GateVerdict.Block, high.Gate);
        Assert.Equal("Profile.Reason.Gaming.Gate.High", high.GateReasonKey);

        var protectedDirect = ProfileSafetyGate.Evaluate(
            new GamingPolicyDecision { Kind = GamingProfileKind.GamingPc, Verdict = GamingVerdict.AutoRemoveCandidate, ReasonKey = "x" },
            Input("Store", GamingKnowledge.K("WindowsStore",
                ComponentFunctionCategory.StoreInfrastructure, ComponentRiskLevel.Moderate,
                ComponentRecommendationKind.RequiredKeep, ComponentProtectionLevel.Protected,
                ComponentProfileTag.StoreInfrastructure, ClassificationConfidence.Curated)));
        Assert.Equal(GateVerdict.Block, protectedDirect.Gate);
        Assert.Equal("Profile.Reason.Gaming.Gate.Protected", protectedDirect.GateReasonKey);
    }

    [Fact]
    public void Gate_Downgrades_Moderate_To_Optional_Only()
    {
        var moderate = Input("ModThing", GamingKnowledge.K("ModThing",
            ComponentFunctionCategory.Productivity, ComponentRiskLevel.Moderate,
            ComponentRecommendationKind.OptionalRemove, ComponentProtectionLevel.None,
            ComponentProfileTag.ConsumerContent, ClassificationConfidence.Curated));
        var decision = _service.EvaluateItem(moderate, GamingProfileKind.GamingPc);
        Assert.NotNull(decision);
        Assert.Equal(GamingVerdict.OptionalRemoveCandidate, decision!.Verdict);
    }

    [Fact]
    public void Heuristic_Never_Auto_Removed()
    {
        var heuristic = Input("Heur.Thing", GamingKnowledge.K("HeurThing",
            ComponentFunctionCategory.Productivity, ComponentRiskLevel.Low,
            ComponentRecommendationKind.OptionalRemove, ComponentProtectionLevel.None,
            ComponentProfileTag.ConsumerContent, ClassificationConfidence.Heuristic));
        var result = _service.Evaluate(new[] { heuristic }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        var item = Assert.Single(result.Items);
        Assert.Equal(GateVerdict.AllowOptional, item.Result.Gate);
        Assert.False(item.Result.IsAutoRecommended, "heuristic must never auto-remove");
        Assert.Equal("Profile.Reason.Gaming.Gate.Heuristic", item.Result.GateReasonKey);
    }

    [Fact]
    public void Unsupported_And_User_Override_Block_Automatic_Action()
    {
        var unsupported = Input("GetHelp", GamingKnowledge.K("GetHelp",
            ComponentFunctionCategory.Productivity, ComponentRiskLevel.Low,
            ComponentRecommendationKind.OptionalRemove, ComponentProtectionLevel.None,
            ComponentProfileTag.ConsumerContent, ClassificationConfidence.Curated),
            supported: false);
        var unsupportedResult = _service.Evaluate(new[] { unsupported }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        Assert.Equal(GateVerdict.Block, unsupportedResult.Items[0].Result.Gate);
        Assert.Equal("Profile.Reason.Gaming.Gate.Unsupported", unsupportedResult.Items[0].Result.GateReasonKey);

        var overridden = Input("Solitaire", GamingKnowledge.K("Solitaire",
            ComponentFunctionCategory.Gaming, ComponentRiskLevel.Low,
            ComponentRecommendationKind.OptionalRemove, ComponentProtectionLevel.None,
            ComponentProfileTag.ConsumerContent, ClassificationConfidence.Curated),
            overridden: true);
        var overriddenResult = _service.Evaluate(new[] { overridden }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        Assert.Equal(GateVerdict.Block, overriddenResult.Items[0].Result.Gate);
        Assert.Equal("Profile.Reason.Gaming.Gate.UserOverride", overriddenResult.Items[0].Result.GateReasonKey);
    }

    // ---- No placebo tweaks (§12) ----
    [Fact]
    public void Gaming_Profiles_Never_Touch_Security_Or_Servicing_Foundations()
    {
        var foundations = new[]
        {
            GamingKnowledge.K("CbsDefenderFamily", ComponentFunctionCategory.Security,
                ComponentRiskLevel.Critical, ComponentRecommendationKind.RequiredKeep,
                ComponentProtectionLevel.Protected, ComponentProfileTag.SecurityEssential, ClassificationConfidence.KnownFamily),
            GamingKnowledge.K("ServicingStack", ComponentFunctionCategory.Servicing,
                ComponentRiskLevel.Critical, ComponentRecommendationKind.RequiredKeep,
                ComponentProtectionLevel.Protected, ComponentProfileTag.ServicingEssential, ClassificationConfidence.Curated),
        };
        foreach (var kind in new[] { GamingProfileKind.GamingPc, GamingProfileKind.DedicatedGaming })
        {
            var result = _service.Evaluate(
                foundations.Select(k => Input(k.CanonicalId!, k)), kind, new HashSet<GamingExtra>());
            foreach (var item in result.Items)
            {
                Assert.True(item.Result.IsKeptForCompatibility,
                    $"{kind} must keep {item.Result.CanonicalId}");
                Assert.DoesNotContain("HPET", item.Result.ReasonKey);
            }
        }
    }

    // ---- Dedicated Gaming vs Gaming PC (§7 distinction + materially different) ----
    [Fact]
    public void Dedicated_Gaming_Adds_Moderate_Consumer_Media_As_Optional()
    {
        // MediaPlayback at Moderate: Gaming PC has no steer (falls to default),
        // Dedicated Gaming suggests it as OPTIONAL (never automatic).
        var media = Input("MediaFeatures", GamingKnowledge.K("MediaFeatures",
            ComponentFunctionCategory.Media, ComponentRiskLevel.Moderate,
            ComponentRecommendationKind.OptionalRemove, ComponentProtectionLevel.Sensitive,
            ComponentProfileTag.MediaPlayback, ClassificationConfidence.Curated));
        var extras = new HashSet<GamingExtra>();

        var pc = _service.Evaluate(new[] { media }, GamingProfileKind.GamingPc, extras);
        Assert.DoesNotContain(pc.Items, i => i.Result.RawIdentity == "MediaFeatures");

        var dedicated = _service.Evaluate(new[] { media }, GamingProfileKind.DedicatedGaming, extras);
        var item = Assert.Single(dedicated.Items, i => i.Result.RawIdentity == "MediaFeatures");
        Assert.Equal(GamingVerdict.OptionalRemoveCandidate, item.Result.Verdict);
        Assert.False(item.Result.IsAutoRecommended);
        Assert.Equal("Profile.Reason.Gaming.Optional.Media", item.Result.ReasonKey);
    }

    [Fact]
    public void Dedicated_Gaming_Produces_More_Optional_Choices_Than_Gaming_Pc()
    {
        var inputs = new[]
        {
            Input("GetHelp", GamingKnowledge.K("GetHelp", ComponentFunctionCategory.Productivity,
                ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
                ComponentProtectionLevel.None, ComponentProfileTag.ConsumerContent, ClassificationConfidence.Curated)),
            Input("MediaFeatures", GamingKnowledge.K("MediaFeatures", ComponentFunctionCategory.Media,
                ComponentRiskLevel.Moderate, ComponentRecommendationKind.OptionalRemove,
                ComponentProtectionLevel.Sensitive, ComponentProfileTag.MediaPlayback, ClassificationConfidence.Curated)),
            Input("OneDrive", GamingKnowledge.K("OneDrive", ComponentFunctionCategory.CloudIntegration,
                ComponentRiskLevel.Moderate, ComponentRecommendationKind.OptionalRemove,
                ComponentProtectionLevel.None, ComponentProfileTag.CloudStorage, ClassificationConfidence.Curated)),
            Input("WindowsStore", GamingKnowledge.K("WindowsStore", ComponentFunctionCategory.StoreInfrastructure,
                ComponentRiskLevel.Moderate, ComponentRecommendationKind.RequiredKeep,
                ComponentProtectionLevel.Protected, ComponentProfileTag.StoreInfrastructure, ClassificationConfidence.Curated)),
        };
        var extras = new HashSet<GamingExtra>();
        var pc = _service.Evaluate(inputs, GamingProfileKind.GamingPc, extras);
        var dedicated = _service.Evaluate(inputs, GamingProfileKind.DedicatedGaming, extras);

        Assert.True(dedicated.OptionalChoices > pc.OptionalChoices,
            $"dedicated optional ({dedicated.OptionalChoices}) must exceed gaming-pc optional ({pc.OptionalChoices})");
        Assert.Equal(pc.KeptForCompatibility, dedicated.KeptForCompatibility);
    }

    // ---- Summary counts (Part C §13) ----
    [Fact]
    public void Summary_Counts_Recommended_Kept_Optional()
    {
        var inputs = new[]
        {
            Input("PhoneLink", GamingKnowledge.K("PhoneLink", ComponentFunctionCategory.Communication,
                ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
                ComponentProtectionLevel.None, ComponentProfileTag.PhoneIntegration, ClassificationConfidence.Curated)),
            Input("GetHelp", GamingKnowledge.K("GetHelp", ComponentFunctionCategory.Productivity,
                ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
                ComponentProtectionLevel.None, ComponentProfileTag.ConsumerContent, ClassificationConfidence.Curated)),
            Input("GamingServices", GamingKnowledge.K("GamingServices", ComponentFunctionCategory.Gaming,
                ComponentRiskLevel.Moderate, ComponentRecommendationKind.ProfileDependent,
                ComponentProtectionLevel.Sensitive, ComponentProfileTag.GamingRelevant, ClassificationConfidence.Curated)),
            Input("OneDrive", GamingKnowledge.K("OneDrive", ComponentFunctionCategory.CloudIntegration,
                ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
                ComponentProtectionLevel.None, ComponentProfileTag.CloudStorage, ClassificationConfidence.Curated)),
        };
        var summary = _service.Evaluate(inputs, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        Assert.Equal(2, summary.RecommendedChanges);   // PhoneLink + GetHelp
        Assert.Equal(1, summary.KeptForCompatibility); // GamingServices
        Assert.Equal(1, summary.OptionalChoices);      // OneDrive
        Assert.Equal(4, summary.Items.Count);
    }

    [Fact]
    public void Reasons_Are_Deterministic_Resource_Keys_Not_AI_Prose()
    {
        var input = Input("GetHelp", GamingKnowledge.K("GetHelp", ComponentFunctionCategory.Productivity,
            ComponentRiskLevel.Low, ComponentRecommendationKind.OptionalRemove,
            ComponentProtectionLevel.None, ComponentProfileTag.ConsumerContent, ClassificationConfidence.Curated));
        var a = _service.Evaluate(new[] { input }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        var b = _service.Evaluate(new[] { input }, GamingProfileKind.GamingPc, new HashSet<GamingExtra>());
        Assert.Equal(a.Items[0].Result.ReasonKey, b.Items[0].Result.ReasonKey);
        Assert.StartsWith("Profile.Reason.", a.Items[0].Result.ReasonKey);
    }
}

// =====================================================================
// 4. RECOMMENDATION ENGINE — KNOWLEDGE TIER + OVERRIDES (Part C §10/§15)
// =====================================================================
public class Stage14GamingEngineTests
{
    private static EffectiveRecommendation Eval(
        RecommendationInput input,
        IReadOnlyList<ProfileDefinition>? profiles = null,
        IReadOnlyCollection<string>? overrides = null,
        IReadOnlyCollection<string>? presentIds = null)
    {
        var engine = new RecommendationEngine();
        return engine.Evaluate(input, new RecommendationContext
        {
            SelectedProfiles = profiles ?? new List<ProfileDefinition>(),
            UserOverrides = overrides ?? new HashSet<string>(),
            PresentIds = presentIds ?? new HashSet<string>(),
        });
    }

    private static GamingPolicyDecision AutoRemove(string reasonKey = "Profile.Reason.Gaming.Remove.Consumer")
        => new()
        {
            Kind = GamingProfileKind.GamingPc,
            Verdict = GamingVerdict.AutoRemoveCandidate,
            ReasonKey = reasonKey,
        };

    private static GamingPolicyDecision Keep()
        => new()
        {
            Kind = GamingProfileKind.GamingPc,
            Verdict = GamingVerdict.KeepForCompatibility,
            ReasonKey = "Profile.Reason.Gaming.Keep.Infrastructure",
        };

    private static RecommendationInput Input(
        string id,
        GamingPolicyDecision? gaming = null,
        OptimizationAction action = OptimizationAction.Remove) => new()
        {
            LogicalId = id,
            Action = action,
            DefaultRecommendation = RecommendationLevel.OptionalRemove,
            Risk = RiskLevel.Low,
            IsPresent = true,
            IsApplySupported = true,
            GamingDecision = gaming,
        };

    [Fact]
    public void Gaming_AutoRemove_Maps_To_RecommendRemove_With_Reason_And_Profile()
    {
        var profiles = new ProfileCatalog().GetProfiles().Where(p => p.Id == "Gaming").ToList();
        // BingNews has no legacy Gaming override — the KNOWLEDGE tier (5b) fires.
        var result = Eval(Input("BingNews", AutoRemove()), profiles);
        Assert.Equal(EffectiveRecommendationLevel.RecommendRemove, result.Level);
        Assert.True(result.WasProfileDriven);
        Assert.Contains("Profile.Reason.Gaming.Remove.Consumer", result.ReasonKeys);
        Assert.Contains("Gaming", result.AdvisedByProfileIds);
    }

    [Fact]
    public void Gaming_Keep_Maps_To_RecommendKeep()
    {
        var profiles = new ProfileCatalog().GetProfiles().Where(p => p.Id == "Gaming").ToList();
        var result = Eval(Input("GamingServices", Keep()), profiles);
        Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, result.Level);
        Assert.Contains("Profile.Reason.Gaming.Keep.Infrastructure", result.ReasonKeys);
    }

    [Fact]
    public void User_Override_Remains_Authoritative()
    {
        var profiles = new ProfileCatalog().GetProfiles().Where(p => p.Id == "Gaming").ToList();
        var result = Eval(Input("PhoneLink", AutoRemove()), profiles, overrides: new[] { "PhoneLink" });
        // Manual choice is authoritative: the override is recorded and reason shown;
        // adoption never touches overridden items (ApplyProfileSelections skips them).
        Assert.True(result.WasOverridden);
        Assert.Contains("Profile.Reason.UserOverride", result.ReasonKeys);
    }

    [Fact]
    public void Profile_Requirement_Wins_Over_Gaming_Decision()
    {
        var catalog = new ProfileCatalog().GetProfiles().ToList();
        var gaming = catalog.Single(p => p.Id == "Gaming");
        var wslDocker = catalog.Single(p => p.Id == "WslDocker");
        var result = Eval(
            Input("Wsl", AutoRemove()),
            profiles: new[] { gaming, wslDocker },
            presentIds: new[] { "Wsl", "VirtualMachinePlatform", "HypervisorPlatform" });
        Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, result.Level);
        Assert.Contains("WslDocker", result.AdvisedByProfileIds);
    }

    [Fact]
    public void Extra_Scenario_Override_Wins_Over_Gaming_Decision()
    {
        var catalog = new ProfileCatalog().GetProfiles().ToList();
        var gaming = catalog.Single(p => p.Id == "Gaming");
        var xbox = catalog.Single(p => p.Id == "XboxGamePass");
        // XboxGamePass extra explicitly keeps XboxApp; the (hypothetical) gaming
        // auto-remove decision must NOT win — explicit extra rules take precedence.
        var result = Eval(
            Input("XboxApp", AutoRemove()),
            profiles: new[] { gaming, xbox });
        Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, result.Level);
    }

    [Fact]
    public void No_Gaming_Decision_Falls_Through_To_Default()
    {
        var result = Eval(Input("PhoneLink", gaming: null));
        Assert.Equal(EffectiveRecommendationLevel.ManualReview, result.Level);
        Assert.False(result.WasProfileDriven);
    }
}

// =====================================================================
// 5. APP-LEVEL — CATALOG + PROFILE SUMMARY (Part C §13/§15)
// =====================================================================
public class Stage14GamingAppTests
{
    [Fact]
    public void Catalog_Exposes_GamingPc_And_DedicatedGaming_As_Distinct_Primaries()
    {
        var profiles = new ProfileCatalog().GetProfiles().ToList();
        var gaming = Assert.Single(profiles, p => p.Id == "Gaming");
        var dedicated = Assert.Single(profiles, p => p.Id == "DedicatedGaming");

        Assert.Equal(GamingProfileKind.GamingPc, gaming.GamingKind);
        Assert.Equal(GamingProfileKind.DedicatedGaming, dedicated.GamingKind);
        Assert.Equal(ProfileKind.Primary, dedicated.Kind);
        Assert.NotEqual(gaming.Id, dedicated.Id); // never aliases
    }

    private static ProfileViewModel BuildProfileVm(
        Func<IEnumerable<IRecommendationSubject>> subjects,
        RecommendationContextService ctx)
        => new(ctx, new FakeLocalizationService(), subjects, () => { });

    private static IReadOnlyList<IRecommendationSubject> GamingSubjects() => new IRecommendationSubject[]
    {
        new FakeGamingSubject
        {
            LogicalId = "PhoneLink",
            RawIdentity = "Microsoft.YourPhone_8wekyb3d8bbwe",
            DeepKnowledge = RealInventoryFixture.Classifier.Classify("Microsoft.YourPhone_8wekyb3d8bbwe"),
            DisplayName = "Phone Link",
        },
        new FakeGamingSubject
        {
            LogicalId = "GamingServices",
            RawIdentity = "Microsoft.GamingServices_8wekyb3d8bbwe",
            DeepKnowledge = RealInventoryFixture.Classifier.Classify("Microsoft.GamingServices_8wekyb3d8bbwe"),
            DisplayName = "Gaming Services",
        },
        new FakeGamingSubject
        {
            LogicalId = "WindowsStore",
            RawIdentity = "Microsoft.WindowsStore_8wekyb3d8bbwe",
            DeepKnowledge = RealInventoryFixture.Classifier.Classify("Microsoft.WindowsStore_8wekyb3d8bbwe"),
            DisplayName = "Microsoft Store",
        },
    };

    [Fact]
    public void Selecting_Gaming_Populates_The_User_Facing_Summary()
    {
        var state = new AppState();
        var ctx = new RecommendationContextService(new RecommendationEngine(), new ProfileCatalog(), state);
        var vm = BuildProfileVm(GamingSubjects, ctx);
        Assert.False(vm.HasGamingSummary);

        vm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;
        vm.RefreshSummary();

        Assert.True(vm.HasGamingSummary);
        Assert.Contains("Profile.Summary.Gaming.Recommended", vm.GamingSummaryText);
        Assert.Contains("Profile.Summary.Gaming.Kept", vm.GamingSummaryText);
        Assert.Contains("Profile.Summary.Gaming.Optional", vm.GamingSummaryText);
        // Kept examples include Store/Gaming Services; auto examples include Phone Link.
        Assert.Contains("Gaming Services", vm.GamingSummaryText);
        Assert.Contains("Microsoft Store", vm.GamingSummaryText);
    }

    [Fact]
    public void Selecting_DedicatedGaming_Shows_Its_Own_Summary()
    {
        var state = new AppState();
        var ctx = new RecommendationContextService(new RecommendationEngine(), new ProfileCatalog(), state);
        var vm = BuildProfileVm(GamingSubjects, ctx);
        vm.Profiles.Single(p => p.Definition.Id == "DedicatedGaming").IsSelected = true;
        vm.RefreshSummary();
        Assert.True(vm.HasGamingSummary);
    }

    [Fact]
    public void Non_Gaming_Profile_Has_No_Gaming_Summary()
    {
        var state = new AppState();
        var ctx = new RecommendationContextService(new RecommendationEngine(), new ProfileCatalog(), state);
        var vm = BuildProfileVm(GamingSubjects, ctx);
        vm.Profiles.Single(p => p.Definition.Id == "Balanced").IsSelected = true;
        vm.RefreshSummary();
        Assert.False(vm.HasGamingSummary);
    }
}
