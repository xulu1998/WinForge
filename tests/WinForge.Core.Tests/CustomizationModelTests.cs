using System;
using System.Linq;
using WinForge.Core.Models;
using Xunit;

namespace WinForge.Core.Tests;

/// <summary>
/// Core model behaviour for the Step 3.3 declarative customization plan and
/// operations: lifecycle immutability, validation gating, duplicate/conflict
/// detection, and freeze semantics.
/// </summary>
public class CustomizationModelTests
{
    private static CustomizationOperation Appx(string id, bool selected = true) => new()
    {
        OperationId = id,
        Category = CustomizationCategory.App,
        OperationType = CustomizationOperationType.RemoveProvisionedAppx,
        DisplayName = id,
        TargetIdentifier = id,
        Risk = RiskClass.Removable,
        IsSelected = selected
    };

    // ---- Plan lifecycle ----

    [Fact]
    public void NewPlan_IsDraft_WithNoOperations()
    {
        var plan = new CustomizationPlan();
        Assert.Equal(CustomizationPlanStatus.Draft, plan.Status);
        Assert.Empty(plan.Operations);
        Assert.Empty(plan.SelectedOperations);
    }

    [Fact]
    public void AddOperation_ThenRemove_WorksInDraft()
    {
        var plan = new CustomizationPlan();
        plan.AddOperation(Appx("a"));
        Assert.Single(plan.Operations);
        plan.RemoveOperation("a");
        Assert.Empty(plan.Operations);
    }

    [Fact]
    public void AddOperation_ThrowsOnceExecuting()
    {
        var plan = new CustomizationPlan();
        plan.AddOperation(Appx("a"));
        plan.Validate();
        plan.FreezeForExecution();
        Assert.Throws<InvalidOperationException>(() => plan.AddOperation(Appx("b")));
    }

    [Fact]
    public void Validate_RequiresAtLeastOneSelected()
    {
        var plan = new CustomizationPlan();
        plan.AddOperation(Appx("a", selected: false));
        var issues = plan.Validate();
        Assert.NotEmpty(issues);
        Assert.NotEqual(CustomizationPlanStatus.Validated, plan.Status);
    }

    [Fact]
    public void Validate_Succeeds_WithSelectedOperation()
    {
        var plan = new CustomizationPlan();
        plan.AddOperation(Appx("a"));
        var issues = plan.Validate();
        Assert.Empty(issues);
        Assert.Equal(CustomizationPlanStatus.Validated, plan.Status);
        Assert.NotNull(plan.ValidatedAt);
    }

    [Fact]
    public void FreezeForExecution_RequiresValidated()
    {
        var plan = new CustomizationPlan();
        plan.AddOperation(Appx("a"));
        Assert.Throws<InvalidOperationException>(() => plan.FreezeForExecution());
    }

    [Fact]
    public void FreezeForExecution_LocksLivePlan_AndReturnsSelectedOnly()
    {
        var plan = new CustomizationPlan();
        plan.AddOperation(Appx("selected"));
        plan.AddOperation(Appx("unselected", selected: false));
        plan.Validate();

        var snapshot = plan.FreezeForExecution();

        Assert.Equal(CustomizationPlanStatus.Executing, plan.Status);
        Assert.Single(snapshot.Operations);
        Assert.Equal("selected", snapshot.Operations[0].OperationId);
    }

    [Fact]
    public void MarkCompleted_AfterExecuting_TransitionsState()
    {
        var plan = new CustomizationPlan();
        plan.AddOperation(Appx("a"));
        plan.Validate();
        plan.FreezeForExecution();
        plan.MarkCompleted(withErrors: false);
        Assert.Equal(CustomizationPlanStatus.Completed, plan.Status);
    }

    [Fact]
    public void MarkCompleted_WithErrors_SetsCompletedWithErrors()
    {
        var plan = new CustomizationPlan();
        plan.AddOperation(Appx("a"));
        plan.Validate();
        plan.FreezeForExecution();
        plan.MarkCompleted(withErrors: true);
        Assert.Equal(CustomizationPlanStatus.CompletedWithErrors, plan.Status);
    }

    // ---- Validation gating ----

    [Fact]
    public void UnsupportedSelected_BlocksValidation()
    {
        var plan = new CustomizationPlan();
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "x",
            OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            TargetIdentifier = "x",
            Risk = RiskClass.Unsupported,
            IsSelected = true
        });
        var issues = plan.Validate();
        Assert.NotEmpty(issues);
        Assert.False(plan.IsValid);
    }

    [Fact]
    public void MissingTarget_BlocksValidation()
    {
        var plan = new CustomizationPlan();
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "x",
            OperationType = CustomizationOperationType.RemovePackage,
            Risk = RiskClass.Removable,
            IsSelected = true
        });
        var issues = plan.Validate();
        Assert.NotEmpty(issues);
        Assert.False(plan.IsValid);
    }

    // ---- Duplicate / conflict detection ----

    [Fact]
    public void RecomputeValidation_FlagsDuplicateSelected()
    {
        var plan = new CustomizationPlan();
        plan.AddOperation(Appx("dup"));
        plan.AddOperation(Appx("dup")); // same OperationId but ConflictKey also same
        var issues = plan.RecomputeValidation();
        Assert.Contains(plan.Operations, o => o.ValidationResult == OperationValidationResult.Duplicate);
        Assert.NotEmpty(issues);
    }

    [Fact]
    public void RecomputeValidation_FlagsSetVsDeleteConflict()
    {
        var plan = new CustomizationPlan();
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "r1", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K", RegistryValueName = "V",
            RegistryValueData = "1", Risk = RiskClass.Safe, IsSelected = true
        });
        plan.AddOperation(new CustomizationOperation
        {
            OperationId = "r2", OperationType = CustomizationOperationType.DeleteOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K", RegistryValueName = "V",
            Risk = RiskClass.Safe, IsSelected = true
        });
        plan.RecomputeValidation();
        Assert.All(plan.Operations, o => Assert.Equal(OperationValidationResult.Conflict, o.ValidationResult));
    }

    [Fact]
    public void TwoSetSameKeyDifferentData_Conflicts()
    {
        var a = new CustomizationOperation
        {
            OperationId = "a", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K", RegistryValueName = "V",
            RegistryValueData = "1", IsSelected = true
        };
        var b = new CustomizationOperation
        {
            OperationId = "b", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K", RegistryValueName = "V",
            RegistryValueData = "0", IsSelected = true
        };
        Assert.True(a.ConflictsWith(b));
    }

    [Fact]
    public void SetAndDelete_DifferentKeys_NoConflict()
    {
        var a = new CustomizationOperation
        {
            OperationId = "a", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K1", RegistryValueName = "V",
            RegistryValueData = "1", IsSelected = true
        };
        var b = new CustomizationOperation
        {
            OperationId = "b", OperationType = CustomizationOperationType.DeleteOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K2", RegistryValueName = "V",
            IsSelected = true
        };
        Assert.False(a.ConflictsWith(b));
    }

    [Fact]
    public void ConflictKey_Uniqueness_PerType()
    {
        var appx = Appx("pkg1");
        var svc = new CustomizationOperation
        {
            OperationId = "s", OperationType = CustomizationOperationType.ConfigureOfflineService,
            ServiceName = "svc1", ServiceStartType = ServiceStartType.Disabled, IsSelected = true
        };
        Assert.NotEqual(appx.ConflictKey, svc.ConflictKey);
        Assert.Equal("pkg|pkg1", appx.ConflictKey);
        Assert.Equal("svc|svc1", svc.ConflictKey);
    }
}
