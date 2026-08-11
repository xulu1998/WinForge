using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace WinForge.Core.Models;

/// <summary>
/// A declarative, platform-agnostic customization plan (Step 3.3). It describes
/// WHAT WinForge intends to change in the isolated, mounted working image before
/// any execution happens. Lifecycle:
///
/// <list type="bullet">
///   <item><description><see cref="CustomizationPlanStatus.Draft"/> — operations may be added/removed/toggled.</description></item>
///   <item><description><see cref="CustomizationPlanStatus.Validated"/> — passed validation; safe to execute.</description></item>
///   <item><description><see cref="CustomizationPlanStatus.Executing"/> — a frozen snapshot is being applied.</description></item>
///   <item><description><see cref="CustomizationPlanStatus.Completed"/> / <see cref="CompletedWithErrors"/> / <see cref="Failed"/> / <see cref="Cancelled"/>.</description></item>
/// </list>
///
/// <para>
/// Rules enforced by the model:
/// - Execution requires <see cref="CustomizationPlanStatus.Validated"/>.
/// - Duplicate operations (same <see cref="CustomizationOperation.ConflictKey"/>) are flagged.
/// - Conflicting operations are flagged and block execution.
/// - Invalid operations (unsupported / missing target) block execution.
/// - Once execution begins the plan is frozen (no further edits).
/// </para>
/// </summary>
public sealed class CustomizationPlan : INotifyPropertyChanged
{
    public string Id { get; init; } = "plan-" + Guid.NewGuid().ToString("N").Substring(0, 12);

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? ValidatedAt { get; private set; }

    private CustomizationPlanStatus _status = CustomizationPlanStatus.Draft;

