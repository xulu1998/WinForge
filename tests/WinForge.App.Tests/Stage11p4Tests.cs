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
    public void Profile_Selection_Updates_Badges_And_Keeps_Are_Never_AutoSelected()
    {
        // Final flow: badges update immediately AND safe trims auto-apply, but a
        // KEEP recommendation (Xbox) is never auto-selected.
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp"));
        var profileVm = customize.Profiles!;
        var apps = (ComponentKnowledgeViewModel)customize.Tabs[0].Content;
        var xbox = apps.Items.Single(i => i.LogicalId == "XboxApp");

        Assert.Equal(EffectiveRecommendationLevel.ManualReview, xbox.Effective.Level);
        Assert.Equal("Recommendation.OptionalRemove", xbox.RecommendationCaption);

        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;

        Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, xbox.Effective.Level);
        Assert.Equal("Recommendation.UsuallyKeep", xbox.RecommendationCaption);
        Assert.False(xbox.IsSelected);
        Assert.DoesNotContain(GetPlanOps(state), o => o.TargetIdentifier == "Microsoft.XboxApp");
        _ = state;
    }

    [Fact]
    public void Preview_Shows_Candidate_Selections()
    {
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp", "Microsoft.BingWeather"));
        var profileVm = customize.Profiles!;
        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;

        var before = GetPlanOps(state); // Gaming selection already auto-applied
        profileVm.ShowPreview();

        var adopt = profileVm.PreviewGroups.Single(g => g.Kind == RecommendationPreviewGroupKind.Adopt);
        // Gaming trims unrelated consumer apps (Weather is present here).
        Assert.Contains(adopt.Items, i => i.DisplayName.Contains("Weather", StringComparison.OrdinalIgnoreCase));
        // A keep (Xbox) must NOT appear in the adopt group.
        Assert.DoesNotContain(adopt.Items, i => i.DisplayName.Contains("Xbox", StringComparison.OrdinalIgnoreCase));
        // Opening the preview itself never mutates the plan.
        Assert.Equal(before.Select(o => o.TargetIdentifier).OrderBy(t => t),
            GetPlanOps(state).Select(o => o.TargetIdentifier).OrderBy(t => t));
    }

    [Fact]
    public void Profile_Selection_Immediately_Applies_Eligible_Selections()
    {
        // Final flow (T1): selecting a profile IS the adoption — no second button.
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp", "Microsoft.BingWeather"));
        var profileVm = customize.Profiles!;
        var apps = (ComponentKnowledgeViewModel)customize.Tabs[0].Content;

        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;

        // Only low-risk, apply-supported, present, conflict-free TRIMS are selected
        // (Gaming trims Weather; the Privacy-tab trims add registry ops too).
        var ops = GetPlanOps(state);
        Assert.NotEmpty(ops);
        Assert.Contains(ops, o => o.TargetIdentifier == "Microsoft.BingWeather");
        Assert.Contains(ops, o => o.OperationType == CustomizationOperationType.SetOfflineRegistryValue);
        // XboxApp is a keep → never auto-selected.
        Assert.DoesNotContain(ops, o => o.TargetIdentifier == "Microsoft.XboxApp");
        Assert.True(apps.Items.Single(i => i.LogicalId == "Weather").IsSelected);
        Assert.False(apps.Items.Single(i => i.LogicalId == "XboxApp").IsSelected);
        // The selection is Profile-managed and attributed.
        Assert.True(profileVm.IsProfileManaged("Weather"));
        Assert.Contains("Profile.Origin.Auto", apps.Items.Single(i => i.LogicalId == "Weather").SelectionOriginText);
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

        // Selecting the profile already ran the safe-apply pass (T1): the
        // high-risk trim stays unselected AND never reaches the plan.
        Assert.False(vmp.IsSelected);
        Assert.DoesNotContain(GetPlanOps(state), o => o.TargetIdentifier == "VirtualMachinePlatform");
    }

    [Fact]
    public void User_Override_Survives_Profile_Switch()
    {
        // Final flow (T6/T9): a manual choice survives profile switching; only an
        // explicit 恢复此配置推荐 may recalculate it.
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp", "Microsoft.BingWeather"));
        var profileVm = customize.Profiles!;
        var apps = (ComponentKnowledgeViewModel)customize.Tabs[0].Content;
        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;

        // Gaming auto-applies Weather (trim).
        var weather = apps.Items.Single(i => i.LogicalId == "Weather");
        Assert.True(weather.IsSelected);

        // Manual deselect -> user override (Part K).
        weather.IsSelected = false;
        Assert.False(weather.IsSelected);
        Assert.True(weather.WasOverridden);
        Assert.Contains("Profile.Origin.Manual", weather.SelectionOriginText);

        // Switch to Lightweight — Weather is ALSO trimmed there, but the user
        // override must win: it stays deselected.
        profileVm.Profiles.Single(p => p.Definition.Id == "Lightweight").IsSelected = true;
        Assert.False(weather.IsSelected);
        Assert.True(weather.WasOverridden);

        // 恢复此配置推荐 (explicit) clears the override and re-applies.
        profileVm.Restore();
        Assert.True(weather.IsSelected);
        Assert.False(weather.WasOverridden);
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
    public void Preview_Is_Not_Required_For_Recommendation_Update()
    {
        // UX fix: badges update IMMEDIATELY on profile selection — no preview step.
        var (_, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp"));
        var profileVm = customize.Profiles!;
        var apps = (ComponentKnowledgeViewModel)customize.Tabs[0].Content;
        var xbox = apps.Items.Single(i => i.LogicalId == "XboxApp");

        Assert.Equal(EffectiveRecommendationLevel.ManualReview, xbox.Effective.Level);
        Assert.False(profileVm.IsPreviewOpen);

        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;

        Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, xbox.Effective.Level);
        Assert.Equal("Recommendation.UsuallyKeep", xbox.RecommendationCaption);
        Assert.False(profileVm.IsPreviewOpen); // preview was never opened
    }

    [Fact]
    public void Restore_Is_Hidden_In_Initial_State()
    {
        var (_, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp"));
        Assert.False(customize.Profiles!.RestoreVisible);
    }

    [Fact]
    public void Restore_Appears_Only_After_Manual_Override()
    {
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp", "Microsoft.BingWeather"));
        var profileVm = customize.Profiles!;
        var apps = (ComponentKnowledgeViewModel)customize.Tabs[0].Content;

        // Selecting a profile alone never reveals restore.
        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;
        Assert.False(profileVm.RestoreVisible);

        // Manual override → visible.
        var weather = apps.Items.Single(i => i.LogicalId == "Weather");
        weather.IsSelected = false;
        Assert.True(profileVm.RestoreVisible);

        // Profile switch alone (no new override) keeps it visible (override persists).
        profileVm.Profiles.Single(p => p.Definition.Id == "Lightweight").IsSelected = true;
        Assert.True(profileVm.RestoreVisible);

        // Restore clears the override → hidden again (nothing to restore).
        profileVm.Restore();
        Assert.False(profileVm.RestoreVisible);
        _ = state;
    }

    [Fact]
    public void Custom_Preserves_Current_Selections()
    {
        // Final flow (T7): Custom stops profile-driven overrides and keeps the
        // user's existing plan — it never clears selections merely because it is
        // chosen. All subsequent changes are manual.
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp", "Microsoft.BingWeather"));
        var profileVm = customize.Profiles!;
        var apps = (ComponentKnowledgeViewModel)customize.Tabs[0].Content;

        // Gaming auto-applies Weather (Profile-managed).
        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;
        var weather = apps.Items.Single(i => i.LogicalId == "Weather");
        Assert.True(weather.IsSelected);

        // Switch to Custom → selections are PRESERVED (plan untouched).
        profileVm.Profiles.Single(p => p.Definition.Id == "Custom").IsSelected = true;
        Assert.False(profileVm.HasActiveProfiles);
        Assert.True(weather.IsSelected);
        Assert.Contains(GetPlanOps(state), o => o.TargetIdentifier == "Microsoft.BingWeather");
        Assert.False(weather.Effective.WasProfileDriven);
        // Profile-managed bookkeeping cleared; the row is now just "selected".
        Assert.False(profileVm.IsProfileManaged("Weather"));

        // Subsequent changes are manual.
        weather.IsSelected = false;
        Assert.False(weather.IsSelected);
        _ = state;
    }

    [Fact]
    public void Summary_Counts_Update_Immediately_On_Profile_Selection()
    {
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp", "Microsoft.BingWeather"));
        var profileVm = customize.Profiles!;
        Assert.Equal(0, profileVm.TrimCount);

        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;

        // Updated immediately — no adopt, no preview required.
        Assert.True(profileVm.TrimCount > 0);
        Assert.True(profileVm.KeepCount > 0);
        _ = state;
    }

    [Fact]
    public void Summary_Counts_Use_Present_Items_Only()
    {
        // Part O: an absent trim candidate (Weather) must NOT be counted.
        var (_, cWithWeather) = BuildCustomize(AppxInventory("Microsoft.XboxApp", "Microsoft.BingWeather"));
        var (_, cWithoutWeather) = BuildCustomize(AppxInventory("Microsoft.XboxApp"));
        var vWith = cWithWeather.Profiles!;
        var vWithout = cWithoutWeather.Profiles!;
        vWith.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;
        vWithout.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;

        Assert.True(vWith.TrimCount > vWithout.TrimCount,
            $"Weather-present ({vWith.TrimCount}) must exceed absent ({vWithout.TrimCount}).");
    }

    // =====================================================================
    // Final flow (2026-08-12): selecting a profile IS the adoption. Tests T1-T17.
    // =====================================================================

    [Fact]
    public void No_Adopt_Button_Exists()
    {
        // T2: the mandatory "choose profile then adopt" step is removed — the
        // ViewModel no longer exposes AdoptCommand / Adopt, and the ProfileView
        // no longer renders a 采用推荐选择 button.
        Assert.Null(typeof(ProfileViewModel).GetProperty("AdoptCommand"));
        Assert.Null(typeof(ProfileViewModel).GetMethod("Adopt"));
        Assert.Null(typeof(ProfileViewModel).GetMethod("Reapply"));
    }

    [Fact]
    public void Selected_Count_Updates_Immediately()
    {
        // T3: clicking a profile updates the plan (and thus 已选 N 项) at once.
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp", "Microsoft.BingWeather"));
        var profileVm = customize.Profiles!;
        Assert.Equal(0, customize.SelectedTotal);

        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;

        Assert.True(customize.SelectedTotal > 0,
            $"SelectedTotal must jump immediately (was {customize.SelectedTotal}).");
        Assert.Equal(customize.SelectedTotal, GetPlanOps(state).Count);
        _ = state;
    }

    [Fact]
    public void Medium_Risk_Stays_Manual_Review()
    {
        // T4: HyperV is a Medium-risk, AdvancedOnly trim under Lightweight — it
        // must stay unselected and appear in the 需要确认 bucket.
        var (state, customize) = BuildCustomize(FeatureInventory(("Microsoft-Hyper-V", FeatureState.Enabled)));
        var profileVm = customize.Profiles!;
        var components = (ComponentKnowledgeViewModel)customize.Tabs[1].Content;
        profileVm.Profiles.Single(p => p.Definition.Id == "Lightweight").IsSelected = true;

        var hyperv = components.Items.Single(i => i.LogicalId == "HyperV");
        Assert.Equal(EffectiveRecommendationLevel.RecommendDisable, hyperv.Effective.Level);
        Assert.Equal(RiskLevel.Medium, hyperv.Effective.Risk);
        Assert.False(hyperv.IsSelected);
        Assert.DoesNotContain(GetPlanOps(state), o => o.TargetIdentifier == "HyperV");

        profileVm.ShowPreview();
        var manual = profileVm.PreviewGroups.Single(g => g.Kind == RecommendationPreviewGroupKind.Manual);
        Assert.Contains(manual.Items, i => i.DisplayName.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase));
        _ = state;
    }

    [Fact]
    public void Unsupported_Capability_Stays_Unselected()
    {
        // T5: an apply-unsupported Capability (OpenSSH Client) is never
        // auto-selected and shows under 冲突/不可执行, not in the plan.
        var (state, customize) = BuildCustomize(MixedInventory());
        var profileVm = customize.Profiles!;
        profileVm.Profiles.Single(p => p.Definition.Id == "Developer").IsSelected = true;

        Assert.DoesNotContain(GetPlanOps(state), o =>
            o.TargetIdentifier is "OpenSSH.Client" or "OpenSSH.Client~~~~0.0.1.0");
        Assert.True(profileVm.UnsupportedCount >= 1);
        profileVm.ShowPreview();
        var conflict = profileVm.PreviewGroups.Single(g => g.Kind == RecommendationPreviewGroupKind.Conflict);
        Assert.Contains(conflict.Items, i => i.DisplayName.Contains("OpenSSH", StringComparison.OrdinalIgnoreCase));
        _ = state;
    }

    [Fact]
    public void Profile_Managed_Selection_Changes_With_Profile()
    {
        // T8: Clipchamp is auto-trimmed by Developer but only OPTIONAL under the
        // knowledge-driven Gaming PC (Phase 14.3, ADR-088) — switching profiles
        // must deselect the now-unrecommended Profile-managed row. (BingNews is
        // no longer the differentiator: the Gaming PC knowledge policy also
        // recommends removing news/search consumer content.)
        var (state, customize) = BuildCustomize(AppxInventory("Clipchamp.Clipchamp"));
        var profileVm = customize.Profiles!;
        var apps = (ComponentKnowledgeViewModel)customize.Tabs[0].Content;

        profileVm.Profiles.Single(p => p.Definition.Id == "Developer").IsSelected = true;
        var clipchamp = apps.Items.Single(i => i.LogicalId == "Clipchamp");
        Assert.True(clipchamp.IsSelected);
        Assert.True(profileVm.IsProfileManaged("Clipchamp"));

        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;
        Assert.False(clipchamp.IsSelected); // optional under Gaming — not auto-applied
        Assert.False(profileVm.IsProfileManaged("Clipchamp"));
        Assert.DoesNotContain(GetPlanOps(state), o => o.TargetIdentifier == "Clipchamp.Clipchamp");
        _ = state;
    }

    [Fact]
    public void User_Managed_Selection_Does_Not_Silently_Change()
    {
        // T9: a row the user selected MANUALLY (never Profile-managed) is not
        // silently toggled by later profile changes.
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp"));
        var profileVm = customize.Profiles!;
        var apps = (ComponentKnowledgeViewModel)customize.Tabs[0].Content;
        var xbox = apps.Items.Single(i => i.LogicalId == "XboxApp");

        // Manual selection while NO profile drives anything.
        xbox.IsSelected = true;
        Assert.True(xbox.IsSelected);
        Assert.True(xbox.WasOverridden);

        profileVm.Profiles.Single(p => p.Definition.Id == "Developer").IsSelected = true;
        // Developer keeps Xbox too — but more importantly the manual choice stays.
        Assert.True(xbox.IsSelected);
        Assert.True(xbox.WasOverridden);
        _ = state;
    }

    [Fact]
    public void Recommendation_Detail_Opens()
    {
        // T10: the detail overlay opens with grouped buckets.
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp", "Microsoft.BingWeather"));
        var profileVm = customize.Profiles!;
        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;

        Assert.False(profileVm.IsPreviewOpen);
        profileVm.ShowPreview();
        Assert.True(profileVm.IsPreviewOpen);
        Assert.True(profileVm.HasPreviewItems);
        Assert.True(profileVm.PreviewGroups.Count >= 2); // adopt + keep (+ manual/conflict)
        _ = state;
    }

    [Fact]
    public void Recommendation_Detail_Closes_And_Restores_State()
    {
        // T11/T12/T13: the overlay has an explicit close; closing restores the
        // EXACT same Customize state — selections, profile and tab preserved.
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp", "Microsoft.BingWeather"));
        var profileVm = customize.Profiles!;
        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;
        customize.SelectedTab = customize.Tabs[3]; // e.g. Privacy

        profileVm.ShowPreview();
        var opsBefore = GetPlanOps(state).Select(o => o.TargetIdentifier).OrderBy(t => t).ToList();
        var selectedTabBefore = customize.SelectedTab;
        var profileBefore = profileVm.ActiveProfileCaption;

        Assert.NotNull(profileVm.ClosePreviewCommand);
        profileVm.ClosePreview();

        Assert.False(profileVm.IsPreviewOpen);
        Assert.Equal(selectedTabBefore, customize.SelectedTab);
        Assert.Equal(profileBefore, profileVm.ActiveProfileCaption);
        Assert.Equal(opsBefore, GetPlanOps(state).Select(o => o.TargetIdentifier).OrderBy(t => t).ToList());
        Assert.True(profileVm.IsProfileManaged("Weather"));
        _ = state;
    }

    [Fact]
    public void Plan_Reflects_Actual_Selections_For_Next()
    {
        // T14: Next gating is driven by the actual resulting plan — after profile
        // selection the plan contains the auto-applied operations and the
        // selected count matches, with no navigation triggered (T15).
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp", "Microsoft.BingWeather"));
        var profileVm = customize.Profiles!;
        var selectedBefore = customize.SelectedTab;

        profileVm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;

        Assert.NotEmpty(GetPlanOps(state));
        Assert.Equal(customize.SelectedTotal, GetPlanOps(state).Count);
        Assert.Equal(selectedBefore, customize.SelectedTab); // no auto-navigation
        _ = state;
    }

    [Fact]
    public void Profile_Switch_Does_Not_Navigate()
    {
        // T15: selecting/switching profiles never navigates away from Customize
        // (SelectedTab and the wizard position are untouched).
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp", "Microsoft.BingWeather"));
        var profileVm = customize.Profiles!;
        customize.SelectedTab = customize.Tabs[0];

        profileVm.Profiles.Single(p => p.Definition.Id == "Lightweight").IsSelected = true;
        profileVm.Profiles.Single(p => p.Definition.Id == "Developer").IsSelected = true;

        Assert.Equal(customize.Tabs[0], customize.SelectedTab);
        _ = state;
    }

    [Fact]
    public void Selection_Origin_Text_Is_Localized()
    {
        // T16: zh/en origin captions resolve through the REAL resx service.
        var zh = BuildCustomizeLocalized(AppxInventory("Microsoft.BingWeather"), "zh-CN");
        var zhVm = zh.Customize.Profiles!;
        var zhApps = (ComponentKnowledgeViewModel)zh.Customize.Tabs[0].Content;
        zhVm.Profiles.Single(p => p.Definition.Id == "Developer").IsSelected = true;
        var zhOrigin = zhApps.Items.Single(i => i.LogicalId == "Weather").SelectionOriginText;
        Assert.Contains("由「", zhOrigin);
        Assert.Contains("」自动选择", zhOrigin);

        var en = BuildCustomizeLocalized(AppxInventory("Microsoft.BingWeather"), "en");
        var enVm = en.Customize.Profiles!;
        var enApps = (ComponentKnowledgeViewModel)en.Customize.Tabs[0].Content;
        enVm.Profiles.Single(p => p.Definition.Id == "Developer").IsSelected = true;
        var enOrigin = enApps.Items.Single(i => i.LogicalId == "Weather").SelectionOriginText;
        Assert.Contains("Auto-selected by", enOrigin);
    }

    /// <summary>Like <see cref="BuildCustomize"/> but with the real resx localization service.</summary>
    private static (AppState State, CustomizeStepViewModel Customize) BuildCustomizeLocalized(
        ComponentInventory inventory, string cultureName)
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
        loc.SetCulture(System.Globalization.CultureInfo.GetCultureInfo(cultureName));
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

    [Fact]
    public void Selecting_Profile_Keeps_User_Manual_Selection_When_Already_Chosen()
    {
        // T1 extension: if the user already manually selected a row and then picks
        // a profile that recommends it too, the row stays (no flicker), still
        // flagged as a user choice — never downgraded to Profile-managed.
        var (_, customize) = BuildCustomize(AppxInventory("Microsoft.BingWeather"));
        var profileVm = customize.Profiles!;
        var apps = (ComponentKnowledgeViewModel)customize.Tabs[0].Content;
        var weather = apps.Items.Single(i => i.LogicalId == "Weather");
        weather.IsSelected = true; // manual, override

        profileVm.Profiles.Single(p => p.Definition.Id == "Developer").IsSelected = true;

        Assert.True(weather.IsSelected);
        Assert.True(weather.WasOverridden);
        Assert.False(profileVm.IsProfileManaged("Weather")); // override never Profile-managed
    }

    [Fact]
    public void Primary_Profiles_Are_Mutually_Exclusive()
    {
        // Part 1: primary profiles are radio choices — selecting one replaces the other.
        var (_, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp"));
        var vm = customize.Profiles!;

        vm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;
        Assert.True(vm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected);

        vm.Profiles.Single(p => p.Definition.Id == "Developer").IsSelected = true;
        Assert.True(vm.Profiles.Single(p => p.Definition.Id == "Developer").IsSelected);
        Assert.False(vm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected);
        Assert.Equal(1, vm.Profiles.Count(p => p.IsSelected));
    }

    [Fact]
    public void Extra_Scenarios_Are_Independently_Selectable()
    {
        // Part 2: extras are independent secondary checkboxes on top of the primary.
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp"));
        var vm = customize.Profiles!;
        vm.Profiles.Single(p => p.Definition.Id == "Developer").IsSelected = true;

        Assert.False(vm.ExtraScenarios.Single(e => e.Definition.Id == "XboxGamePass").IsSelected);
        vm.ExtraScenarios.Single(e => e.Definition.Id == "XboxGamePass").IsSelected = true;
        vm.ExtraScenarios.Single(e => e.Definition.Id == "PrintingScanning").IsSelected = true;

        Assert.True(vm.ExtraScenarios.Single(e => e.Definition.Id == "XboxGamePass").IsSelected);
        Assert.True(vm.ExtraScenarios.Single(e => e.Definition.Id == "PrintingScanning").IsSelected);
        Assert.True(vm.CanToggleExtras);
        _ = state;
    }

    [Fact]
    public void Engine_Combines_Primary_And_Extra_Scenarios()
    {
        // The engine still combines internally: primary Gaming + extra WslDocker
        // keeps WSL via the extra's requirement (Part 2).
        var catalog = new ProfileCatalog().GetProfiles().ToList();
        var gaming = catalog.Single(p => p.Id == "Gaming");
        var wslDocker = catalog.Single(p => p.Id == "WslDocker");

        var result = Eval(Input("Wsl", RecommendationLevel.OptionalRemove, RiskLevel.Medium),
            presentIds: new[] { "Wsl", "VirtualMachinePlatform", "HypervisorPlatform" },
            profiles: new[] { gaming, wslDocker });

        Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, result.Level);
        Assert.Contains("WslDocker", result.AdvisedByProfileIds);
    }

    [Theory]
    [InlineData("Gaming")]
    [InlineData("Developer")]
    [InlineData("Office")]
    [InlineData("Lightweight")]
    [InlineData("DedicatedMinimal")]
    public void Primary_Profile_Is_Meaningfully_Different_From_Balanced(string id)
    {
        // Part 4: a profile with only 1-3 executable changes is a product smell.
        var balanced = EvaluateProfile("Balanced");
        var profile = EvaluateProfile(id);

        Assert.True(profile.Auto >= 5,
            $"{id} automatic actions = {profile.Auto} — profile too weak (Part 4).");
        Assert.True(profile.Auto != balanced.Auto || profile.Manual != balanced.Manual,
            $"{id} must differ meaningfully from Balanced.");
    }

    [Fact]
    public void Recommendation_Reason_Includes_Profile_Source()
    {
        // Part 13: the advising profile is attributed.
        var input = Input("XboxApp", RecommendationLevel.OptionalRemove, RiskLevel.Low);
        var result = Eval(input, profiles: CatalogProfiles()["Gaming"]);

        Assert.Equal(EffectiveRecommendationLevel.RecommendKeep, result.Level);
        Assert.Contains("Gaming", result.AdvisedByProfileIds);
    }

    [Fact]
    public void Preview_Buckets_Show_Manual_And_Unsupported()
    {
        // Part 11/12: medium-risk trims appear in 需要确认; unsupported items in
        // 冲突/不可执行 — they never disappear merely because they cannot be auto-selected.
        var (state, customize) = BuildCustomize(MixedInventory());
        var vm = customize.Profiles!;
        vm.Profiles.Single(p => p.Definition.Id == "Lightweight").IsSelected = true;

        vm.ShowPreview();

        var manual = vm.PreviewGroups.Single(g => g.Kind == RecommendationPreviewGroupKind.Manual);
        Assert.Contains(manual.Items, i => i.DisplayName.Contains("VirtualMachinePlatform", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(manual.Items, i => i.DisplayName.Contains("OpenSSH", StringComparison.OrdinalIgnoreCase));

        var conflict = vm.PreviewGroups.Single(g => g.Kind == RecommendationPreviewGroupKind.Conflict);
        Assert.Contains(conflict.Items, i => i.DisplayName.Contains("OpenSSH", StringComparison.OrdinalIgnoreCase));
        Assert.True(vm.UnsupportedCount >= 1);
        _ = state;
    }

    [Fact]
    public void Preview_Groups_Show_Per_Tab_Breakdown()
    {
        var (state, customize) = BuildCustomize(AppxInventory("Microsoft.XboxApp", "Microsoft.BingWeather"));
        var vm = customize.Profiles!;
        vm.Profiles.Single(p => p.Definition.Id == "Gaming").IsSelected = true;
        vm.ShowPreview();

        var adopt = vm.PreviewGroups.Single(g => g.Kind == RecommendationPreviewGroupKind.Adopt);
        Assert.NotEmpty(adopt.TabBreakdown); // e.g. "Apps 1 · Privacy 4 · ..."
        Assert.Contains("Apps", adopt.TabBreakdown);
        _ = state;
    }

    [Fact]
    public void Profile_Impact_Report_Is_Generated()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Stage 11.4 — Profile Impact Report");
        sb.AppendLine();
        sb.AppendLine("> Modeled fixture: all 22 Apps + 12 Windows Components + 12 Services + 11 Privacy +");
        sb.AppendLine("> 10 System + 14 Personalization items present/applicable. Computed with the real");
        sb.AppendLine("> RecommendationEngine (Part 15 profile quality diagnostic).");
        sb.AppendLine();

        foreach (var profile in new ProfileCatalog().GetProfiles().Where(p => p.Kind == ProfileKind.Primary))
        {
            var counts = EvaluateProfile(profile.Id);
            sb.AppendLine("## " + profile.Id);
            sb.AppendLine("- Automatic (low-risk, adoptable): " + counts.Auto);
            sb.AppendLine("- Manual review: " + counts.Manual);
            sb.AppendLine("- Keep: " + counts.Keep);
            sb.AppendLine("- Unsupported: " + counts.Unsupported);
            sb.AppendLine("- Conflict: " + counts.Conflict);
            foreach (var tab in new[] { OptimizationTab.Apps, OptimizationTab.WindowsComponents, OptimizationTab.Services,
                OptimizationTab.Privacy, OptimizationTab.System, OptimizationTab.Personalization })
            {
                var (auto, manual) = counts.ByTab.TryGetValue(tab, out var t) ? t : (0, 0);
                sb.AppendLine($"  - {tab}: auto {auto} · manual {manual}");
            }

            if (counts.Auto <= 3)
            {
                sb.AppendLine("- **WARNING**: Automatic <= 3 — profile too weak (Part 4); needs evidence-backed rule expansion.");
            }

            sb.AppendLine();
        }

        var root = RepoRoot();
        var dir = System.IO.Path.Combine(root, ".tmp", "phase11");
        System.IO.Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "stage11.4-profile-impact.md");
        System.IO.File.WriteAllText(path, sb.ToString());

        Assert.True(System.IO.File.Exists(path));
        var text = System.IO.File.ReadAllText(path);
        Assert.Contains("## Gaming", text);
        Assert.Contains("## Balanced", text);
        Assert.Contains("Automatic (low-risk, adoptable):", text);
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

    private sealed record ProfileCounts(
        int Auto, int Manual, int Keep, int Unsupported, int Conflict,
        IReadOnlyDictionary<OptimizationTab, (int Auto, int Manual)> ByTab);

    /// <summary>
    /// Part 4/15 diagnostic: evaluates one primary profile (+ optional extras)
    /// against the full modeled fixture with the REAL engine and buckets every
    /// decision (automatic = Part J eligibility, manual review, keep, unsupported,
    /// conflict), with a per-tab breakdown.
    /// </summary>
    private static ProfileCounts EvaluateProfile(string primaryId, params string[] extraIds)
    {
        var engine = new RecommendationEngine();
        var catalog = new ProfileCatalog().GetProfiles().ToList();
        var profiles = new List<ProfileDefinition> { catalog.Single(p => p.Id == primaryId) };
        profiles.AddRange(catalog.Where(p => extraIds.Contains(p.Id)));
        var fixture = FullFixture();
        var presentIds = fixture.Select(f => f.LogicalId).ToHashSet(StringComparer.Ordinal);

        int auto = 0, manual = 0, keep = 0, unsupported = 0, conflict = 0;
        var byTab = new Dictionary<OptimizationTab, (int Auto, int Manual)>();
        foreach (var f in fixture)
        {
            var eff = engine.Evaluate(
                new RecommendationInput
                {
                    LogicalId = f.LogicalId,
                    Action = f.Action,
                    DefaultRecommendation = f.Recommendation,
                    Risk = f.Risk,
                    Removal = f.Removal,
                    IsPresent = true,
                    IsApplySupported = f.ApplySupported,
                    Dependencies = f.Dependencies,
                },
                new RecommendationContext
                {
                    SelectedProfiles = profiles,
                    PresentIds = presentIds,
                    UserOverrides = new HashSet<string>(),
                });

            var (a, m) = byTab.TryGetValue(f.Tab, out var t) ? t : (0, 0);
            if (!eff.IsApplySupported)
            {
                unsupported++;
            }
            else if (eff.HasConflict)
            {
                conflict++;
            }
            else if (eff.Level == EffectiveRecommendationLevel.RecommendKeep)
            {
                keep++;
            }
            else if (eff.IsApplySupported && eff.Risk == RiskLevel.Low && !eff.HasConflict &&
                     eff.Level is EffectiveRecommendationLevel.RecommendRemove
                         or EffectiveRecommendationLevel.RecommendDisable
                         or EffectiveRecommendationLevel.RecommendSet)
            {
                auto++;
                a++;
            }
            else
            {
                manual++;
                m++;
            }

            byTab[f.Tab] = (a, m);
        }

        return new ProfileCounts(auto, manual, keep, unsupported, conflict, byTab);
    }

    private sealed record FixtureItem(
        string LogicalId,
        OptimizationTab Tab,
        OptimizationAction Action,
        RecommendationLevel Recommendation,
        RiskLevel Risk,
        RemovalSupport Removal,
        bool ApplySupported,
        IReadOnlyList<ComponentDependency> Dependencies);

    /// <summary>All Stage 11.3 modeled items treated as present/applicable.</summary>
    private static List<FixtureItem> FullFixture()
    {
        var items = new List<FixtureItem>();
        foreach (var d in new CuratedComponentCatalog().GetDefinitions())
        {
            items.Add(new FixtureItem(d.Id, OptimizationTab.Apps, d.Action, d.Recommendation, d.Risk,
                d.Removal, true, d.Dependencies));
        }

        foreach (var d in new WindowsFeaturesCatalog().GetDefinitions())
        {
            var isCapability = d.Category == ComponentCategory.Capability;
            items.Add(new FixtureItem(d.Id, OptimizationTab.WindowsComponents, d.Action, d.Recommendation,
                d.Risk, d.Removal, !isCapability, d.Dependencies));
        }

        foreach (var o in new OptimizationCatalog().GetEntries())
        {
            items.Add(new FixtureItem(o.Id, o.Tab, o.Action, o.Recommendation, o.Risk, o.Removal, true, o.Dependencies));
        }

        return items;
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "WinForge.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private static ComponentInventory MixedInventory()
        => new()
        {
            Discovered = true,
            Categories = new[]
            {
                new CategoryDiscoveryResult
                {
                    Category = ComponentCategory.OptionalFeature,
                    Status = InventoryStatus.Success,
                    Items = new List<IRawInventoryItem>
                    {
                        new RawOptionalFeature
                        {
                            Category = ComponentCategory.OptionalFeature,
                            RawIdentity = "VirtualMachinePlatform",
                            DisplayName = "VirtualMachinePlatform",
                            FeatureStateValue = FeatureState.Enabled,
                        },
                    },
                },
                new CategoryDiscoveryResult
                {
                    Category = ComponentCategory.Capability,
                    Status = InventoryStatus.Success,
                    Items = new List<IRawInventoryItem>
                    {
                        new RawCapability
                        {
                            Category = ComponentCategory.Capability,
                            RawIdentity = "OpenSSH.Client~~~~0.0.1.0",
                            DisplayName = "OpenSSH Client",
                            CapState = CapabilityState.Installed,
                        },
                    },
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
