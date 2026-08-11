using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WinForge.App.Converters;
using WinForge.App.Localization;
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
            var ciVm = new ComponentIntelligenceViewModel(state, logger, new NoDiscoveryCiService(),
                new CuratedComponentCatalog(), loc);
            return new ComponentKnowledgeViewModel(ciVm, state, logger, loc);
        }

        [Fact]
        public void Knowledge_Tab_Seeded_With_All_Curated_No_Raw()
        {
            var vm = BuildSeeded();
            Assert.Equal(22, vm.CuratedCount);
            Assert.Equal(22, vm.Items.Count);
            Assert.All(vm.Items, i => Assert.NotNull(i.Entry.Definition));
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
            // Only curated (well-understood) components are offered; raw unclassified
            // and protected objects stay hidden in the Customize primary surface.
            Assert.All(knowledge.Items, i => Assert.NotNull(i.Entry.Definition));
            Assert.Equal(22, knowledge.Items.Count);
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
        public void Apps_Tab_Raw_Identity_Hidden_From_Curated_DisplayName()
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
