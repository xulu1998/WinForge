using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinForge.App.Converters;
using WinForge.App.Localization;
using WinForge.App.Mvvm;
using WinForge.App.ViewModels;
using WinForge.App.Views;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.ComponentIntelligence;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Stage 11.2 regression + behavioural tests for Component Knowledge Import,
/// Catalog Expansion, and Customize integration. Groups:
///  - KnowledgeProvenanceTests    (Part A: FACT vs RECOMMENDATION, scenario overrides)
///  - KnowledgeImportPipelineTests (Part B: community never auto-RecommendedRemove, candidate≠Curated)
///  - CuratedCatalogStage11p2Tests (Part C: catalog expansion, Xbox grouping, provenance)
///  - ComponentKnowledgeCustomizeTests (Parts D–I: sort, filter, hover, detail, Protected/Unknown, no auto-select)
///  - PhaseRegression11p2Tests    (Part M: tab wiring, safety gate, XAML load)
/// No DISM / mount is required except where a discovery pass is explicitly exercised.
/// </summary>
public class ComponentKnowledgeStage11p2Tests
{
    // ---- Localization fakes ----

    private sealed class KeyLoc : ILocalizationService
    {
        public CultureInfo CurrentCulture => CultureInfo.GetCultureInfo("en");
        public event EventHandler? CultureChanged { add { } remove { } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public string this[string key] => key;
        public void SetCulture(CultureInfo c) { }
        public bool Contains(string key) => true;
    }

    /// <summary>Resolves <c>Comp.*</c> keys to a readable string (strips the prefix) so
    /// hover/detail text assertions can use real-looking values in tests.</summary>
    private sealed class ResolvingLoc : ILocalizationService
    {
        public CultureInfo CurrentCulture => CultureInfo.GetCultureInfo("en");
        public event EventHandler? CultureChanged { add { } remove { } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public string this[string key] =>
            key.StartsWith("Comp.", StringComparison.Ordinal) ? key.Substring(5) : key;
        public void SetCulture(CultureInfo c) { }
        public bool Contains(string key) => true;
    }

    /// <summary>Language-specific localizer for the en-US vs zh-CN wiring test.</summary>
    private sealed class LangLoc : ILocalizationService
    {
        private readonly string _suffix;
        public LangLoc(string suffix) => _suffix = suffix;
        public CultureInfo CurrentCulture => CultureInfo.GetCultureInfo("en");
        public event EventHandler? CultureChanged { add { } remove { } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public string this[string key] => key + _suffix;
        public void SetCulture(CultureInfo c) { }
        public bool Contains(string key) => true;
    }

    private sealed class NoDiscoveryCiService : IComponentIntelligenceService
    {
        public Task<ComponentInventory> DiscoverAsync(
            ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
            => Task.FromResult(new ComponentInventory());
    }

    private static IReadOnlyList<ComponentDefinition> Catalog() => new CuratedComponentCatalog().GetDefinitions();

    private static ComponentInventoryEntry CuratedEntry(ComponentDefinition def) =>
        new ComponentInventoryEntry
        {
            Definition = def,
            RawItems = new List<IRawInventoryItem>(),
            Classification = ComponentClassification.Curated
        };

    /// <summary>A raw inventory whose AppX identities match Weather / Clipchamp /
    /// Maps (RecommendedRemove) and AV1 (OptionalRemove) catalog definitions. Shared by
    /// the Customize and Phase-regression test classes (ADR-049 present-only semantics).</summary>
    private static ComponentInventory MakeMatchingRawInventory()
    {
        var items = new List<IRawInventoryItem>
        {
            Appx("Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe", "Weather"),
            Appx("Clipchamp.Clipchamp_2.0_neutral_~_8wekyb3d8bbwe", "Clipchamp"),
            Appx("Microsoft.WindowsMaps_1.0_neutral_~_8wekyb3d8bbwe", "Maps"),
            Appx("Microsoft.AV1VideoExtension_2.0.6.0_neutral_~_8wekyb3d8bbwe", "AV1"),
        };
        return new ComponentInventory
        {
            Discovered = true,
            Categories = new List<CategoryDiscoveryResult>
            {
                new() { Category = ComponentCategory.AppX, Status = InventoryStatus.Success, Items = items }
            }
        };
    }

    private static RawAppxPackage Appx(string identity, string display) => new()
    {
        Category = ComponentCategory.AppX,
        RawIdentity = identity,
        DisplayName = display,
        State = "Provisioned"
    };

    // ============================================================
    // Part A — Knowledge provenance model
    // ============================================================

    public class KnowledgeProvenanceTests
    {
        [Fact]
        public void Fact_And_Recommendation_Claims_Are_Separately_Tagged()
        {
            var def = new ComponentDefinition
            {
                Id = "X",
                Provenance = new[]
                {
                    new KnowledgeClaim
                    {
                        Kind = KnowledgeClaimKind.Fact,
                        TextKey = "Comp.X.Prov0",
                        Sources = new[] { new KnowledgeSource(KnowledgeSourceType.MicrosoftOfficial, "MS", ConfidenceLevel.High) }
                    },
                    new KnowledgeClaim
                    {
                        Kind = KnowledgeClaimKind.Recommendation,
                        TextKey = "Comp.X.Prov1",
                        Sources = new[] { new KnowledgeSource(KnowledgeSourceType.WinForgeCurated, "WinForge", ConfidenceLevel.Verified) }
                    }
                }
            };

            var facts = def.Provenance.Where(p => p.Kind == KnowledgeClaimKind.Fact).ToList();
            var recs = def.Provenance.Where(p => p.Kind == KnowledgeClaimKind.Recommendation).ToList();

            Assert.Single(facts);
            Assert.Single(recs);
            Assert.All(facts, f => Assert.Equal(KnowledgeClaimKind.Fact, f.Kind));
            Assert.All(recs, r => Assert.Equal(KnowledgeClaimKind.Recommendation, r.Kind));
        }

        [Fact]
        public void ResolveRecommendation_Without_Scenario_Returns_Base_Recommendation()
        {
            var def = new ComponentDefinition { Id = "X", Recommendation = RecommendationLevel.OptionalRemove };
            Assert.Equal(RecommendationLevel.OptionalRemove, def.ResolveRecommendation(null));
            Assert.Equal(RecommendationLevel.OptionalRemove, def.ResolveRecommendation(ComponentScenario.Unknown));
        }

        [Fact]
        public void ResolveRecommendation_Applies_Scenario_Override()
        {
            var def = new ComponentDefinition
            {
                Id = "X",
                Recommendation = RecommendationLevel.OptionalRemove,
                ScenarioRecommendations = new[]
                {
                    new ScenarioRecommendation(ComponentScenario.Gaming, RecommendationLevel.UsuallyKeep, "Comp.X.Scen.Gaming")
                }
            };

            Assert.Equal(RecommendationLevel.UsuallyKeep, def.ResolveRecommendation(ComponentScenario.Gaming));
            Assert.Equal(RecommendationLevel.OptionalRemove, def.ResolveRecommendation(ComponentScenario.Office));
            Assert.Equal(RecommendationLevel.OptionalRemove, def.ResolveRecommendation(null));
        }

        [Fact]
        public void ResolveRecommendation_Unknown_Scenario_Returns_Base()
        {
            var def = new ComponentDefinition { Id = "X", Recommendation = RecommendationLevel.RecommendedRemove };
            Assert.Equal(RecommendationLevel.RecommendedRemove, def.ResolveRecommendation(ComponentScenario.Unknown));
        }

        [Fact]
        public void KnowledgeSource_RoundTrips_Fields()
        {
            var s = new KnowledgeSource(KnowledgeSourceType.CommunityProject, "Win11Debloat", ConfidenceLevel.Low,
                sourceReference: "remove.json", retrievedOrReviewedAt: new DateTime(2026, 1, 1));
            Assert.Equal(KnowledgeSourceType.CommunityProject, s.SourceType);
            Assert.Equal("Win11Debloat", s.SourceName);
            Assert.Equal(ConfidenceLevel.Low, s.Confidence);
            Assert.Equal("remove.json", s.SourceReference);
            Assert.Equal(new DateTime(2026, 1, 1), s.RetrievedOrReviewedAt);
        }

        [Fact]
        public void ScenarioRecommendation_RoundTrips_Fields()
        {
            var sr = new ScenarioRecommendation(ComponentScenario.Developer, RecommendationLevel.UsuallyKeep, "Comp.X.Scen.Dev");
            Assert.Equal(ComponentScenario.Developer, sr.Scenario);
            Assert.Equal(RecommendationLevel.UsuallyKeep, sr.Recommendation);
            Assert.Equal("Comp.X.Scen.Dev", sr.ReasonKey);
        }

