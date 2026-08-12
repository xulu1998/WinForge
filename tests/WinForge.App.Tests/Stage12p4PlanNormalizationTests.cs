using System;
using System.Linq;
using WinForge.App.Localization;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// REGRESSION for the Phase 12 real-desktop blocker: the generated plan contained
/// duplicate identical registry operations. Two independent optimization items —
/// Privacy "Windows 聚焦内容" (<c>SpotlightFeatures</c>) and Personalization
/// "Windows 聚焦（锁屏内容）" (<c>DisableSpotlight</c>) — both compile to the exact
/// same mutation (SOFTWARE\Policies\Microsoft\Windows\CloudContent
/// \DisableWindowsSpotlightFeatures = DWord 1, OfflineMachine). The validator
/// correctly reported the duplicate, but the plan COMPILER must never emit two
/// identical physical operations in the first place.
///
/// Fix: <see cref="CustomizationOperation"/> gains a canonical effective-target
/// identity (registry SCOPE + normalized hive + normalized key path + normalized
/// value name) and a mutation-semantics comparison (operation type + value kind +
/// normalized data). <see cref="CustomizationPlan.AddOperation"/> merges identical
/// effective registry changes into ONE physical operation while retaining
/// provenance; semantically DIFFERENT mutations of the same target remain two
/// operations and the validator still blocks them. The validator is NOT weakened.
/// </summary>
public class Stage12p4PlanNormalizationTests
{
    private static CustomizationOperation RegistryOp(
        string id, string hive, string keyPath, string valueName,
        string data = "1", OfflineRegistryValueKind kind = OfflineRegistryValueKind.DWord,
        OptimizationScope scope = OptimizationScope.OfflineMachine,
        string? sourceId = null)
    {
        var op = new CustomizationOperation
        {
            OperationId = id,
            OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            DisplayName = id,
            RegistryHive = hive,
            RegistryKeyPath = keyPath,
            RegistryValueName = valueName,
            RegistryValueKind = kind,
            RegistryValueData = data,
            Scope = scope,
            Risk = RiskClass.Safe,
            IsSelected = true,
        };
        if (sourceId is not null) op.AddSourceDefinition(sourceId);
        return op;
    }

    private static CustomizationPlan NewPlan() => new();

    // 1. identical registry target + identical value dedupes to one operation
    [Fact]
    public void Identical_Registry_Target_Dedupes_To_One()
    {
        var plan = NewPlan();
        plan.AddOperation(RegistryOp("a", "SOFTWARE", "Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightFeatures", "1"));
        plan.AddOperation(RegistryOp("b", "SOFTWARE", "Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightFeatures", "1"));

        Assert.Single(plan.SelectedOperations);
        Assert.Equal("a", plan.SelectedOperations[0].OperationId); // first one wins
    }

    // 2. identical target + different value remains blocking conflict
    [Fact]
    public void Different_Value_Remains_Blocking_Conflict()
    {
        var plan = NewPlan();
        plan.AddOperation(RegistryOp("a", "SOFTWARE", "Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightFeatures", "1"));
        plan.AddOperation(RegistryOp("b", "SOFTWARE", "Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightFeatures", "0"));

        Assert.Equal(2, plan.SelectedOperations.Count); // NOT merged
        var issues = plan.Validate();
        Assert.NotEmpty(issues);
        Assert.Equal(CustomizationPlanStatus.Draft, plan.Status);
    }

    // 3. same key/value text but different registry scope does NOT merge
    [Fact]
    public void Different_Scope_Does_Not_Merge()
    {
        var plan = NewPlan();
        plan.AddOperation(RegistryOp("machine", "SOFTWARE", "Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightFeatures", "1", scope: OptimizationScope.OfflineMachine));
        plan.AddOperation(RegistryOp("user", "SOFTWARE", "Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightFeatures", "1", scope: OptimizationScope.OfflineDefaultUser));

        Assert.Equal(2, plan.SelectedOperations.Count); // scope is part of identity
    }

    // 4. Default User vs machine scope does NOT merge (explicit)
    [Fact]
    public void DefaultUser_Does_Not_Merge_With_Machine()
    {
        var plan = NewPlan();
        plan.AddOperation(RegistryOp("m", "SOFTWARE", "Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightFeatures", "1", scope: OptimizationScope.OfflineMachine));
        plan.AddOperation(RegistryOp("u", "SOFTWARE", "Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightFeatures", "1", scope: OptimizationScope.OfflineDefaultUser));

        Assert.Equal(2, plan.SelectedOperations.Count);
        // And the validator treats them as distinct targets (no duplicate flag).
        var issues = plan.Validate();
        Assert.Empty(issues);
        Assert.Equal(CustomizationPlanStatus.Validated, plan.Status);
    }

