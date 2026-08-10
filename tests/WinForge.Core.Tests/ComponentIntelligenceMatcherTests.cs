using System.Collections.Generic;
using System.Linq;
using WinForge.Core.Models;
using Xunit;

namespace WinForge.Core.Tests;

/// <summary>
/// Pure classification logic of <see cref="ComponentMatcher"/> (Stage 11.1): matching a
/// discovered raw object onto a curated <see cref="ComponentDefinition"/>, classifying
/// the rest as Protected / Unsupported / DiscoveredUnclassified, collapsing multi-target
/// components, and preserving catalog-only rows. No DISM, no App layer.
/// </summary>
public class ComponentIntelligenceMatcherTests
{
    private static ComponentDefinition CatalogDef(string id, ComponentCategory category,
        params (ComponentCategory, MatchMethod, string)[] targets)
        => CatalogDef(id, category, null, targets);

    private static ComponentDefinition CatalogDef(string id, ComponentCategory category,
        IReadOnlyList<ComponentDependency>? dependencies,
        params (ComponentCategory, MatchMethod, string)[] targets)
    {
        return new ComponentDefinition
        {
            Id = id,
            Category = category,
            TechnicalTargets = targets
                .Select(t => new TechnicalTarget { Category = t.Item1, Match = t.Item2, Pattern = t.Item3 })
                .ToList(),
            Dependencies = dependencies ?? new List<ComponentDependency>()
        };
    }

    private static CategoryDiscoveryResult RawCategory(ComponentCategory category, params IRawInventoryItem[] items)
        => new CategoryDiscoveryResult { Category = category, Status = InventoryStatus.Success, Items = items };

    private static RawAppxPackage Appx(string identity, string display = "")
        => new RawAppxPackage { Category = ComponentCategory.AppX, RawIdentity = identity, DisplayName = display, State = "Provisioned" };

    [Fact]
    public void NullRaw_ReturnsCatalogOnlyCuratedRows()
    {
        var catalog = new List<ComponentDefinition>
        {
            CatalogDef("Weather", ComponentCategory.AppX, (ComponentCategory.AppX, MatchMethod.Prefix, "Microsoft.BingWeather"))
        };

        var result = ComponentMatcher.BuildInventoryEntries(null, catalog);

        Assert.False(result.Discovered);
        Assert.Single(result.Entries);
        Assert.All(result.Entries, e => Assert.Equal(ComponentClassification.Curated, e.Classification));
        Assert.All(result.Entries, e => Assert.Empty(e.RawItems));
    }

    [Fact]
    public void MatchingAppX_BecomesCurated()
    {
        var catalog = new List<ComponentDefinition>
        {
            CatalogDef("Weather", ComponentCategory.AppX, (ComponentCategory.AppX, MatchMethod.Prefix, "Microsoft.BingWeather"))
        };
        var raw = new ComponentInventory
        {
            Discovered = true,
            Categories = new List<CategoryDiscoveryResult>
            {
                RawCategory(ComponentCategory.AppX, Appx("Microsoft.BingWeather_1.0_neutral_~_8wekyb3d8bbwe", "Microsoft Weather"))
            }
        };

        var result = ComponentMatcher.BuildInventoryEntries(raw, catalog);
        var entry = Assert.Single(result.Entries.Where(e => e.Classification == ComponentClassification.Curated));

        Assert.Equal("Weather", entry.Definition!.Id);
        Assert.Equal("Microsoft.BingWeather_1.0_neutral_~_8wekyb3d8bbwe", entry.RepresentativeRaw!.RawIdentity);
    }

    [Fact]
    public void NonMatchingAppX_BecomesDiscoveredUnclassified()
    {
        // Empty catalog isolates the raw-item classification: with no curated
        // definition to match, a non-matching AppX stays DiscoveredUnclassified.
        var raw = new ComponentInventory
        {
            Discovered = true,
            Categories = new List<CategoryDiscoveryResult>
            {
                RawCategory(ComponentCategory.AppX, Appx("Contoso.SampleApp_1.0_neutral_~_x"))
            }
        };

        var result = ComponentMatcher.BuildInventoryEntries(raw, new List<ComponentDefinition>());
        var entry = Assert.Single(result.Entries);

        Assert.Equal(ComponentClassification.DiscoveredUnclassified, entry.Classification);
        Assert.Null(entry.Definition);
    }

    [Fact]
    public void ProtectedIdentity_BecomesProtected()
    {
        var raw = new ComponentInventory
        {
            Discovered = true,
            Categories = new List<CategoryDiscoveryResult>
            {
                RawCategory(ComponentCategory.CbsPackage, new RawCbsPackage
                {
                    Category = ComponentCategory.CbsPackage,
                    RawIdentity = "Microsoft-Windows-ServicingStack-Package~31bf3856ad364e35~amd64~~10.0.26100.1",
                    State = "Installed"
                })
            }
        };

        var result = ComponentMatcher.BuildInventoryEntries(raw, new List<ComponentDefinition>());
        var entry = Assert.Single(result.Entries);

        Assert.Equal(ComponentClassification.Protected, entry.Classification);
    }