        [Fact]
        public void ResolveRecommendation_NoOverride_Returns_Base_Even_When_Scenario_Given()
        {
            var def = new ComponentDefinition { Id = "X", Recommendation = RecommendationLevel.AdvancedOnly };
            Assert.Equal(RecommendationLevel.AdvancedOnly, def.ResolveRecommendation(ComponentScenario.Gaming));
        }
    }

    // ============================================================
    // Part B — Knowledge import pipeline
    // ============================================================

    public class KnowledgeImportPipelineTests
    {
        [Fact]
        public void Community_Candidate_Recommendation_Stays_Unknown_Not_Promoted()
        {
            var adapter = new Win11DebloatCommunityAdapter(new[]
            {
                ("Microsoft.BingWeather", ComponentCategory.AppX, RecommendationLevel.RecommendedRemove)
            });
            var candidate = adapter.Produce().Single();

            Assert.Equal(RecommendationLevel.Unknown, candidate.EffectiveRecommendation);
            Assert.Equal(RecommendationLevel.RecommendedRemove, candidate.CommunityProposal);
            Assert.True(candidate.IsCommunityOnly);
            Assert.False(candidate.HasTrustedRecommendation);
        }

        [Fact]
        public void PromoteToCurated_Refuses_Community_Only()
        {
            var adapter = new Win11DebloatCommunityAdapter(new[]
            {
                ("Microsoft.BingWeather", ComponentCategory.AppX, RecommendationLevel.RecommendedRemove)
            });
            var candidate = adapter.Produce().Single();

            Assert.Null(KnowledgeImportPipeline.PromoteToCurated(candidate));
        }

        [Fact]
        public void Candidate_Not_Auto_Curated_Promote_Returns_Null()
        {
            var def = new ComponentDefinition
            {
                Id = "X",
                Recommendation = RecommendationLevel.OptionalRemove,
                Provenance = new[] { new KnowledgeClaim(KnowledgeClaimKind.Recommendation, "Comp.X.Prov1",
                    new[] { new KnowledgeSource(KnowledgeSourceType.WinForgeCurated, "WinForge", ConfidenceLevel.Verified) }) }
            };
            var adapter = new WinForgeCuratedAdapter(new[] { def });
            // Force status to Candidate (not yet Reviewed/Curated).
            var candidate = adapter.Produce().Single();
            Assert.Equal(ReviewStatus.Curated, candidate.Status); // WinForgeCurated adapter marks Curated

            // But a purely-Candidate (unreviewed) candidate must not promote.
            var pending = new CandidateComponentDefinition
            {
                Id = "Y",
                DisplayName = "Y",
                EffectiveRecommendation = RecommendationLevel.OptionalRemove,
                Status = ReviewStatus.Candidate,
                Sources = new[] { new KnowledgeSource(KnowledgeSourceType.WinForgeCurated, "WinForge", ConfidenceLevel.Verified) }
            };
            Assert.Null(KnowledgeImportPipeline.PromoteToCurated(pending));
        }

        [Fact]
        public void Curated_WinForge_Candidate_Promotes_To_ComponentDefinition()
        {
            var def = new ComponentDefinition
            {
                Id = "X",
                DisplayNameKey = "Comp.X.DisplayName",
                Recommendation = RecommendationLevel.OptionalRemove,
                Risk = RiskLevel.Low,
                TechnicalTargets = new[] { new TechnicalTarget { Category = ComponentCategory.AppX, Match = MatchMethod.Prefix, Pattern = "X.Family" } },
                Provenance = new[] { new KnowledgeClaim(KnowledgeClaimKind.Recommendation, "Comp.X.Prov1",
                    new[] { new KnowledgeSource(KnowledgeSourceType.WinForgeCurated, "WinForge", ConfidenceLevel.Verified) }) }
            };
            var adapter = new WinForgeCuratedAdapter(new[] { def });
            var candidate = adapter.Produce().Single();

            var promoted = KnowledgeImportPipeline.PromoteToCurated(candidate);
            Assert.NotNull(promoted);
            Assert.Equal("X", promoted!.Id);
            Assert.Equal(RecommendationLevel.OptionalRemove, promoted.Recommendation);
            Assert.Equal("X.Family", promoted.TechnicalTargets[0].Pattern);
        }

        [Fact]
        public void Pipeline_Merge_Same_Id_No_Duplicate_Targets_Or_Sources_Status_Raised()
        {
            var ms = new MicrosoftOfficialAdapter(new (string, string, ComponentCategory, string, string?)[]
            {
                ("X", "X display", ComponentCategory.AppX, "X is a thing.", "ref")
            });
            var curated = new WinForgeCuratedAdapter(new[]
            {
                new ComponentDefinition
                {
                    Id = "X",
                    DisplayNameKey = "Comp.X.DisplayName",
                    Recommendation = RecommendationLevel.OptionalRemove,
                    TechnicalTargets = new[] { new TechnicalTarget { Category = ComponentCategory.AppX, Match = MatchMethod.Prefix, Pattern = "X.Family" } }
                }
            });

            var pipeline = new KnowledgeImportPipeline();
            pipeline.AddAdapter(ms);
            pipeline.AddAdapter(curated);
            var merged = pipeline.Run().Single();

            Assert.Equal("X", merged.Id);
            Assert.Equal(RecommendationLevel.OptionalRemove, merged.EffectiveRecommendation);
            Assert.Equal(ReviewStatus.Curated, merged.Status); // raised to most trusted
            Assert.Single(merged.TechnicalTargets);            // de-duplicated
            Assert.Equal(2, merged.Sources.Count);             // MS + WinForge, no dup
        }

        [Fact]
        public void GetCurrent_Excludes_Deprecated()
        {
            var kept = new CandidateComponentDefinition { Id = "A", Status = ReviewStatus.Curated };
            var deprecated = new CandidateComponentDefinition { Id = "B", Status = ReviewStatus.Deprecated };

            var current = KnowledgeImportPipeline.GetCurrent(new[] { kept, deprecated });
            Assert.Single(current);
            Assert.Equal("A", current[0].Id);
        }

        [Fact]
        public void Unknown_Candidate_Stays_Unknown_And_Does_Not_Promote()
        {
            var candidate = new CandidateComponentDefinition
            {
                Id = "Z",
                DisplayName = "Z",
                Status = ReviewStatus.Candidate,
                Sources = new[] { new KnowledgeSource(KnowledgeSourceType.MicrosoftOfficial, "MS", ConfidenceLevel.Medium) }
            };

            Assert.Equal(RecommendationLevel.Unknown, candidate.EffectiveRecommendation);
            Assert.False(candidate.HasTrustedRecommendation);
            Assert.Null(KnowledgeImportPipeline.PromoteToCurated(candidate));
        }
    }

    // ============================================================
    // Part C — Curated catalog expansion + grouping
    // ============================================================

    public class CuratedCatalogStage11p2Tests
    {
        [Fact]
        public void Catalog_Expanded_To_22_Components()
        {
            Assert.Equal(22, Catalog().Count);
        }

        [Fact]
        public void New_Stage11p2_Components_Are_Present()
        {
            var ids = Catalog().Select(d => d.Id).ToHashSet();
            foreach (var id in new[]
            {
                "AV1VideoExtension", "AVCEncoderVideoExtension", "BingNews", "BingSearch",
                "Calculator", "Notepad", "Paint", "Terminal", "ToDo", "QuickAssist", "DesktopAppInstaller"
            })
            {
                Assert.Contains(id, ids);
            }
        }

        [Fact]
        public void Xbox_Grouping_Collapses_Nine_Identities_Into_One_Row()
        {
            var raw = MakeRawInventory(new[]
            {
                "Microsoft.XboxApp_1.0_neutral_~_8wekyb3d8bbwe",
                "Microsoft.XboxGamingOverlay_1.0_neutral_~_8wekyb3d8bbwe",
                "Microsoft.XboxIdentityProvider_1.0_neutral_~_8wekyb3d8bbwe",
                "Microsoft.XboxSpeechToTextOverlay_1.0_neutral_~_8wekyb3d8bbwe",
                "Microsoft.XboxGameOverlay_1.0_neutral_~_8wekyb3d8bbwe",
                "Microsoft.Xbox.TCUI_1.0_neutral_~_8wekyb3d8bbwe",
                "Microsoft.GamingServices_1.0_neutral_~_8wekyb3d8bbwe",
                "Microsoft.GamingServicesNet_1.0_neutral_~_8wekyb3d8bbwe",
                "Microsoft.GamingApp_1.0_neutral_~_8wekyb3d8bbwe"
            });

            var inv = ComponentMatcher.BuildInventoryEntries(raw, Catalog());
            var xbox = inv.Entries.Single(e => e.Definition?.Id == "XboxApp");

            Assert.Equal(ComponentClassification.Curated, xbox.Classification);
            Assert.Equal(9, xbox.RawItems.Count); // all nine collapsed into one logical component
        }