    public CustomizationPlanStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged(nameof(Status));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// Selection count and validity are aggregate, computed properties. Whenever an
    /// operation is added, removed, or its selection is toggled, surface the change
    /// so subscribers (the workflow coordinator and the review view) can react live.
    /// Without this, the in-place plan mutation performed by <see cref="PlanSync"/>
    /// would never reach <see cref="IAppState"/> listeners and the Review/Next gating
    /// would stay frozen at its last recomputed value.
    /// </summary>
    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedOperations));
        OnPropertyChanged(nameof(IsValid));
    }

    private readonly List<CustomizationOperation> _operations = new();
    public IReadOnlyList<CustomizationOperation> Operations => _operations;

    /// <summary>Selected operations only (the ones the user intends to apply).</summary>
    public IReadOnlyList<CustomizationOperation> SelectedOperations =>
        _operations.Where(o => o.IsSelected).ToList();

    public void AddOperation(CustomizationOperation operation)
    {
        if (operation is null) throw new ArgumentNullException(nameof(operation));
        if (Status is CustomizationPlanStatus.Executing or CustomizationPlanStatus.Completed
            or CustomizationPlanStatus.CompletedWithErrors or CustomizationPlanStatus.Failed
            or CustomizationPlanStatus.Cancelled)
        {
            throw new InvalidOperationException("Cannot modify a plan that has started or finished.");
        }

        _operations.Add(operation);
        RaiseSelectionChanged();
    }

    public void RemoveOperation(string operationId)
    {
        if (Status is CustomizationPlanStatus.Executing or CustomizationPlanStatus.Completed
            or CustomizationPlanStatus.CompletedWithErrors or CustomizationPlanStatus.Failed
            or CustomizationPlanStatus.Cancelled)
        {
            throw new InvalidOperationException("Cannot modify a plan that has started or finished.");
        }

        _operations.RemoveAll(o => o.OperationId == operationId);
        RaiseSelectionChanged();
    }

    public void SetSelected(string operationId, bool selected)
    {
        if (Status is CustomizationPlanStatus.Executing or CustomizationPlanStatus.Completed
            or CustomizationPlanStatus.CompletedWithErrors or CustomizationPlanStatus.Failed
            or CustomizationPlanStatus.Cancelled)
        {
            return;
        }

        var op = _operations.FirstOrDefault(o => o.OperationId == operationId);
        if (op is not null)
        {
            op.IsSelected = selected;
            RaiseSelectionChanged();
        }
    }

    /// <summary>
    /// Recomputes <see cref="CustomizationOperation.ValidationResult"/> for every
    /// operation, detecting duplicates, conflicts, and unsupported / missing
    /// targets. Returns the list of human-readable issues (empty when clean).
    /// </summary>
    public IReadOnlyList<string> RecomputeValidation()
    {
        var issues = new List<string>();
        var selected = _operations.Where(o => o.IsSelected).ToList();

        // Reset then classify.
        foreach (var op in _operations)
        {
            op.ValidationResult = ClassifyBase(op);
        }

        // Duplicate detection: more than one selected op sharing a ConflictKey.
        var grouped = selected
            .GroupBy(o => o.ConflictKey)
            .Where(g => g.Count() > 1);
        foreach (var dup in grouped)
        {
            foreach (var op in dup)
            {
                op.ValidationResult = OperationValidationResult.Duplicate;
            }
            issues.Add($"Duplicate operations target the same change: {dup.Key}.");
        }

        // Conflict detection: selected ops that conflict pairwise.
        for (var i = 0; i < selected.Count; i++)
        {
            for (var j = i + 1; j < selected.Count; j++)
            {
                if (selected[i].ConflictsWith(selected[j]))
                {
                    selected[i].ValidationResult = OperationValidationResult.Conflict;
                    selected[j].ValidationResult = OperationValidationResult.Conflict;
                    issues.Add($"Conflicting operations: {selected[i].DisplayName} vs {selected[j].DisplayName}.");
                }
            }
        }

        // Selected operations that are unsupported or missing a required target
        // are blocking issues that must be surfaced to the user before execution.
        foreach (var op in selected)
        {
            if (op.ValidationResult == OperationValidationResult.Unsupported)
            {
                issues.Add($"Operation is not supported and cannot be applied: {op.DisplayName}.");
            }
            else if (op.ValidationResult == OperationValidationResult.MissingTarget)
            {
                issues.Add($"Operation is missing a required target (id/registry/service): {op.DisplayName}.");
            }
        }

        OnPropertyChanged(nameof(IsValid));
        return issues;
    }

    private static OperationValidationResult ClassifyBase(CustomizationOperation op)
    {
        if (op.Risk is RiskClass.Protected or RiskClass.Unsupported)
        {
            return op.IsSelected ? OperationValidationResult.Unsupported : OperationValidationResult.Valid;
        }

        // ADR-030: a ConfigureOfflineService whose service name is not on the
        // trusted allowlist is never permitted, regardless of how the operation
        // was constructed. This rejects a manually injected unapproved service op.
        if (op.OperationType == CustomizationOperationType.ConfigureOfflineService &&
            !ServiceConfigPolicy.IsConfigurable(op.ServiceName))
        {
            return OperationValidationResult.Unsupported;
        }

        if (op.OperationType is CustomizationOperationType.RemoveProvisionedAppx or CustomizationOperationType.RemovePackage
            or CustomizationOperationType.RemoveOfflineFile
            or CustomizationOperationType.DisableOptionalFeature
            or CustomizationOperationType.RemoveCapability)
        {
            if (string.IsNullOrWhiteSpace(op.TargetIdentifier))
            {
                return OperationValidationResult.MissingTarget;
            }
        }

        if (op.OperationType is CustomizationOperationType.SetOfflineRegistryValue or CustomizationOperationType.DeleteOfflineRegistryValue)
        {
            if (string.IsNullOrWhiteSpace(op.RegistryHive) ||
                string.IsNullOrWhiteSpace(op.RegistryKeyPath) ||
                string.IsNullOrWhiteSpace(op.RegistryValueName))
            {
                return OperationValidationResult.MissingTarget;
            }
        }

        if (op.OperationType == CustomizationOperationType.ConfigureOfflineService)
        {
            if (string.IsNullOrWhiteSpace(op.ServiceName) || op.ServiceStartType is null)
            {
                return OperationValidationResult.MissingTarget;
            }
        }

        return OperationValidationResult.Valid;
    }

    /// <summary>True when the plan has no blocking validation issues.</summary>
    public bool IsValid => !_operations.Any(o => o.IsSelected && o.ValidationResult
        is OperationValidationResult.Duplicate or OperationValidationResult.Conflict
        or OperationValidationResult.Unsupported or OperationValidationResult.MissingTarget);

    /// <summary>
    /// Marks the plan <see cref="Validated"/> when it is valid and has at least
    /// one selected operation. Returns the validation issues (empty on success).
    /// A plan that fails validation cannot be marked valid and cannot execute.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        if (Status is CustomizationPlanStatus.Executing or CustomizationPlanStatus.Completed
            or CustomizationPlanStatus.CompletedWithErrors or CustomizationPlanStatus.Failed
            or CustomizationPlanStatus.Cancelled)
        {
            return new[] { "Plan has already started or finished; cannot re-validate." };
        }

        var issues = RecomputeValidation();
        if (issues.Count > 0 || !IsValid)
        {
            Status = CustomizationPlanStatus.Draft;
            return issues;
        }

        if (!SelectedOperations.Any())
        {
            Status = CustomizationPlanStatus.Draft;
            return new[] { "No operations are selected; nothing to execute." };
        }

        Status = CustomizationPlanStatus.Validated;
        ValidatedAt = DateTime.UtcNow;
        return Array.Empty<string>();
    }

    /// <summary>
    /// Freezes the plan for execution. Returns a shallow, execution-safe snapshot
    /// (operations cloned; selection/validation locked). Throws if the plan is
    /// not <see cref="Validated"/>.
    /// </summary>
    public CustomizationPlan FreezeForExecution()
    {
        if (Status != CustomizationPlanStatus.Validated)
        {
            throw new InvalidOperationException("A plan must be Validated before execution.");
        }

        var snapshot = new CustomizationPlan
        {
            Id = Id,
            CreatedAt = CreatedAt,
            ValidatedAt = ValidatedAt,
            Status = CustomizationPlanStatus.Executing
        };

        foreach (var op in _operations.Where(o => o.IsSelected))
        {
            snapshot._operations.Add(new CustomizationOperation
            {
                OperationId = op.OperationId,
                Category = op.Category,
                OperationType = op.OperationType,
                DisplayName = op.DisplayName,
                Description = op.Description,
                TargetIdentifier = op.TargetIdentifier,
                IsSelected = true,
                Risk = op.Risk,
                ExecutionOrder = op.ExecutionOrder,
                ValidationResult = op.ValidationResult,
                RegistryHive = op.RegistryHive,
                RegistryKeyPath = op.RegistryKeyPath,
                RegistryValueName = op.RegistryValueName,
                RegistryValueKind = op.RegistryValueKind,
                RegistryValueData = op.RegistryValueData,
                ServiceName = op.ServiceName,
                ServiceStartType = op.ServiceStartType,
                ActionKind = op.ActionKind,
                Mechanism = op.Mechanism,
                Scope = op.Scope,
                ReversalKey = op.ReversalKey,
                RestoreValueData = op.RestoreValueData
            });
        }

        // Lock the live plan so it cannot be edited mid-execution.
        Status = CustomizationPlanStatus.Executing;
        return snapshot;
    }

    /// <summary>
    /// Marks the plan completed after execution. Only valid while the plan is
    /// <see cref="Executing"/> (i.e. after <see cref="FreezeForExecution"/>).
    /// </summary>
    public void MarkCompleted(bool withErrors)
    {
        if (Status != CustomizationPlanStatus.Executing)
        {
            return;
        }

        Status = withErrors ? CustomizationPlanStatus.CompletedWithErrors : CustomizationPlanStatus.Completed;
    }

    /// <summary>Marks the plan failed. Only valid while <see cref="Executing"/>.</summary>
    public void MarkFailed()
    {
        if (Status != CustomizationPlanStatus.Executing)
        {
            return;
        }

        Status = CustomizationPlanStatus.Failed;
    }

    /// <summary>Marks the plan cancelled. Only valid while <see cref="Executing"/>.</summary>
    public void MarkCancelled()
    {
        if (Status != CustomizationPlanStatus.Executing)
        {
            return;
        }

        Status = CustomizationPlanStatus.Cancelled;
    }
}