    [Fact]
    public void UnsupportedCategory_BecomesUnsupported()
    {
        // A raw item whose category is in the Unsupported set must never be offered,
        // even though its provider interface is designed. Use an AppX-typed object but
        // tagged with the Service category to exercise the UnsupportedCategories set.
        var svc = new RawAppxPackage { Category = ComponentCategory.Service, RawIdentity = "Dummy.Service", State = "Running" };
        var raw = new ComponentInventory
        {
            Discovered = true,
            Categories = new List<CategoryDiscoveryResult> { RawCategory(ComponentCategory.Service, svc) }
        };

        var result = ComponentMatcher.BuildInventoryEntries(raw, new List<ComponentDefinition>());
        var entry = Assert.Single(result.Entries);

        Assert.Equal(ComponentClassification.Unsupported, entry.Classification);
    }

    [Fact]
    public void MultiTargetCollapse_GroupsIntoSingleCuratedRow()
    {
        var xbox = CatalogDef("XboxApp", ComponentCategory.AppX,
            (ComponentCategory.AppX, MatchMethod.Prefix, "Microsoft.XboxApp"),
            (ComponentCategory.AppX, MatchMethod.Prefix, "Microsoft.XboxGamingOverlay"),
            (ComponentCategory.AppX, MatchMethod.Prefix, "Microsoft.XboxIdentityProvider"),
            (ComponentCategory.AppX, MatchMethod.Prefix, "Microsoft.XboxSpeechToTextOverlay"));
        var raw = new ComponentInventory
        {
            Discovered = true,
            Categories = new List<CategoryDiscoveryResult>
            {
                RawCategory(ComponentCategory.AppX,
                    Appx("Microsoft.XboxApp_1.0_neutral_~_8wekyb3d8bbwe"),
                    Appx("Microsoft.XboxGamingOverlay_1.0_neutral_~_8wekyb3d8bbwe"),
                    Appx("Microsoft.XboxIdentityProvider_1.0_neutral_~_8wekyb3d8bbwe"),
                    Appx("Microsoft.XboxSpeechToTextOverlay_1.0_neutral_~_8wekyb3d8bbwe"))
            }
        };

        var result = ComponentMatcher.BuildInventoryEntries(raw, new List<ComponentDefinition> { xbox });
        var entry = Assert.Single(result.Entries.Where(e => e.Classification == ComponentClassification.Curated));

        Assert.Equal("XboxApp", entry.Definition!.Id);
        Assert.Equal(4, entry.RawItems.Count);
    }

    [Fact]
    public void Dependency_Requires_IsPreserved()
    {
        var teams = CatalogDef("Teams", ComponentCategory.AppX,
            new List<ComponentDependency>
            {
                new ComponentDependency { ToId = "OneDrive", Relation = DependencyRelation.Requires, Reason = "Teams files live in OneDrive." }
            },
            (ComponentCategory.AppX, MatchMethod.Prefix, "MicrosoftTeams"));
        var raw = new ComponentInventory
        {
            Discovered = true,
            Categories = new List<CategoryDiscoveryResult>
            {
                RawCategory(ComponentCategory.AppX, Appx("MicrosoftTeams_1.0_neutral_~_8wekyb3d8bbwe"))
            }
        };

        var result = ComponentMatcher.BuildInventoryEntries(raw, new List<ComponentDefinition> { teams });
        var entry = Assert.Single(result.Entries);
        var dep = Assert.Single(entry.Definition!.Dependencies);

        Assert.Equal("OneDrive", dep.ToId);
        Assert.Equal(DependencyRelation.Requires, dep.Relation);
    }

    [Fact]
    public void Cancelled_Raw_PreservesCancelledFlag()
    {
        var result = ComponentMatcher.BuildInventoryEntries(
            new ComponentInventory { Discovered = true, Cancelled = true, Categories = new List<CategoryDiscoveryResult>() },
            new List<ComponentDefinition>());

        Assert.True(result.Cancelled);
    }

    [Fact]
    public void UnmatchedCatalogDefs_AppendedAsCatalogOnlyRows()
    {
        var catalog = new List<ComponentDefinition>
        {
            CatalogDef("Weather", ComponentCategory.AppX, (ComponentCategory.AppX, MatchMethod.Prefix, "Microsoft.BingWeather"))
        };
        var raw = new ComponentInventory
        {
            Discovered = true,
            Categories = new List<CategoryDiscoveryResult>
            {
                RawCategory(ComponentCategory.AppX, Appx("Contoso.Other_1.0_neutral_~_x"))
            }
        };

        var result = ComponentMatcher.BuildInventoryEntries(raw, catalog);

        Assert.Contains(result.Entries, e => e.Classification == ComponentClassification.DiscoveredUnclassified);
        Assert.Contains(result.Entries, e =>
            e.Classification == ComponentClassification.Curated &&
            e.Definition!.Id == "Weather" &&
            e.RawItems.Count == 0);
    }
}