        [Fact]
        public void New_Curated_Matches_Real_Weather_Identity()
        {
            var raw = MakeRawInventory(new[] { "Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe" });
            var inv = ComponentMatcher.BuildInventoryEntries(raw, Catalog());
            var weather = inv.Entries.Single(e => e.Definition?.Id == "Weather");

            Assert.Equal(ComponentClassification.Curated, weather.Classification);
            Assert.Equal("Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe", weather.RepresentativeRaw!.RawIdentity);
        }

        [Fact]
        public void Grouping_Does_Not_Match_Unrelated_Raw()
        {
            var raw = MakeRawInventory(new[] { "Microsoft.Contoso_1.0_neutral_~_8wekyb3d8bbwe" });
            var inv = ComponentMatcher.BuildInventoryEntries(raw, Catalog());

            var contoso = inv.Entries.Single(e => e.RepresentativeRaw?.RawIdentity.StartsWith("Microsoft.Contoso") == true);
            Assert.Null(contoso.Definition);
            Assert.Equal(ComponentClassification.DiscoveredUnclassified, contoso.Classification);
        }

        [Fact]
        public void Xbox_Provenance_Has_Fact_And_Recommendation_Claims()
        {
            var xbox = Catalog().Single(d => d.Id == "XboxApp");
            Assert.Contains(xbox.Provenance, c => c.Kind == KnowledgeClaimKind.Fact);
            Assert.Contains(xbox.Provenance, c => c.Kind == KnowledgeClaimKind.Recommendation);
        }

        [Fact]
        public void Xbox_Scenario_Override_Present_In_Catalog()
        {
            var xbox = Catalog().Single(d => d.Id == "XboxApp");
            var sr = xbox.ScenarioRecommendations.SingleOrDefault(s => s.Scenario == ComponentScenario.Gaming);
            Assert.NotNull(sr);
            Assert.Equal(RecommendationLevel.UsuallyKeep, sr!.Recommendation);
            Assert.Equal(RecommendationLevel.OptionalRemove, xbox.ResolveRecommendation(null));
            Assert.Equal(RecommendationLevel.UsuallyKeep, xbox.ResolveRecommendation(ComponentScenario.Gaming));
        }

        [Fact]
        public void Compatibility_Rule_Present_For_Weather()
        {
            var weather = Catalog().Single(d => d.Id == "Weather");
            var rule = Assert.Single(weather.CompatibilityRules);
            Assert.Equal("22000", rule.SupportedBuildMin);
            Assert.Contains("26100", rule.KnownOnBuilds);
        }

        private static ComponentInventory MakeRawInventory(IEnumerable<string> identities)
        {
            var items = identities.Select(id => (IRawInventoryItem)new RawAppxPackage
            {
                Category = ComponentCategory.AppX,
                RawIdentity = id,
                DisplayName = id,
                State = "Provisioned"
            }).ToList();

            return new ComponentInventory
            {
                Discovered = true,
                Categories = new List<CategoryDiscoveryResult>
                {
                    new CategoryDiscoveryResult { Category = ComponentCategory.AppX, Status = InventoryStatus.Success, Items = items }
                }
            };
        }
    }

    // ============================================================
    // Parts D–I — Customize knowledge tab integration
    // ============================================================

    public class ComponentKnowledgeCustomizeTests
    {
        private static ComponentKnowledgeViewModel BuildSeeded()
        {
            var state = new AppState();
            var logger = new InMemoryLoggerService();
            var loc = new ResolvingLoc();
            var svc = new RawInventoryCiService(MakeMatchingRawInventory());
            var ciVm = new ComponentIntelligenceViewModel(state, logger, svc,
                new CuratedComponentCatalog(), loc);
            state.CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                State = ServicingWorkspaceState.Mounted,
                MountDirectory = @"C:\wf\mount"
            };
            // ADR-049 real-desktop semantics: only curated components PRESENT in the
            // image appear, so seed a discovery pass that matches several catalog defs.
            ciVm.DiscoverAsync().GetAwaiter().GetResult();
            return new ComponentKnowledgeViewModel(ciVm, state, logger, loc);
        }

        [Fact]
        public void Apps_Tab_Empty_Before_Discovery_No_Raw()
        {
            // ADR-049 real-desktop fix: before discovery the Apps tab must NOT show
            // catalog-only definitions (they may be absent from the image). It shows
            // the empty-await-discovery state instead of an empty detail card.
            var state = new AppState();
            var logger = new InMemoryLoggerService();
            var loc = new ResolvingLoc();
            var ciVm = new ComponentIntelligenceViewModel(state, logger, new NoDiscoveryCiService(),
                new CuratedComponentCatalog(), loc);
            var vm = new ComponentKnowledgeViewModel(ciVm, state, logger, loc);

            Assert.Empty(vm.Items);
            Assert.Equal(0, vm.CuratedCount);
            Assert.True(vm.IsEmpty);
            Assert.False(vm.HasInventory);
            Assert.False(string.IsNullOrEmpty(vm.EmptyStateText));
        }

        [Fact]
        public void Default_Sort_Places_RecommendedRemove_First_Then_By_Risk_Name()
        {
            var vm = BuildSeeded();
            var items = vm.Items.ToList();

            var seq = string.Join(",", items.Select(i => i.RecommendationLevel.ToString()));
            // Recommendation usefulness must be non-decreasing down the list.
            var orders = items.Select(i => i.RecommendationLevel switch
            {
                RecommendationLevel.RecommendedRemove => 1,
                RecommendationLevel.OptionalRemove => 2,
                RecommendationLevel.UsuallyKeep => 3,
                RecommendationLevel.AdvancedOnly => 4,
                RecommendationLevel.NeverRemove => 5,
                _ => 99
            }).ToList();
            for (var idx = 1; idx < orders.Count; idx++)
            {
                Assert.True(orders[idx] >= orders[idx - 1],
                    $"order not non-decreasing at {idx}: {seq}");
            }

            Assert.Equal(RecommendationLevel.RecommendedRemove, items[0].RecommendationLevel);
            var firstOptional = items.FindIndex(i => i.RecommendationLevel == RecommendationLevel.OptionalRemove);
            var lastRecommended = items.FindLastIndex(i => i.RecommendationLevel == RecommendationLevel.RecommendedRemove);
            if (firstOptional >= 0 && lastRecommended >= 0)
            {
                Assert.True(lastRecommended < firstOptional, $"RecommendedRemove not grouped first: {seq}");
            }
        }

        [Fact]
        public void Filter_Reduces_To_Matching_Recommendation()
        {
            var vm = BuildSeeded();
            vm.Filter = ComponentKnowledgeFilter.RecommendedRemove;

            Assert.All(vm.Items, i => Assert.Equal(RecommendationLevel.RecommendedRemove, i.RecommendationLevel));
            Assert.Equal(3, vm.Items.Count); // Weather + Clipchamp + Maps
        }

        [Fact]
        public void Recommendation_And_Risk_Reach_Customize_Item()
        {
            var vm = BuildSeeded();
            var weather = vm.Items.Single(i => i.Entry.Definition?.Id == "Weather");

            Assert.Equal(RecommendationLevel.RecommendedRemove, weather.RecommendationLevel);
            Assert.Equal(RiskLevel.Low, weather.RiskLevel);
            Assert.Equal("Recommendation.RecommendedRemove", weather.RecommendationCaption);
            Assert.Equal("Risk.Low", weather.RiskCaption);
        }

        [Fact]
        public void Hover_Card_Fields_Are_Populated()
        {
            var vm = BuildSeeded();
            var weather = vm.Items.Single(i => i.Entry.Definition?.Id == "Weather");

            Assert.False(string.IsNullOrEmpty(weather.KeepIfText));
            Assert.False(string.IsNullOrEmpty(weather.RemoveIfText));
            Assert.False(string.IsNullOrEmpty(weather.ImpactText));
            Assert.Equal("Restore.Easy", weather.RestoreCaption);
        }