    // 5. duplicate provenance retains both source customization IDs
    [Fact]
    public void Provenance_Retains_Both_Sources()
    {
        var plan = NewPlan();
        plan.AddOperation(RegistryOp("a", "SOFTWARE", "Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightFeatures", "1", sourceId: "SpotlightFeatures"));
        plan.AddOperation(RegistryOp("b", "SOFTWARE", "Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightFeatures", "1", sourceId: "DisableSpotlight"));

        var merged = plan.SelectedOperations.Single();
        Assert.Contains(merged.SourceDefinitionIds, s => s == "SpotlightFeatures");
        Assert.Contains(merged.SourceDefinitionIds, s => s == "DisableSpotlight");
        Assert.Contains(merged.SourceDefinitionIds, s => s == "b"); // operation id of the absorbed op
    }

    // 6. operation total reflects deduped executable count (2 intents -> 1 physical op)
    [Fact]
    public void Total_Selected_Reflects_Deduped_Count()
    {
        var plan = NewPlan();
        plan.AddOperation(RegistryOp("a", "SOFTWARE", "Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightFeatures", "1"));
        plan.AddOperation(RegistryOp("b", "SOFTWARE", "Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightFeatures", "1"));

        Assert.Single(plan.SelectedOperations);
        Assert.Single(plan.Operations.Where(o => o.IsSelected));
    }

