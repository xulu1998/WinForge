using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.App.Views;
using WinForge.Core.Models;
using WinForge.Core.Profiles;
using WinForge.Core.Services;
using WinForge.Infrastructure.ComponentIntelligence;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.Profiles;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Stage 11.4 — Scenario profile / recommended configuration engine (ADR-057..060).
/// Covers Part R test areas 1-32: model, precedence, gaming/developer/office/
/// lightweight rules, conflict resolution, user-override semantics, and the
/// profile selector / preview / adopt UX. Regression areas 33-40 stay covered by
/// the existing Stage 11.2 / Stage 11.3 / Phase 3 / Phase 10 suites.
/// </summary>
public sealed class Stage11p4Tests
{
    // =====================================================================
    // MODEL (R1-R4)
    // =====================================================================

    [Fact]
    public void ProfileDefinition_Model_RoundTrip()
    {
        var profile = new ProfileDefinition
        {
            Id = "Gaming",
            DisplayNameKey = "Profile.Gaming.DisplayName",
            DescriptionKey = "Profile.Gaming.Description",
            Scenarios = new[] { ProfileScenario.Gaming, ProfileScenario.XboxGamePass },
            RequiredCapabilities = new[] { "Wsl" },
            PreferredCapabilities = new[] { "Terminal" },
            AvoidedComponents = new[] { "Weather" },
            RecommendationOverrides = new[]
            {
                new ProfileRecommendationOverride { TargetId = "XboxApp", Intent = ProfileIntent.Keep, ReasonKey = "Profile.Reason.Gaming.Xbox", Tier = 5 },
            },
        };

        Assert.Equal("Gaming", profile.Id);
        Assert.Equal(ProfileIntent.Keep, profile.RecommendationOverrides[0].Intent);
        Assert.Equal("XboxApp", profile.RecommendationOverrides[0].TargetId);
        Assert.Equal(ProfileScenario.XboxGamePass, profile.Scenarios[1]);
        Assert.Equal("Wsl", profile.RequiredCapabilities[0]);
        Assert.Equal("Weather", profile.AvoidedComponents[0]);
    }