        [Fact]
        public void Click_Detail_Does_Not_Change_Selection()
        {
            var parent = ComponentKnowledgeTestFactory.Make(new AppState(), new InMemoryLoggerService());
            var def = Catalog().Single(d => d.Id == "Weather");
            var item = new ComponentKnowledgeItem(CuratedEntry(def), new ResolvingLoc(), new AppState(), parent);

            Assert.False(item.IsSelected);
            item.ShowDetail();
            Assert.Same(item, parent.ActiveDetail);
            Assert.False(item.IsSelected); // opening detail never toggles the plan
        }

        [Fact]
        public async Task Standard_Mode_Hides_Raw_Unclassified_And_Protected()
        {
            var state = new AppState();
            var logger = new InMemoryLoggerService();
            var loc = new ResolvingLoc();
            var svc = new RawInventoryCiService(new ComponentInventory
            {
                Discovered = true,
                Categories = new List<CategoryDiscoveryResult>
                {
                    new CategoryDiscoveryResult
                    {
                        Category = ComponentCategory.AppX,
                        Status = InventoryStatus.Success,
                        Items = new List<IRawInventoryItem>
                        {
                            new RawAppxPackage { Category = ComponentCategory.AppX,
                                RawIdentity = "Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe",
                                DisplayName = "Weather", State = "Provisioned" },
                            new RawAppxPackage { Category = ComponentCategory.AppX,
                                RawIdentity = "Microsoft.Contoso_1.0_neutral_~_8wekyb3d8bbwe",
                                DisplayName = "Contoso", State = "Provisioned" }
                        }
                    },
                    new CategoryDiscoveryResult
                    {
                        Category = ComponentCategory.CbsPackage,
                        Status = InventoryStatus.Success,
                        Items = new List<IRawInventoryItem>
                        {
                            new RawCbsPackage { Category = ComponentCategory.CbsPackage,
                                RawIdentity = "Microsoft-Windows-ServicingStack-10.0.26100.1",
                                DisplayName = "Servicing Stack", State = "Installed" }
                        }
                    }
                }
            });

            var ciVm = new ComponentIntelligenceViewModel(state, logger, svc, new CuratedComponentCatalog(), loc);
            state.CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                State = ServicingWorkspaceState.Mounted,
                MountDirectory = @"C:\wf\mount"
            };
            await ciVm.DiscoverAsync();

            var knowledge = new ComponentKnowledgeViewModel(ciVm, state, logger, loc);
            // ADR-049: only curated components PRESENT in the image appear. Weather is
            // the single matched curated entry; Contoso (unclassified) and the servicing
            // stack (protected) stay out of the Customize primary surface.
            Assert.All(knowledge.Items, i => Assert.NotNull(i.Entry.Definition));
            var onlyCurated = Assert.Single(knowledge.Items);
            Assert.Equal("Weather", onlyCurated.Entry.Definition?.Id);
            Assert.DoesNotContain(knowledge.Items, i => i.Entry.RepresentativeRaw?.RawIdentity.Contains("Contoso") == true);
        }

        [Fact]
        public void Blocked_Explains_Why_When_NeverRemove()
        {
            var def = new ComponentDefinition
            {
                Id = "Core",
                DisplayNameKey = "Comp.Core.DisplayName",
                Recommendation = RecommendationLevel.NeverRemove,
                Risk = RiskLevel.Critical
            };
            var parent = ComponentKnowledgeTestFactory.Make(new AppState(), new InMemoryLoggerService());
            var item = new ComponentKnowledgeItem(CuratedEntry(def), new KeyLoc(), new AppState(), parent);

            Assert.False(item.IsSelectable);
            Assert.Equal("Component.Blocked", item.BlockReason);
        }

        [Fact]
        public void Blocked_Explains_Why_When_Unconfirmed_DefinitionNull()
        {
            var entry = new ComponentInventoryEntry
            {
                Definition = null,
                RawItems = new List<IRawInventoryItem>
                {
                    new RawAppxPackage { Category = ComponentCategory.AppX, RawIdentity = "Microsoft.Unknown_1.0", DisplayName = "Unknown", State = "Provisioned" }
                },
                Classification = ComponentClassification.DiscoveredUnclassified
            };
            var parent = ComponentKnowledgeTestFactory.Make(new AppState(), new InMemoryLoggerService());
            var item = new ComponentKnowledgeItem(entry, new KeyLoc(), new AppState(), parent);

            Assert.False(item.IsSelectable);
            Assert.Equal("Component.NotConfirmed", item.BlockReason);
            Assert.Equal("Component.NotConfirmed", item.ShortPurpose);
            Assert.Equal("Unknown", item.DisplayName); // falls back to raw DisplayName
        }

        [Fact]
        public void Protected_By_Critical_Risk_Is_Not_Selectable()
        {
            var def = new ComponentDefinition { Id = "P", Risk = RiskLevel.Critical, Recommendation = RecommendationLevel.UsuallyKeep };
            var parent = ComponentKnowledgeTestFactory.Make(new AppState(), new InMemoryLoggerService());
            var item = new ComponentKnowledgeItem(CuratedEntry(def), new KeyLoc(), new AppState(), parent);

            Assert.False(item.IsSelectable);
            Assert.Equal("Component.Blocked", item.BlockReason);
        }

        [Fact]
        public void No_Auto_Destructive_Selection_On_Load()
        {
            var state = new AppState();
            var logger = new InMemoryLoggerService();
            var loc = new ResolvingLoc();
            var ciVm = new ComponentIntelligenceViewModel(state, logger, new NoDiscoveryCiService(),
                new CuratedComponentCatalog(), loc);
            _ = new ComponentKnowledgeViewModel(ciVm, state, logger, loc);

            Assert.Null(state.CurrentCustomizationPlan);
        }

        [Fact]
        public void Official_And_Community_Evidence_Are_Separated()
        {
            var def = new ComponentDefinition
            {
                Id = "X",
                DisplayNameKey = "Comp.X.DisplayName",
                Provenance = new[]
                {
                    new KnowledgeClaim { Kind = KnowledgeClaimKind.Fact, TextKey = "Comp.X.Prov0",
                        Sources = new[] { new KnowledgeSource(KnowledgeSourceType.MicrosoftOfficial, "MS", ConfidenceLevel.High) } },
                    new KnowledgeClaim { Kind = KnowledgeClaimKind.Recommendation, TextKey = "Comp.X.Prov1",
                        Sources = new[] { new KnowledgeSource(KnowledgeSourceType.CommunityProject, "Win11Debloat", ConfidenceLevel.Low) } }
                }
            };
            var parent = ComponentKnowledgeTestFactory.Make(new AppState(), new InMemoryLoggerService());
            var item = new ComponentKnowledgeItem(CuratedEntry(def), new KeyLoc(), new AppState(), parent);

            Assert.Contains(item.OfficialEvidence, e => e.Contains("KnowledgeSource.MicrosoftOfficial"));
            Assert.DoesNotContain(item.OfficialEvidence, e => e.Contains("KnowledgeSource.CommunityProject"));
            Assert.Contains(item.CommunityEvidence, e => e.Contains("KnowledgeSource.CommunityProject"));
            Assert.DoesNotContain(item.CommunityEvidence, e => e.Contains("KnowledgeSource.MicrosoftOfficial"));
        }

        [Fact]
        public void Localization_Is_Wired_Recommendation_Caption_Differs_By_Language()
        {
            var def = Catalog().Single(d => d.Id == "Weather");
            var parentEn = ComponentKnowledgeTestFactory.Make(new AppState(), new InMemoryLoggerService());
            var parentZh = ComponentKnowledgeTestFactory.Make(new AppState(), new InMemoryLoggerService());

            var en = new ComponentKnowledgeItem(CuratedEntry(def), new LangLoc("_EN"), new AppState(), parentEn);
            var zh = new ComponentKnowledgeItem(CuratedEntry(def), new LangLoc("_ZH"), new AppState(), parentZh);

            Assert.Equal("Recommendation.RecommendedRemove_EN", en.RecommendationCaption);
            Assert.Equal("Recommendation.RecommendedRemove_ZH", zh.RecommendationCaption);
            Assert.NotEqual(en.RecommendationCaption, zh.RecommendationCaption);
        }

        // ============================================================
        // ADR-050 — master–detail state independence (unit level)
        // ============================================================