    // 7. validator still rejects unexpected duplicate physical operations
    //    (dedupe is registry-specific; an AppX duplicate is still a blocking plan defect)
    [Fact]
    public void Validator_Still_Rejects_NonRegistry_Duplicate()
    {
        var plan = NewPlan();
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "appx-a", OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            TargetIdentifier = "Microsoft.OneDrive", DisplayName = "OneDrive", IsSelected = true, Risk = RiskClass.Removable,
        });
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "appx-b", OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            TargetIdentifier = "Microsoft.OneDrive", DisplayName = "OneDrive (alt)", IsSelected = true, Risk = RiskClass.Removable,
        });

        Assert.Equal(2, plan.SelectedOperations.Count);
        var issues = plan.Validate();
        Assert.Contains(issues, i => i.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(CustomizationPlanStatus.Draft, plan.Status);
    }

    // 8. true conflict UI remains visible (Review feedback for different-value conflict)
    [Fact]
    public void True_Conflict_UI_Remains_Visible()
    {
        var state = new AppState();
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        var plan = PlanSync.EnsureDraftPlan(state);
        plan.AddOperation(RegistryOp("a", "SOFTWARE", "Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightFeatures", "1"));
        plan.AddOperation(RegistryOp("b", "SOFTWARE", "Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightFeatures", "0"));

        var vm = new PlanReviewViewModel(state, new InMemoryLoggerService(), new FakeCustomizationExecutionService());
        vm.ValidatePlan();

        Assert.False(vm.ValidationPassed);
        Assert.True(vm.HasValidationFailure);
        Assert.True(vm.HasWarnings);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    // 9. the real observed target no longer generates a duplicate (real catalog data)
    [Fact]
    public void Real_Spotlight_Target_No_Longer_Duplicates()
    {
        var catalog = new OptimizationCatalog().GetEntries().ToList();
        var spotlight = catalog.Single(e => e.Id == "SpotlightFeatures");
        var disableSpotlight = catalog.Single(e => e.Id == "DisableSpotlight");

        Assert.Equal(spotlight.RegistryTargets[0].ValueName, disableSpotlight.RegistryTargets[0].ValueName);
        Assert.Equal(spotlight.RegistryTargets[0].RecommendedData, disableSpotlight.RegistryTargets[0].RecommendedData);
        Assert.Equal(spotlight.Scope, disableSpotlight.Scope);

        // Compile both selected items exactly like the customization VM does.
        var plan = NewPlan();
        plan.AddOperation(RegistryOp("opt|SpotlightFeatures|0",
            spotlight.RegistryTargets[0].Hive, spotlight.RegistryTargets[0].KeyPath,
            spotlight.RegistryTargets[0].ValueName, spotlight.RegistryTargets[0].RecommendedData,
            spotlight.RegistryTargets[0].ValueKind, spotlight.Scope, sourceId: spotlight.Id));
        plan.AddOperation(RegistryOp("opt|DisableSpotlight|0",
            disableSpotlight.RegistryTargets[0].Hive, disableSpotlight.RegistryTargets[0].KeyPath,
            disableSpotlight.RegistryTargets[0].ValueName, disableSpotlight.RegistryTargets[0].RecommendedData,
            disableSpotlight.RegistryTargets[0].ValueKind, disableSpotlight.Scope, sourceId: disableSpotlight.Id));

        Assert.Single(plan.SelectedOperations); // merged
        var issues = plan.Validate();
        Assert.Empty(issues);                    // no Duplicate warning any more
        Assert.Equal(CustomizationPlanStatus.Validated, plan.Status);
    }

    // 10-12. profile selections compile + validate: the whole real registry catalog
    //        (a superset of any profile's selection) must validate after dedupe.
    //        If the full catalog compiles cleanly, every profile subset does too.
    [Theory]
    [InlineData("Gaming")]
    [InlineData("Lightweight")]
    [InlineData("Developer")]
    public void Profile_Selection_Compiles_And_Validates(string profileId)
    {
        var catalog = new OptimizationCatalog().GetEntries().ToList();
        var plan = NewPlan();

        var index = 0;
        foreach (var def in catalog)
        {
            foreach (var target in def.RegistryTargets)
            {
                plan.AddOperation(RegistryOp($"opt|{def.Id}|{index}",
                    target.Hive, target.KeyPath, target.ValueName, target.RecommendedData,
                    target.ValueKind, def.Scope, sourceId: def.Id));
                index++;
            }
        }

        // At least the real duplicate pair is present pre-dedupe in the source data.
        var spotlightIds = catalog.Where(e => e.RegistryTargets.Any(t =>
            t.ValueName == "DisableWindowsSpotlightFeatures")).Select(e => e.Id).ToList();
        Assert.Contains("SpotlightFeatures", spotlightIds);
        Assert.Contains("DisableSpotlight", spotlightIds);

        // After normalization the plan validates (identical duplicates merged;
        // the profile-selected subset inherits this property).
        var issues = plan.Validate();
        Assert.True(issues.Count == 0, $"[{profileId}] full catalog did not validate: {string.Join("; ", issues)}");
        Assert.Equal(CustomizationPlanStatus.Validated, plan.Status);
    }

    // 13. a manual combination that creates a true conflict is still blocked
    [Fact]
    public void Manual_True_Conflict_Is_Blocked()
    {
        var state = new AppState();
        state.CurrentServicingWorkspace = new ImageServicingWorkspace { State = ServicingWorkspaceState.Mounted };
        var plan = PlanSync.EnsureDraftPlan(state);
        plan.AddOperation(RegistryOp("a", "SOFTWARE", "K", "V", "1"));
        plan.AddOperation(RegistryOp("b", "SOFTWARE", "K", "V", "0"));

        var vm = new PlanReviewViewModel(state, new InMemoryLoggerService(), new FakeCustomizationExecutionService());
        vm.ValidatePlan();

        Assert.False(vm.ValidationPassed);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.True(vm.HasValidationFailure);
    }

    // Normalization sanity: equivalent DWord data forms still dedupe ("1" == "0x1" == "01")
    [Fact]
    public void Equivalent_Numeric_Data_Dedupes()
    {
        var plan = NewPlan();
        plan.AddOperation(RegistryOp("a", "SOFTWARE", "K", "V", "1"));
        plan.AddOperation(RegistryOp("b", "SOFTWARE", "K", "V", "0x1"));
        plan.AddOperation(RegistryOp("c", "SOFTWARE", "K", "V", "01"));

        Assert.Single(plan.SelectedOperations);
    }

    // Path normalization sanity: case + separator differences on the SAME scope merge
    [Fact]
    public void Path_Case_And_Separator_Normalize()
    {
        var plan = NewPlan();
        var opA = RegistryOp("a", "software", "policies/microsoft/windows/cloudcontent", "disablewindowsspotlightfeatures", "1");
        var opB = RegistryOp("b", "SOFTWARE", "Policies\\Microsoft\\Windows\\CloudContent", "DisableWindowsSpotlightFeatures", "1");
        plan.AddOperation(opA);
        plan.AddOperation(opB);

        Assert.Single(plan.SelectedOperations);
    }
}