    [Fact]
    public void Multiple_Scenario_Combination_Applies_Both_Profiles()
    {
        // Gaming keeps XboxGipSvc; Lightweight trims it. With BOTH selected the
        // engine considers both rule sets (keep wins, conflict recorded).
        var gaming = P("Gaming", ovs: new[] { ("XboxGipSvc", ProfileIntent.Keep, "Profile.Reason.Gaming.XboxServices") });
        var lightweight = P("Lightweight", avoided: new[] { "XboxGipSvc" });

        var result = Eval(Input("XboxGipSvc"), profiles: new[] { gaming, lightweight });

        Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, result.Level);
        Assert.True(result.WasProfileDriven);
        Assert.True(result.HasConflict);
    }

    [Fact]
    public void Default_Recommendation_Is_Unchanged_By_Engine()
    {
        // The engine maps the definition default (tier 6) without mutating it.
        var input = Input("X", RecommendationLevel.UsuallyKeep);
        var result = Eval(input);

        Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, result.Level);
        Assert.False(result.WasProfileDriven);
        Assert.Equal(RecommendationLevel.UsuallyKeep, input.DefaultRecommendation);
    }

    [Fact]
    public void Effective_Recommendation_Computed_Separately()
    {
        // With a profile the effective level differs from the definition default
        // AND the definition's own recommendation is not written back.
        var input = Input("XboxApp", RecommendationLevel.OptionalRemove, RiskLevel.Low);
        var gaming = P("Gaming", ovs: new[] { ("XboxApp", ProfileIntent.Keep, "Profile.Reason.Gaming.Xbox") });

        var result = Eval(input, profiles: gaming);

        Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, result.Level);
        Assert.Equal(RecommendationLevel.OptionalRemove, input.DefaultRecommendation);
    }

    // =====================================================================
    // PRECEDENCE (R5-R9)
    // =====================================================================

    [Fact]
    public void Dependency_Keep_Overrides_Profile_Trim()
    {
        // Item Y Requires X; profile Dev REQUIRES X (present) → Y kept via the
        // dependency rule even though Lightweight trims Y. Visible conflict.
        var dev = P("Developer", required: new[] { "X" });
        var lightweight = P("Lightweight", avoided: new[] { "Y" });
        var y = Input("Y", RecommendationLevel.OptionalRemove, RiskLevel.Low,
            deps: new[] { new ComponentDependency { ToId = "X", Relation = DependencyRelation.Requires } });

        var result = Eval(y, presentIds: new[] { "Y", "X" }, profiles: new[] { dev, lightweight });

        Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, result.Level);
        Assert.True(result.HasConflict);
        Assert.Contains(result.ReasonKeys, k => k == "Profile.Reason.Dependency");
        Assert.Contains(result.ReasonKeys, k => k == "Profile.Reason.Conflict.KeepWins");
    }

    [Fact]
    public void User_Override_Survives_Recalculation()
    {
        var input = Input("XboxApp", RecommendationLevel.OptionalRemove);
        var gaming = P("Gaming", ovs: new[] { ("XboxApp", ProfileIntent.Keep, "Profile.Reason.Gaming.Xbox") });

        var result = Eval(input, overrides: new[] { "XboxApp" }, profiles: gaming);

        Assert.True(result.WasOverridden);
        Assert.Contains(result.ReasonKeys, k => k == "Profile.Reason.UserOverride");
    }

    [Fact]
    public void Safety_Block_Wins_Over_Profile_Rules()
    {
        // Critical risk: blocked even when a profile asks to trim it.
        var input = Input("CoreX", RecommendationLevel.OptionalRemove, RiskLevel.Critical);
        var lightweight = P("Lightweight", avoided: new[] { "CoreX" });

        var result = Eval(input, profiles: lightweight);

        Assert.Equal(EffectiveRecommendationLevel.Blocked, result.Level);
    }

    [Fact]
    public void NeverRemove_Stays_Blocked_Under_Profile()
    {
        var input = Input("SvcCore", RecommendationLevel.NeverRemove, RiskLevel.Medium);
        var lightweight = P("Lightweight", avoided: new[] { "SvcCore" });

        var result = Eval(input, profiles: lightweight);

        Assert.Equal(EffectiveRecommendationLevel.Blocked, result.Level);
    }

    [Fact]
    public void Unsupported_Apply_Flags_But_Does_Not_AutoSelect()
    {
        // A capability row (apply unsupported) keeps its badge but must never be
        // auto-selected — Part J eligibility excludes it (App-level assertion).
        var input = Input("OpenSshClient", RecommendationLevel.OptionalRemove, RiskLevel.Low,
            present: true, applySupported: false);
        var lightweight = P("Lightweight", avoided: new[] { "OpenSshClient" });

        var result = Eval(input, profiles: lightweight);

        Assert.False(result.IsApplySupported);
        Assert.True(result.IsPresent);
    }

    // =====================================================================
    // GAMING (R10-R12)
    // =====================================================================

    [Fact]
    public void Gaming_Changes_Xbox_Recommendation()
    {
        var input = Input("XboxApp", RecommendationLevel.OptionalRemove, RiskLevel.Low);
        var gaming = CatalogProfiles()["Gaming"];

        var result = Eval(input, profiles: gaming);

        Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, result.Level);
        Assert.True(result.WasProfileDriven);
        Assert.Contains(result.ReasonKeys, k => k.StartsWith("Profile.Reason.Gaming.", StringComparison.Ordinal));
    }

    [Fact]
    public void Gaming_Does_Not_Disable_Security_Foundations()
    {
        // No Gaming rule touches security/update foundations; and even if an item
        // were Critical, the engine blocks it (safety tier wins).
        var gaming = CatalogProfiles()["Gaming"];
        foreach (var rule in gaming.RecommendationOverrides)
        {
            Assert.False(
                rule.TargetId.Contains("Defender", StringComparison.OrdinalIgnoreCase) ||
                rule.TargetId.Contains("WindowsUpdate", StringComparison.OrdinalIgnoreCase) ||
                rule.TargetId.Contains("Security", StringComparison.OrdinalIgnoreCase),
                "Gaming must not target security/update foundations: " + rule.TargetId);
        }

        var critical = Eval(Input("SecX", RecommendationLevel.OptionalRemove, RiskLevel.Critical), profiles: gaming);
        Assert.Equal(EffectiveRecommendationLevel.Blocked, critical.Level);
    }

    [Fact]
    public void Gaming_Keeps_Media_Codecs()
    {
        foreach (var id in new[] { "AV1VideoExtension", "AVCEncoderVideoExtension" })
        {
            var result = Eval(Input(id, RecommendationLevel.OptionalRemove, RiskLevel.Low),
                profiles: CatalogProfiles()["Gaming"]);
            Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, result.Level);
        }
    }

    // =====================================================================
    // DEVELOPER (R13-R17)
    // =====================================================================

    [Theory]
    [InlineData("Wsl")]
    [InlineData("VirtualMachinePlatform")]
    [InlineData("Terminal")]
    [InlineData("DesktopAppInstaller")]
    [InlineData("OpenSshClient")]
    [InlineData("HyperV")]
    [InlineData("HypervisorPlatform")]
    public void Developer_Keeps_Required_Tooling(string logicalId)
    {
        var result = Eval(Input(logicalId, RecommendationLevel.OptionalRemove, RiskLevel.Low),
            presentIds: new[] { logicalId }, profiles: CatalogProfiles()["Developer"]);

        Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, result.Level);
        Assert.True(result.WasProfileDriven);
    }

    [Fact]
    public void Developer_Keeps_Only_When_Present()
    {
        // Part O: an absent required capability is not force-kept nor counted.
        var result = Eval(Input("Wsl", RecommendationLevel.OptionalRemove, RiskLevel.Low, present: false),
            presentIds: Array.Empty<string>(), profiles: CatalogProfiles()["Developer"]);

        Assert.False(result.IsPresent);
        // Absent items keep their neutral default mapping but are never offered.
        Assert.Equal(EffectiveRecommendationLevel.ManualReview, result.Level);
    }

    // =====================================================================
    // OFFICE (R18-R19)
    // =====================================================================

    [Theory]
    [InlineData("OneDrive")]
    [InlineData("OneDriveSync")]
    [InlineData("InternetPrinting")]
    [InlineData("ScanManagement")]
    [InlineData("Teams")]
    [InlineData("RemoteAssistance")]
    public void Office_Keeps_Productivity_And_Printing(string logicalId)
    {
        var result = Eval(Input(logicalId, RecommendationLevel.OptionalRemove, RiskLevel.Low),
            profiles: CatalogProfiles()["Office"]);

        Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, result.Level);
        Assert.True(result.WasProfileDriven);
    }

    // =====================================================================
    // LIGHTWEIGHT (R20-R21)
    // =====================================================================

    [Fact]
    public void Lightweight_Recommends_Consumer_Trims()
    {
        var result = Eval(Input("Weather", RecommendationLevel.OptionalRemove, RiskLevel.Low),
            profiles: CatalogProfiles()["Lightweight"]);

        Assert.Equal(EffectiveRecommendationLevel.RecommendRemove, result.Level);
        Assert.True(result.WasProfileDriven);
    }

    [Fact]
    public void Lightweight_Never_Selects_Critical_Items()
    {
        var result = Eval(Input("CriticalX", RecommendationLevel.OptionalRemove, RiskLevel.Critical),
            profiles: CatalogProfiles()["Lightweight"]);
        Assert.Equal(EffectiveRecommendationLevel.Blocked, result.Level);
    }

    // =====================================================================
    // CONFLICT (R22-R24)
    // =====================================================================

    [Fact]
    public void Gaming_Plus_Lightweight_Conflict_Is_Resolved_Visibly()
    {
        var input = Input("XboxGipSvc", RecommendationLevel.OptionalRemove, RiskLevel.Low);
        var result = Eval(input,
            profiles: new[] { CatalogProfiles()["Gaming"], CatalogProfiles()["Lightweight"] });

        Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, result.Level);
        Assert.True(result.HasConflict);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("Gaming", conflict.KeepProfileId);
        Assert.Equal("Lightweight", conflict.TrimProfileId);
        Assert.Contains(result.ReasonKeys, k => k == "Profile.Reason.Conflict.KeepWins");
    }

    [Fact]
    public void Developer_Plus_Lightweight_Virtualization_Keep_Wins()
    {
        var input = Input("VirtualMachinePlatform", RecommendationLevel.OptionalRemove, RiskLevel.High);
        var result = Eval(input, presentIds: new[] { "VirtualMachinePlatform", "Wsl" },
            profiles: new[] { CatalogProfiles()["Developer"], CatalogProfiles()["Lightweight"] });

        Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, result.Level);
        Assert.True(result.HasConflict);
        Assert.Equal("Developer", result.Conflicts[0].KeepProfileId);
        Assert.Equal("Lightweight", result.Conflicts[0].TrimProfileId);
    }

    [Fact]
    public void Conflict_Is_Shown_With_Reason()
    {
        var input = Input("XboxGipSvc", RecommendationLevel.OptionalRemove, RiskLevel.Low);
        var result = Eval(input,
            profiles: new[] { CatalogProfiles()["Gaming"], CatalogProfiles()["Lightweight"] });

        Assert.Contains(result.ReasonKeys, k => k == "Profile.Reason.Conflict.KeepWins");
        Assert.Equal("Profile.Reason.Conflict.KeepWins", result.Conflicts[0].ReasonKey);
    }

    // =====================================================================
    // UI — profile selector / preview / adopt (R25-R32)
    // =====================================================================

    [Fact]
    public void Profile_Selector_Localizes_En_And_Zh()
    {
        var profile = new ProfileCatalog().GetProfiles().Single(p => p.Id == "Gaming");

        var en = new ProfileItemViewModel(profile, new SuffixLoc("_EN"), ParentVm(new SuffixLoc("_EN")));
        var zh = new ProfileItemViewModel(profile, new SuffixLoc("_ZH"), ParentVm(new SuffixLoc("_ZH")));

        Assert.Equal("Profile.Gaming.DisplayName_EN", en.DisplayName);
        Assert.Equal("Profile.Gaming.DisplayName_ZH", zh.DisplayName);
        Assert.NotEqual(en.DisplayName, zh.DisplayName);
        Assert.Equal("Profile.Gaming.Description_ZH", zh.Description);
    }

    [Fact]
    public void Profile_Change_Updates_Badges_Not_Checkboxes()
    {
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp"));
        var profileVm = customize.Profiles!;
        var apps = (ComponentKnowledgeViewModel)customize.Tabs[0].Content;
        var xbox = apps.Items.Single(i => i.LogicalId == "XboxApp");

        Assert.Equal(EffectiveRecommendationLevel.ManualReview, xbox.Effective.Level);
        Assert.Equal("Recommendation.OptionalRemove", xbox.RecommendationCaption);

        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;

        Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, xbox.Effective.Level);
        Assert.Equal("Recommendation.UsuallyKeep", xbox.RecommendationCaption);
        // Part H: NO checkbox changes automatically — plan untouched.
        Assert.False(xbox.IsSelected);
        Assert.Empty(GetPlanOps(state));
    }

    [Fact]
    public void Preview_Shows_Candidate_Selections()
    {
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp", "Microsoft.BingWeather"));
        var profileVm = customize.Profiles!;
        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;

        profileVm.ShowPreview();

        var adopt = profileVm.PreviewGroups.Single(g => g.Kind == RecommendationPreviewGroupKind.Adopt);
        // Gaming trims unrelated consumer apps (Weather is present here).
        Assert.Contains(adopt.Items, i => i.DisplayName.Contains("Weather", StringComparison.OrdinalIgnoreCase));
        // A keep (Xbox) must NOT appear in the adopt group.
        Assert.DoesNotContain(adopt.Items, i => i.DisplayName.Contains("Xbox", StringComparison.OrdinalIgnoreCase));
        // Still nothing selected.
        Assert.Empty(GetPlanOps(state));
    }

    [Fact]
    public void Adopt_Updates_Only_Eligible_Items()
    {
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp", "Microsoft.BingWeather"));
        var profileVm = customize.Profiles!;
        var apps = (ComponentKnowledgeViewModel)customize.Tabs[0].Content;
        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;

        profileVm.Adopt();

        // Only low-risk, apply-supported, present, conflict-free TRIMS are selected
        // (Gaming trims Weather; the Privacy-tab trims add registry ops too).
        var ops = GetPlanOps(state);
        Assert.NotEmpty(ops);
        Assert.Contains(ops, o => o.TargetIdentifier == "Microsoft.BingWeather");
        Assert.Contains(ops, o => o.OperationType == CustomizationOperationType.SetOfflineRegistryValue);
        // XboxApp is a keep → never adopted.
        Assert.DoesNotContain(ops, o => o.TargetIdentifier == "Microsoft.XboxApp");
        Assert.True(apps.Items.Single(i => i.LogicalId == "Weather").IsSelected);
        Assert.False(apps.Items.Single(i => i.LogicalId == "XboxApp").IsSelected);
        _ = state;
    }

    [Fact]
    public void High_Risk_Trim_Is_Not_AutoSelected()
    {
        // VirtualMachinePlatform is trimmed by Lightweight but its risk is High —
        // Part J excludes it from auto-selection.
        var (state, customize) = BuildCustomize(FeatureInventory(("VirtualMachinePlatform", FeatureState.Enabled), ("Microsoft-Windows-Subsystem-Linux", FeatureState.Enabled)));
        var profileVm = customize.Profiles!;
        var components = (ComponentKnowledgeViewModel)customize.Tabs[1].Content;
        profileVm.Profiles.Single(p => p.Definition.Id == "Lightweight").IsSelected = true;

        var vmp = components.Items.Single(i => i.LogicalId == "VirtualMachinePlatform");
        Assert.Equal(EffectiveRecommendationLevel.RecommendDisable, vmp.Effective.Level);
        Assert.Equal(RiskLevel.High, vmp.Effective.Risk);

        profileVm.Adopt();

        // High-risk trim stays unselected AND never reaches the plan.
        Assert.False(vmp.IsSelected);
        Assert.DoesNotContain(GetPlanOps(state), o => o.TargetIdentifier == "VirtualMachinePlatform");
    }

    [Fact]
    public void Manual_Override_Survives_Reapply()
    {
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp", "Microsoft.BingWeather"));
        var profileVm = customize.Profiles!;
        var apps = (ComponentKnowledgeViewModel)customize.Tabs[0].Content;
        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;

        // Adopt → Weather selected (Gaming trims it).
        profileVm.Adopt();
        var weather = apps.Items.Single(i => i.LogicalId == "Weather");
        Assert.True(weather.IsSelected);

        // Manual deselect → user override (Part K).
        weather.IsSelected = false;
        Assert.False(weather.IsSelected);

        // Reapply must NOT resurrect the user's explicit choice.
        profileVm.Reapply();
        Assert.False(weather.IsSelected);
        Assert.True(weather.WasOverridden);
        _ = state;
    }

    [Fact]
    public void Recommendation_Reason_Changes_Visibly()
    {
        var (_, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp"));
        var profileVm = customize.Profiles!;
        var apps = (ComponentKnowledgeViewModel)customize.Tabs[0].Content;
        var xbox = apps.Items.Single(i => i.LogicalId == "XboxApp");

        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;

        // Deterministic reason keys (Part F) — engine-level, localization-agnostic.
        Assert.Contains("Profile.Reason.Gaming.Xbox", xbox.Effective.ReasonKeys);
        Assert.Contains(xbox.WhyPoints, w => w.Contains("Profile.Why", StringComparison.Ordinal));
    }

    [Fact]
    public void Profile_Change_Does_Not_Mutate_Plan()
    {
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp", "Microsoft.BingNews"));
        var profileVm = customize.Profiles!;
        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;
        Assert.Empty(GetPlanOps(state));

        profileVm.Profiles.Single(p => p.Definition.Id == "Lightweight").IsSelected = true;
        Assert.Empty(GetPlanOps(state));
        _ = state;
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static readonly Lazy<IReadOnlyDictionary<string, ProfileDefinition>> _catalog =
        new(() => new ProfileCatalog().GetProfiles().ToDictionary(p => p.Id, StringComparer.Ordinal));

    private static IReadOnlyDictionary<string, ProfileDefinition> CatalogProfiles() => _catalog.Value;

    private static RecommendationInput Input(
        string id,
        RecommendationLevel rec = RecommendationLevel.OptionalRemove,
        RiskLevel risk = RiskLevel.Low,
        OptimizationAction action = OptimizationAction.Remove,
        bool present = true,
        bool applySupported = true,
        params ComponentDependency[] deps)
        => new()
        {
            LogicalId = id,
            Action = action,
            DefaultRecommendation = rec,
            Risk = risk,
            Removal = RemovalSupport.Supported,
            IsPresent = present,
            IsApplySupported = applySupported,
            Dependencies = deps,
        };

    private static EffectiveRecommendation Eval(
        RecommendationInput input,
        IReadOnlyCollection<string>? presentIds = null,
        IReadOnlyCollection<string>? overrides = null,
        params ProfileDefinition[] profiles)
        => new RecommendationEngine().Evaluate(input, new RecommendationContext
        {
            SelectedProfiles = profiles,
            PresentIds = presentIds ?? new[] { input.LogicalId },
            UserOverrides = overrides ?? new HashSet<string>(),
        });

    private static ProfileDefinition P(
        string id,
        IEnumerable<string>? required = null,
        IEnumerable<(string Target, ProfileIntent Intent, string Reason)>? ovs = null,
        IEnumerable<string>? avoided = null)
        => new()
        {
            Id = id,
            DisplayNameKey = "Profile." + id + ".DisplayName",
            DescriptionKey = "Profile." + id + ".Description",
            IconKey = string.Empty,
            RequiredCapabilities = (required ?? Array.Empty<string>()).ToList(),
            RecommendationOverrides = (ovs ?? Array.Empty<(string, ProfileIntent, string)>())
                .Select(o => new ProfileRecommendationOverride { TargetId = o.Target, Intent = o.Intent, ReasonKey = o.Reason, Tier = 5 })
                .ToList(),
            AvoidedComponents = (avoided ?? Array.Empty<string>()).ToList(),
        };

    private static ProfileViewModel ParentVm(SuffixLoc loc)
        => new(new RecommendationContextService(new RecommendationEngine(), new ProfileCatalog(), new AppState()),
            loc, () => Array.Empty<IRecommendationSubject>(), () => { });

    private static List<CustomizationOperation> GetPlanOps(AppState state)
        => state.CurrentCustomizationPlan?.Operations.ToList() ?? new List<CustomizationOperation>();

    private static (AppState State, CustomizeStepViewModel Customize) BuildCustomize(ComponentInventory inventory)
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
        var loc = new FakeLocalizationService();
        var ctx = new RecommendationContextService(new RecommendationEngine(), new ProfileCatalog(), state);
        var ciVm = new ComponentIntelligenceViewModel(state, logger,
            new StaticInventoryCiService { Inventory = inventory },
            new CompositeComponentCatalog(new CuratedComponentCatalog(), new WindowsFeaturesCatalog()), loc);

        var knowledge = new ComponentKnowledgeViewModel(ciVm, state, logger, loc, null, ctx);
        var componentsKnowledge = new ComponentKnowledgeViewModel(ciVm, state, logger, loc,
            new[] { ComponentCategory.OptionalFeature, ComponentCategory.Capability }, ctx);

        var catalog = new OptimizationCatalog();
        var customize = new CustomizeStepViewModel(
            new ComponentsViewModel(state, logger, new FakeCustomizationDiscoveryService(), new FakeCustomizationDefinitionProvider()),
            knowledge,
            componentsKnowledge,
            new OptimizationKnowledgeViewModel(state, logger, loc, catalog, OptimizationTab.Services, ctx),
            new OptimizationKnowledgeViewModel(state, logger, loc, catalog, OptimizationTab.Privacy, ctx),
            new OptimizationKnowledgeViewModel(state, logger, loc, catalog, OptimizationTab.System, ctx),
            new OptimizationKnowledgeViewModel(state, logger, loc, catalog, OptimizationTab.Personalization, ctx),
            ctx,
            loc);

        knowledge.DiscoverAsync().GetAwaiter().GetResult();
        componentsKnowledge.RefreshFromInventory();
        return (state, customize);
    }

    private static ComponentInventory AppxInventory(params string[] identities)
        => new()
        {
            Discovered = true,
            Categories = new[]
            {
                new CategoryDiscoveryResult
                {
                    Category = ComponentCategory.AppX,
                    Status = InventoryStatus.Success,
                    Items = identities
                        .Select(id => new RawAppxPackage
                        {
                            Category = ComponentCategory.AppX,
                            RawIdentity = id,
                            DisplayName = id,
                        } as IRawInventoryItem)
                        .ToList(),
                },
            },
        };

    private static ComponentInventory FeatureInventory(params (string Name, FeatureState State)[] features)
        => new()
        {
            Discovered = true,
            Categories = new[]
            {
                new CategoryDiscoveryResult
                {
                    Category = ComponentCategory.OptionalFeature,
                    Status = InventoryStatus.Success,
                    Items = features
                        .Select(f => new RawOptionalFeature
                        {
                            Category = ComponentCategory.OptionalFeature,
                            RawIdentity = f.Name,
                            DisplayName = f.Name,
                            FeatureStateValue = f.State,
                        } as IRawInventoryItem)
                        .ToList(),
                },
            },
        };

    private sealed class StaticInventoryCiService : IComponentIntelligenceService
    {
        public ComponentInventory Inventory { get; set; } = new();

        public Task<ComponentInventory> DiscoverAsync(
            ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
            => Task.FromResult(Inventory);
    }

    /// <summary>Localization fake that suffixes every key (en/zh render tests).</summary>
    private sealed class SuffixLoc : ILocalizationService
    {
        private readonly string _suffix;
        public SuffixLoc(string suffix) => _suffix = suffix;
        public System.Globalization.CultureInfo CurrentCulture => System.Globalization.CultureInfo.GetCultureInfo("en");
        public event EventHandler? CultureChanged { add { } remove { } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public string this[string key] => key + _suffix;
        public void SetCulture(System.Globalization.CultureInfo culture) { }
        public bool Contains(string key) => false;
    }
}