        [Fact]
        public void ActiveDetail_Flag_Is_Distinct_From_Removal_Selection()
        {
            // The inspection highlight is driven for items that belong to the VM's
            // list, so build a real seeded VM and use its rows.
            var vm = BuildSeeded();
            var item = vm.Items[0];

            // Opening the detail sets ONLY the inspection flag; selection is false.
            vm.ActiveDetail = item;
            Assert.True(item.IsActiveDetail);
            Assert.False(item.IsSelected);

            // Toggling the removal checkbox must NOT change the inspection highlight.
            item.IsSelected = true;
            Assert.True(item.IsSelected);
            Assert.True(item.IsActiveDetail);

            vm.ActiveDetail = null;
            Assert.False(item.IsActiveDetail);
            Assert.True(item.IsSelected); // removal selection survives detail close
        }

        [Fact]
        public void Checkbox_Toggle_Does_Not_Change_ActiveDetail()
        {
            var parent = ComponentKnowledgeTestFactory.Make(new AppState(), new InMemoryLoggerService());
            var weather = new ComponentKnowledgeItem(CuratedEntry(Catalog().Single(d => d.Id == "Weather")),
                new ResolvingLoc(), new AppState(), parent);
            var clip = new ComponentKnowledgeItem(CuratedEntry(Catalog().Single(d => d.Id == "Clipchamp")),
                new ResolvingLoc(), new AppState(), parent);

            parent.ActiveDetail = weather;
            // Toggle a DIFFERENT row's checkbox (removal action only).
            clip.IsSelected = true;

            Assert.Same(weather, parent.ActiveDetail); // detail unchanged
            Assert.True(clip.IsSelected);
        }

        [Fact]
        public void Removal_Selections_Survive_Detail_Close()
        {
            var parent = ComponentKnowledgeTestFactory.Make(new AppState(), new InMemoryLoggerService());
            var weather = new ComponentKnowledgeItem(CuratedEntry(Catalog().Single(d => d.Id == "Weather")),
                new ResolvingLoc(), new AppState(), parent);
            var clip = new ComponentKnowledgeItem(CuratedEntry(Catalog().Single(d => d.Id == "Clipchamp")),
                new ResolvingLoc(), new AppState(), parent);

            weather.IsSelected = true;
            clip.IsSelected = true;
            parent.ActiveDetail = weather;
            parent.ClearDetailCommand.Execute(null);

            Assert.Null(parent.ActiveDetail);
            Assert.True(weather.IsSelected); // selections untouched by closing detail
            Assert.True(clip.IsSelected);
        }

        [Fact]
        public void ActiveDetail_Closes_When_Filtered_Out_Selection_Intact()
        {
            var vm = BuildSeeded();
            var weather = vm.Items.Single(i => i.Entry.Definition?.Id == "Weather");
            weather.IsSelected = true;

            vm.ActiveDetail = weather;
            Assert.Same(weather, vm.ActiveDetail);

            // Switch filter so the open detail item leaves the visible set.
            vm.Filter = ComponentKnowledgeFilter.OptionalRemove; // Weather is RecommendedRemove
            Assert.Null(vm.ActiveDetail);      // detail closes
            Assert.True(weather.IsSelected);   // but its removal selection survives
        }

        [Fact]
        public void Filter_Keeps_Detail_Open_While_Item_Visible()
        {
            var vm = BuildSeeded();
            var av1 = vm.Items.Single(i => i.Entry.Definition?.Id == "AV1VideoExtension");

            vm.ActiveDetail = av1;
            vm.Filter = ComponentKnowledgeFilter.OptionalRemove; // AV1 is OptionalRemove → still visible
            Assert.Contains(av1, vm.Items);
            Assert.Same(av1, vm.ActiveDetail); // remains open

            vm.Filter = ComponentKnowledgeFilter.RecommendedRemove; // now filtered out
            Assert.DoesNotContain(av1, vm.Items);
            Assert.Null(vm.ActiveDetail);
        }

        [Fact]
        public void Blocked_Row_Can_Open_Detail_And_Checkbox_Disabled()
        {
            var parent = ComponentKnowledgeTestFactory.Make(new AppState(), new InMemoryLoggerService());
            var def = new ComponentDefinition
            {
                Id = "Core",
                DisplayNameKey = "Comp.Core.DisplayName",
                Recommendation = RecommendationLevel.NeverRemove,
                Risk = RiskLevel.Critical
            };
            var item = new ComponentKnowledgeItem(CuratedEntry(def), new KeyLoc(), new AppState(), parent);

            // A blocked row is still inspectable (detail opens) …
            parent.ShowDetailCommand.Execute(item);
            Assert.Same(item, parent.ActiveDetail);
            // … but its checkbox is disabled (no removal selection).
            Assert.False(item.IsSelectable);
        }
    }

    // ============================================================
    // Part M — Phase regression + wiring + XAML load
    // ============================================================

    public class PhaseRegression11p2Tests
    {
        [Fact]
        public void Customize_Step_Has_No_Knowledge_Tab_And_Apps_Is_Knowledge()
        {
            var knowledge = ComponentKnowledgeTestFactory.Make(new AppState(), new InMemoryLoggerService());
            var components = new ComponentsViewModel(new AppState(), new InMemoryLoggerService(),
                new FakeCustomizationDiscoveryService(), new FakeCustomizationDefinitionProvider());
            var privacy = new PrivacyViewModel(new AppState(), new InMemoryLoggerService(), new FakeCustomizationDefinitionProvider());
            var system = new SystemViewModel(new AppState(), new InMemoryLoggerService(), new FakeCustomizationDefinitionProvider());
            var comingSoon = new ComingSoonViewModel();

            var customize = new CustomizeStepViewModel(components, privacy, system, comingSoon, knowledge);

            // ADR-048: the separate "Component Knowledge" tab is REMOVED. The
            // knowledge engine is surfaced as the Apps tab content instead, so the
            // removal decision is made where the component lives.
            Assert.DoesNotContain(customize.Tabs, t => t.HeaderKey == "Customize.Tab.Knowledge");
            Assert.DoesNotContain(customize.Tabs,
                t => t.Content is ComponentKnowledgeViewModel && t.HeaderKey != "Customize.Tab.Apps");

            // Apps tab (index 0) reuses the SAME ComponentKnowledgeViewModel instance
            // — knowledge is the decision surface, not a duplicated ViewModel.
            var appsTab = customize.Tabs[0];
            Assert.Equal("Customize.Tab.Apps", appsTab.HeaderKey);
            var appsContent = Assert.IsType<ComponentKnowledgeViewModel>(appsTab.Content);
            Assert.Same(knowledge, appsContent);
        }

        [Fact]
        public async Task Apps_Tab_Raw_Identity_Hidden_From_Curated_DisplayName()
        {
            var state = new AppState();
            var logger = new InMemoryLoggerService();
            var loc = new ResolvingLoc();
            var svc = new RawInventoryCiService(new ComponentInventory
            {
                Discovered = true,
                Categories = new List<CategoryDiscoveryResult>
                {
                    new CategoryDiscoveryResult
                    {
                        Category = ComponentCategory.AppX,
                        Status = InventoryStatus.Success,
                        Items = new List<IRawInventoryItem>
                        {
                            new RawAppxPackage { Category = ComponentCategory.AppX,
                                RawIdentity = "Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe",
                                DisplayName = "Weather", State = "Provisioned" }
                        }
                    }
                }
            });
            var ciVm = new ComponentIntelligenceViewModel(state, logger, svc, new CuratedComponentCatalog(), loc);
            state.CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                State = ServicingWorkspaceState.Mounted,
                MountDirectory = @"C:\wf\mount"
            };
            await ciVm.DiscoverAsync();
            var knowledge = new ComponentKnowledgeViewModel(ciVm, state, logger, loc);

            var weather = knowledge.Items.Single(i => i.Entry.Definition?.Id == "Weather");
            // The human name is shown, NOT the raw AppX identity (standard mode hides it).
            Assert.NotEqual("Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe", weather.DisplayName);
            Assert.DoesNotContain("8wekyb3d8bbwe", weather.DisplayName);
            Assert.Equal("Weather.DisplayName", weather.DisplayName); // ResolvingLoc strips the "Comp." prefix
        }

        [Fact]
        public void ShowDetailCommand_Sets_ActiveDetail_Without_Changing_Selection()
        {
            var parent = ComponentKnowledgeTestFactory.Make(new AppState(), new InMemoryLoggerService());
            var def = Catalog().Single(d => d.Id == "Weather");
            var item = new ComponentKnowledgeItem(CuratedEntry(def), new ResolvingLoc(), new AppState(), parent);

            Assert.False(item.IsSelected);
            // Command path (mouse / keyboard / touch via CommandParameter) — not hover-only.
            parent.ShowDetailCommand.Execute(item);
            Assert.Same(item, parent.ActiveDetail);
            Assert.False(item.IsSelected); // opening detail never toggles the plan
        }

        [Fact]
        public async Task App_Selection_Toggles_Plan_Operation()
        {
            var state = new AppState();
            var logger = new InMemoryLoggerService();
            var loc = new ResolvingLoc();
            var svc = new RawInventoryCiService(new ComponentInventory
            {
                Discovered = true,
                Categories = new List<CategoryDiscoveryResult>
                {
                    new CategoryDiscoveryResult
                    {
                        Category = ComponentCategory.AppX,
                        Status = InventoryStatus.Success,
                        Items = new List<IRawInventoryItem>
                        {
                            new RawAppxPackage { Category = ComponentCategory.AppX,
                                RawIdentity = "Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe",
                                DisplayName = "Weather", State = "Provisioned" }
                        }
                    }
                }
            });
            var ciVm = new ComponentIntelligenceViewModel(state, logger, svc, new CuratedComponentCatalog(), loc);
            state.CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                State = ServicingWorkspaceState.Mounted,
                MountDirectory = @"C:\wf\mount"
            };
            await ciVm.DiscoverAsync();
            var knowledge = new ComponentKnowledgeViewModel(ciVm, state, logger, loc);

            var weather = knowledge.Items.Single(i => i.Entry.Definition?.Id == "Weather");
            Assert.True(weather.IsSelectable);
            weather.IsSelected = true;

            var op = state.CurrentCustomizationPlan!.Operations
                .Single(o => o.OperationId.StartsWith("appx|") && o.IsSelected);
            Assert.Equal(CustomizationCategory.App, op.Category);
            Assert.Equal(CustomizationOperationType.RemoveProvisionedAppx, op.OperationType);
        }

        [Fact]
        public async Task WindowsComponent_Selection_Toggles_Pkg_Operation()
        {
            var state = new AppState();
            var discovery = new FakeCustomizationDiscoveryService
            {
                Inventory = new DiscoveryInventory
                {
                    Discovered = true,
                    WindowsPackages = new List<DiscoveredWindowsPackage>
                    {
                        new DiscoveredWindowsPackage
                        {
                            PackageIdentity = "Microsoft-Windows-TestComponent~31bf3856ad364e35",
                            DisplayName = "Test Component",
                            Risk = RiskClass.Removable
                        }
                    }
                }
            };
            var components = new ComponentsViewModel(state, new InMemoryLoggerService(),
                discovery, new FakeCustomizationDefinitionProvider());
            state.CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                State = ServicingWorkspaceState.Mounted,
                MountDirectory = @"C:\wf\mount"
            };
            await components.DiscoverAsync();

            var pkg = Assert.Single(components.WindowsPackages);
            Assert.True(pkg.CanSelect);
            pkg.IsSelected = true;

            var op = state.CurrentCustomizationPlan!.Operations
                .Single(o => o.OperationId == "pkg|Microsoft-Windows-TestComponent~31bf3856ad364e35" && o.IsSelected);
            Assert.Equal(CustomizationCategory.Package, op.Category);
            Assert.Equal(CustomizationOperationType.RemovePackage, op.OperationType);
        }

        [Fact]
        public void PlanSync_Refuses_Protected_Operation_Phase10_Safety()
        {
            var state = new AppState();
            PlanSync.Toggle(state, "appx|Microsoft-Windows-ServicingStack", selected: true, () => new CustomizationOperation
            {
                OperationId = "appx|Microsoft-Windows-ServicingStack",
                OperationType = CustomizationOperationType.RemoveProvisionedAppx,
                Risk = RiskClass.Protected,
                TargetIdentifier = "Microsoft-Windows-ServicingStack"
            });

            Assert.NotNull(state.CurrentCustomizationPlan);
            Assert.Empty(state.CurrentCustomizationPlan!.Operations);
        }

        [Fact]
        public void ComponentKnowledgeView_Loads_Without_XamlParseException()
        {
            RunSta(() =>
            {
                var loc = new FakeLocalizationService();
                InstallAppResources(loc);
                var view = new ComponentKnowledgeView();
                view.Measure(new Size(1000, 800));
                view.Arrange(new Rect(0, 0, 1000, 800));
                Assert.NotNull(view.Content); // root-cause guard: code-behind must run
            });
        }

        [Fact]
        public void ComponentKnowledgeView_Loads_With_Real_DataContext()
        {
            RunSta(() =>
            {
                var culture = CultureInfo.GetCultureInfo("en");
                var loc = new ResourceManagerLocalizationService(
                    new System.Resources.ResourceManager("WinForge.App.Resources.Strings", typeof(ComponentKnowledgeView).Assembly), culture);
                InstallAppResources(loc);

                var state = new AppState();
                var ciVm = new ComponentIntelligenceViewModel(state, new InMemoryLoggerService(),
                    new NoDiscoveryCiService(), new CuratedComponentCatalog(), loc);
                var knowledge = new ComponentKnowledgeViewModel(ciVm, state, new InMemoryLoggerService(), loc);

                var view = new ComponentKnowledgeView { DataContext = knowledge };
                view.Measure(new Size(1000, 800));
                view.Arrange(new Rect(0, 0, 1000, 800));
                Assert.NotNull(view.Content);
            });
        }

        // ============================================================
        // ADR-049 — real-desktop defect regression (Apps decision surface)
        // ============================================================

        [Fact]
        public async Task Unified_Discovery_Populates_Apps_Knowledge_And_Components_NonDestructive()
        {
            var state = new AppState();
            var logger = new InMemoryLoggerService();
            var loc = new ResolvingLoc();

            var components = new ComponentsViewModel(state, logger,
                new FakeCustomizationDiscoveryService
                {
                    Inventory = new DiscoveryInventory
                    {
                        Discovered = true,
                        AppxPackages = new List<DiscoveredAppxPackage>
                        {
                            new()
                            {
                                PackageName = "Microsoft.BingWeather_4.53_neutral_~_8wekyb3d8bbwe",
                                DisplayName = "Weather",
                                Risk = RiskClass.Removable
                            }
                        }
                    }
                },
                new FakeCustomizationDefinitionProvider());

            var ciVm = new ComponentIntelligenceViewModel(state, logger,
                new RawInventoryCiService(MakeMatchingRawInventory()),
                new CuratedComponentCatalog(), loc);
            var knowledge = new ComponentKnowledgeViewModel(ciVm, state, logger, loc);

            var customize = new CustomizeStepViewModel(components,
                new PrivacyViewModel(state, logger, new FakeCustomizationDefinitionProvider()),
                new SystemViewModel(state, logger, new FakeCustomizationDefinitionProvider()),
                new ComingSoonViewModel(), knowledge);

            state.CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                State = ServicingWorkspaceState.Mounted,
                MountDirectory = @"C:\wf\mount"
            };

            // ONE Discover button drives BOTH the Components discovery and the CI
            // knowledge discovery (ADR-049). The user never discovers twice.
            Assert.True(customize.CanDiscover);
            await ((AsyncRelayCommand)customize.DiscoverCommand).ExecuteAsync(null);

            Assert.NotEmpty(components.AppxPackages);   // Components discovery worked
            Assert.NotEmpty(knowledge.Items);           // Knowledge discovery worked
            Assert.Contains(knowledge.Items, i => i.Entry.Definition?.Id == "Weather");
            // Read-only: discovery alone adds NO plan operations (no destructive servicing).
            Assert.True(state.CurrentCustomizationPlan is null
                || state.CurrentCustomizationPlan.Operations.Count == 0);
        }

        [Fact]
        public async Task Curated_Present_Components_Visible_Absent_Excluded()
        {
            var state = new AppState();
            var logger = new InMemoryLoggerService();
            var loc = new ResolvingLoc();
            var ciVm = new ComponentIntelligenceViewModel(state, logger,
                new RawInventoryCiService(MakeMatchingRawInventory()),
                new CuratedComponentCatalog(), loc);
            state.CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                State = ServicingWorkspaceState.Mounted,
                MountDirectory = @"C:\wf\mount"
            };
            await ciVm.DiscoverAsync();
            var knowledge = new ComponentKnowledgeViewModel(ciVm, state, logger, loc);

            // Only the 4 curated components PRESENT in the image appear (Weather,
            // Clipchamp, Maps, AV1). Catalog definitions absent from the image
            // (e.g. Calculator) are NOT shown as removable rows.
            Assert.Equal(4, knowledge.Items.Count);
            Assert.Contains(knowledge.Items, i => i.Entry.Definition?.Id == "Weather");
            Assert.Contains(knowledge.Items, i => i.Entry.Definition?.Id == "Clipchamp");
            Assert.Contains(knowledge.Items, i => i.Entry.Definition?.Id == "Maps");
            Assert.Contains(knowledge.Items, i => i.Entry.Definition?.Id == "AV1VideoExtension");
            Assert.DoesNotContain(knowledge.Items, i => i.Entry.Definition?.Id == "Calculator");
            Assert.False(knowledge.IsEmpty);
        }

        [Fact]
        public async Task Empty_State_After_Discovery_No_Curated_Matches()
        {
            var state = new AppState();
            var logger = new InMemoryLoggerService();
            var loc = new ResolvingLoc();
            var raw = new ComponentInventory
            {
                Discovered = true,
                Categories = new List<CategoryDiscoveryResult>
                {
                    new()
                    {
                        Category = ComponentCategory.AppX,
                        Status = InventoryStatus.Success,
                        Items = new List<IRawInventoryItem>
                        {
                            new RawAppxPackage
                            {
                                Category = ComponentCategory.AppX,
                                RawIdentity = "Microsoft.Contoso_1.0_neutral_~_8wekyb3d8bbwe",
                                DisplayName = "Contoso",
                                State = "Provisioned"
                            }
                        }
                    }
                }
            };
            var ciVm = new ComponentIntelligenceViewModel(state, logger,
                new RawInventoryCiService(raw), new CuratedComponentCatalog(), loc);
            state.CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                State = ServicingWorkspaceState.Mounted,
                MountDirectory = @"C:\wf\mount"
            };
            await ciVm.DiscoverAsync();
            var knowledge = new ComponentKnowledgeViewModel(ciVm, state, logger, loc);

            // Discovery happened but no curated-present components → empty state, NOT
            // an empty detail card.
            Assert.True(knowledge.HasInventory);
            Assert.True(knowledge.IsEmpty);
            Assert.Empty(knowledge.Items);
            Assert.False(string.IsNullOrEmpty(knowledge.EmptyStateText));
        }

        [Fact]
        public void ClearDetail_Hides_Detail_Without_Changing_Selection()
        {
            var parent = ComponentKnowledgeTestFactory.Make(new AppState(), new InMemoryLoggerService());
            var def = Catalog().Single(d => d.Id == "Weather");
            var item = new ComponentKnowledgeItem(CuratedEntry(def), new ResolvingLoc(), new AppState(), parent);

            parent.ShowDetailCommand.Execute(item);
            Assert.Same(item, parent.ActiveDetail);
            parent.ClearDetailCommand.Execute(null);
            Assert.Null(parent.ActiveDetail);
        }

        [Fact]
        public void Component_Inspector_Still_Shows_Catalog_Only_Rows()
        {
            // ADR-049 did NOT change the matcher: the Component Intelligence inspection
            // surface (Stage 11.1) still seeds catalog-only rows so users can see what
            // WinForge understands, even before discovery. Only the Customize Apps tab
            // filters to present-in-image curated.
            var ciVm = new ComponentIntelligenceViewModel(new AppState(), new InMemoryLoggerService(),
                new NoDiscoveryCiService(), new CuratedComponentCatalog(), new ResolvingLoc());
            Assert.Equal(22, ciVm.Entries.Count);
            Assert.All(ciVm.Entries, e => Assert.Equal(ComponentClassification.Curated, e.Entry.Classification));
        }

        [Fact]
        public void Other_Customize_Tabs_Unchanged_Six_Tabs()
        {
            var knowledge = ComponentKnowledgeTestFactory.Make(new AppState(), new InMemoryLoggerService());
            var components = new ComponentsViewModel(new AppState(), new InMemoryLoggerService(),
                new FakeCustomizationDiscoveryService(), new FakeCustomizationDefinitionProvider());
            var customize = new CustomizeStepViewModel(components,
                new PrivacyViewModel(new AppState(), new InMemoryLoggerService(), new FakeCustomizationDefinitionProvider()),
                new SystemViewModel(new AppState(), new InMemoryLoggerService(), new FakeCustomizationDefinitionProvider()),
                new ComingSoonViewModel(), knowledge);

            Assert.Equal(6, customize.Tabs.Count);
            Assert.Equal("Customize.Tab.Apps", customize.Tabs[0].HeaderKey);
            Assert.Equal("Customize.Tab.Components", customize.Tabs[1].HeaderKey);
            Assert.Equal("Customize.Tab.Services", customize.Tabs[2].HeaderKey);
            Assert.Equal("Customize.Tab.Privacy", customize.Tabs[3].HeaderKey);
            Assert.Equal("Customize.Tab.System", customize.Tabs[4].HeaderKey);
            Assert.Equal("Customize.Tab.Experience", customize.Tabs[5].HeaderKey);
        }

        [Fact]
        public void View_List_NonZero_Height_And_Detail_Collapsed_When_No_ActiveDetail()
        {
            RunSta(() =>
            {
                var loc = new FakeLocalizationService();
                InstallAppResources(loc);

                var state = new AppState();
                var ciVm = new ComponentIntelligenceViewModel(state, new InMemoryLoggerService(),
                    new RawInventoryCiService(MakeMatchingRawInventory()),
                    new CuratedComponentCatalog(), loc);
                state.CurrentServicingWorkspace = new ImageServicingWorkspace
                {
                    State = ServicingWorkspaceState.Mounted,
                    MountDirectory = @"C:\wf\mount"
                };
                ciVm.DiscoverAsync().GetAwaiter().GetResult();
                var knowledge = new ComponentKnowledgeViewModel(ciVm, state, new InMemoryLoggerService(), loc);

                var view = new ComponentKnowledgeView { DataContext = knowledge };
                view.Measure(new Size(1200, 700));
                view.Arrange(new Rect(0, 0, 1200, 700));
                view.UpdateLayout();

                // The list is the primary surface and must have real height + items.
                var list = FindVisual<ListView>(view);
                Assert.NotNull(list);
                Assert.True(list!.ActualHeight > 0, $"list height {list.ActualHeight}");
                Assert.NotEmpty(list.Items);

                // No detail selected → the detail side panel ContentControl must be
                // Collapsed (NullToVis), never an empty panel squeezing the list.
                Assert.Null(knowledge.ActiveDetail);
                var detail = FindVisual<ContentControl>(view,
                    cc => cc.GetType() == typeof(ContentControl) && cc.ContentTemplate is not null);
                Assert.NotNull(detail);
                Assert.Equal(Visibility.Collapsed, detail!.Visibility);

                // Opening detail makes the side panel visible; closing collapses it again.
                knowledge.ActiveDetail = knowledge.Items[0];
                view.UpdateLayout();
                Assert.Equal(Visibility.Visible, detail.Visibility);
                knowledge.ActiveDetail = null;
                view.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, detail.Visibility);
            });
        }

        [Fact]
        public void Recommendation_Risk_Captions_Resolve_Under_ZhCn()
        {
            RunSta(() =>
            {
                var loc = new ResourceManagerLocalizationService(
                    new System.Resources.ResourceManager("WinForge.App.Resources.Strings",
                        typeof(ComponentKnowledgeView).Assembly),
                    CultureInfo.GetCultureInfo("zh-CN"));
                InstallAppResources(loc);

                var state = new AppState();
                var ciVm = new ComponentIntelligenceViewModel(state, new InMemoryLoggerService(),
                    new RawInventoryCiService(MakeMatchingRawInventory()),
                    new CuratedComponentCatalog(), loc);
                state.CurrentServicingWorkspace = new ImageServicingWorkspace
                {
                    State = ServicingWorkspaceState.Mounted,
                    MountDirectory = @"C:\wf\mount"
                };
                ciVm.DiscoverAsync().GetAwaiter().GetResult();
                var knowledge = new ComponentKnowledgeViewModel(ciVm, state, new InMemoryLoggerService(), loc);

                var weather = knowledge.Items.Single(i => i.Entry.Definition?.Id == "Weather");
                // Captions resolve to real zh-CN text (not the raw key) — the badges
                // render visible text, never blank/white-on-empty.
                Assert.False(string.IsNullOrEmpty(weather.RecommendationCaption));
                Assert.False(string.IsNullOrEmpty(weather.RiskCaption));
                Assert.NotEqual("Recommendation.RecommendedRemove", weather.RecommendationCaption);
                Assert.NotEqual("Risk.Low", weather.RiskCaption);
            });
        }

        // ============================================================
        // ADR-050 — master–detail row selection (no per-row Details button)
        // ============================================================

        [Fact]
        public void Details_Button_Removed_From_Rows()
        {
            RunSta(() =>
            {
                var (view, _, list) = BuildLoadedView();
                // No per-row Details button anywhere inside the decision list
                // (the only remaining button is the detail-panel ✕, outside the list).
                Assert.Null(FindVisual<Button>(list));
            });
        }

        [Fact]
        public void Row_Click_Opens_Detail()
        {
            RunSta(() =>
            {
                var (view, knowledge, list) = BuildLoadedView();
                var container = Container(list, 0);
                RaiseMouseLeftButtonUp(list, container);
                Assert.Same(knowledge.Items[0], knowledge.ActiveDetail);
            });
        }

        [Fact]
        public void Row_Click_Switches_ActiveDetail_And_Stays_Open()
        {
            RunSta(() =>
            {
                var (view, knowledge, list) = BuildLoadedView();
                RaiseMouseLeftButtonUp(list, Container(list, 0));
                Assert.Same(knowledge.Items[0], knowledge.ActiveDetail);

                RaiseMouseLeftButtonUp(list, Container(list, 1));
                Assert.Same(knowledge.Items[1], knowledge.ActiveDetail); // switched

                var detail = FindVisual<ContentControl>(view,
                    cc => cc.GetType() == typeof(ContentControl) && cc.ContentTemplate is not null);
                Assert.NotNull(detail);
                Assert.Equal(Visibility.Visible, detail!.Visibility); // panel stays open
            });
        }

        [Fact]
        public void Row_Click_Does_Not_Change_RemovalSelected()
        {
            RunSta(() =>
            {
                var (view, knowledge, list) = BuildLoadedView();
                knowledge.Items[0].IsSelected = true;
                Assert.True(knowledge.Items[0].IsSelected);

                // Click a DIFFERENT row to inspect it.
                RaiseMouseLeftButtonUp(list, Container(list, 1));
                Assert.Same(knowledge.Items[1], knowledge.ActiveDetail);
                // The removal selection of row 0 is untouched by the inspection click.
                Assert.True(knowledge.Items[0].IsSelected);
            });
        }

        [Fact]
        public void Checkbox_Click_Does_Not_Open_Detail()
        {
            RunSta(() =>
            {
                var (view, knowledge, list) = BuildLoadedView();
                knowledge.ActiveDetail = knowledge.Items[0];
                Assert.Same(knowledge.Items[0], knowledge.ActiveDetail);

                // Click the checkbox of a different row — must NOT switch the detail.
                var cb = FindVisual<CheckBox>(Container(list, 1));
                Assert.NotNull(cb);
                RaiseMouseLeftButtonUp(list, cb!);
                Assert.Same(knowledge.Items[0], knowledge.ActiveDetail);
            });
        }

        [Fact]
        public void Enter_On_Row_Opens_Detail()
        {
            RunSta(() =>
            {
                var (view, knowledge, list) = BuildLoadedView();
                using var hs = new HwndSource(new HwndSourceParameters("enter") { Width = 800, Height = 600 });
                hs.RootVisual = view;
                var source = PresentationSource.FromVisual(view);
                Assert.NotNull(source);
                RaiseKeyDown(list, Container(list, 2), source, Key.Enter);
                Assert.Same(knowledge.Items[2], knowledge.ActiveDetail);
            });
        }

        [Fact]
        public void Enter_Does_Not_Toggle_Removal_Selection()
        {
            RunSta(() =>
            {
                var (view, knowledge, list) = BuildLoadedView();
                using var hs = new HwndSource(new HwndSourceParameters("enter2") { Width = 800, Height = 600 });
                hs.RootVisual = view;
                var source = PresentationSource.FromVisual(view);
                Assert.NotNull(source);
                // Row 0 not yet selected; pressing Enter on it must open detail, not select.
                RaiseKeyDown(list, Container(list, 0), source, Key.Enter);
                Assert.Same(knowledge.Items[0], knowledge.ActiveDetail);
                Assert.False(knowledge.Items[0].IsSelected);
            });
        }

        [Fact]
        public void No_Horizontal_Scroll_Dependency_At_Normal_Width()
        {
            RunSta(() =>
            {
                var (view, knowledge, list) = BuildLoadedView();
                Assert.Equal(ScrollBarVisibility.Disabled,
                    ScrollViewer.GetHorizontalScrollBarVisibility(list));
                Assert.True(list.ActualWidth > 500, $"list width {list.ActualWidth}");
            });
        }

        // ---- ADR-050 STA helpers ----

        private static (ComponentKnowledgeView View, ComponentKnowledgeViewModel Vm, ListView List) BuildLoadedView()
        {
            var loc = new FakeLocalizationService();
            InstallAppResources(loc);
            var state = new AppState();
            var ciVm = new ComponentIntelligenceViewModel(state, new InMemoryLoggerService(),
                new RawInventoryCiService(MakeMatchingRawInventory()),
                new CuratedComponentCatalog(), loc);
            state.CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                State = ServicingWorkspaceState.Mounted,
                MountDirectory = @"C:\wf\mount"
            };
            ciVm.DiscoverAsync().GetAwaiter().GetResult();
            var knowledge = new ComponentKnowledgeViewModel(ciVm, state, new InMemoryLoggerService(), loc);

            var view = new ComponentKnowledgeView { DataContext = knowledge };
            view.Measure(new Size(1200, 700));
            view.Arrange(new Rect(0, 0, 1200, 700));
            view.UpdateLayout();

            var list = FindVisual<ListView>(view);
            if (list is null)
            {
                throw new Exception("ListView not found in visual tree");
            }

            return (view, knowledge, list);
        }

        private static ListViewItem Container(ListView list, int index)
        {
            list.UpdateLayout();
            var container = list.ItemContainerGenerator.ContainerFromIndex(index) as ListViewItem;
            if (container is null)
            {
                throw new Exception($"No ListViewItem container at index {index}");
            }

            return container;
        }

        private static void RaiseMouseLeftButtonUp(UIElement target, DependencyObject? source)
        {
            target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent,
                Source = source ?? target
            });
        }

        private static void RaiseKeyDown(ListView list, ListViewItem container, PresentationSource source, Key key)
        {
            list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
            {
                RoutedEvent = UIElement.KeyDownEvent,
                Source = container
            });
        }

        // ---- STA + resource helpers (mirrors ComponentIntelligenceXamlLoadRegressionTests) ----

        private static void RunSta(Action action)
        {
            Exception? captured = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { captured = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (captured is not null)
            {
                throw new Exception("STA load failed — see inner exception for the full WPF chain.", captured);
            }
        }

        private static T? FindVisual<T>(DependencyObject root, Predicate<T>? match = null) where T : DependencyObject
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t && (match is null || match(t)))
                {
                    return t;
                }

                var found = FindVisual<T>(child, match);
                if (found is not null)
                {
                    return found;
                }
            }

            return null;
        }

        private static void InstallAppResources(ILocalizationService loc)
        {
            if (Application.Current is null)
            {
                new Application();
            }

            var res = Application.Current!.Resources;
            if (!res.Contains("locKey")) res.Add("locKey", new LocKeyMultiConverter());
            if (!res.Contains("BoolToVis")) res.Add("BoolToVis", new BooleanToVisibilityConverter());
            if (!res.Contains("BoolToVisInv")) res.Add("BoolToVisInv", new BooleanToVisibilityInverseConverter());
            if (!res.Contains("NullToVis")) res.Add("NullToVis", new NullToVisibilityConverter());
            if (!res.Contains("NullEmptyToVis")) res.Add("NullEmptyToVis", new StringNullOrEmptyToVisibilityConverter());
            if (!res.Contains("StatusTile")) res.Add("StatusTile", new Style(typeof(Border)));
            if (!res.Contains("PrimaryButton")) res.Add("PrimaryButton", new Style(typeof(Button)));
            if (!res.Contains("FieldLabel")) res.Add("FieldLabel", new Style(typeof(TextBlock)));
            if (!res.Contains("recColor")) res.Add("recColor", new RecommendationToColorConverter());
            if (!res.Contains("riskColor")) res.Add("riskColor", new RiskToColorConverter());
            res["Loc"] = loc;
        }
    }

    // ---- CI service that returns a fixed inventory (for Standard-hides-raw test) ----

    private sealed class RawInventoryCiService : IComponentIntelligenceService
    {
        private readonly ComponentInventory _result;
        public RawInventoryCiService(ComponentInventory result) => _result = result;
        public Task<ComponentInventory> DiscoverAsync(
            ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }
}
