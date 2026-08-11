using System;

namespace WinForge.Core.Models;

/// <summary>
/// A single declarative offline customization operation. A plan describes WHAT
/// WinForge intends to change before execution; each operation carries the
/// exact identity it targets (never a fuzzy match) plus its current validation
/// and execution status. The model is platform-agnostic — it holds data only,
/// not behaviour; the execution engine interprets it.
///
/// <para>
/// Payload fields are operation-type specific and optional:
/// <list type="bullet">
///   <item><description>Appx/Package removal uses <see cref="TargetIdentifier"/> (exact package identity).</description></item>
///   <item><description>Registry operations use <see cref="RegistryHive"/>, <see cref="RegistryKeyPath"/>,
///     <see cref="RegistryValueName"/>, <see cref="RegistryValueKind"/>, <see cref="RegistryValueData"/>.</description></item>
///   <item><description>Service configuration uses <see cref="ServiceName"/> and <see cref="ServiceStartType"/>.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class CustomizationOperation
{
    /// <summary>A stable, unique operation id (e.g. <c>appx:Microsoft.X...</c>).</summary>
    public string OperationId { get; init; } = Guid.NewGuid().ToString("N");

    public CustomizationCategory Category { get; init; }
    public CustomizationOperationType OperationType { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>Exact identity targeted by the operation (package name, service name, file id).</summary>
    public string? TargetIdentifier { get; init; }

    /// <summary>Whether the user has selected this operation for the plan.</summary>
    public bool IsSelected { get; set; }

    /// <summary>Safety classification of the underlying item.</summary>
    public RiskClass Risk { get; init; } = RiskClass.Unsupported;

    /// <summary>Execution order within the plan (lower runs first).</summary>
    public int ExecutionOrder { get; set; }

    public OperationValidationResult ValidationResult { get; set; } = OperationValidationResult.Valid;
    public CustomizationOperationStatus ExecutionStatus { get; set; } = CustomizationOperationStatus.Pending;
    public string? ErrorDetails { get; set; }

    // ---- Registry payload (SetOfflineRegistryValue / DeleteOfflineRegistryValue) ----

    public string? RegistryHive { get; init; }
    public string? RegistryKeyPath { get; init; }
    public string? RegistryValueName { get; init; }
    public OfflineRegistryValueKind? RegistryValueKind { get; init; }
    public string? RegistryValueData { get; init; }

    // ---- Service payload (ConfigureOfflineService) ----

    public string? ServiceName { get; init; }
    public ServiceStartType? ServiceStartType { get; init; }

    // ---- Stage 11.3 optimization metadata (ADR-051) ----
    // These describe WHAT KIND of change this is for the Review surface and the
    // offline-image scope it targets. They are data, never behaviour: views and
    // the plan display them, the execution engine still branches on the concrete
    // OperationType.

    /// <summary>User-visible kind of change (Remove / Disable / Configure / Service / Feature).</summary>
    public OptimizationAction? ActionKind { get; init; }

    /// <summary>Concrete technical mechanism (ServiceStartup, ExplorerPreference, …).</summary>
    public OptimizationMechanism? Mechanism { get; init; }

    /// <summary>Offline-image scope the change applies to (OfflineMachine / OfflineDefaultUser / …).</summary>
    public OptimizationScope? Scope { get; init; }

    /// <summary>Localization key describing how to revert this change (empty = generic restore text).</summary>
    public string? ReversalKey { get; init; }

    /// <summary>
    /// The Windows/default value WinForge restores on revert (registry operations).
    /// For a freshly-created offline image the "original" value may not exist, so
    /// WinForge records the documented default it would restore instead (Part O).
    /// </summary>
    public string? RestoreValueData { get; init; }

    /// <summary>
    /// Returns the canonical conflict key used for duplicate/conflict detection.
    /// Two operations with the same key target the same concrete change.
    /// </summary>
    public string ConflictKey => OperationType switch
    {
        CustomizationOperationType.SetOfflineRegistryValue or CustomizationOperationType.DeleteOfflineRegistryValue
            => $"reg|{RegistryHive}|{RegistryKeyPath}|{RegistryValueName}",
        CustomizationOperationType.ConfigureOfflineService
            => $"svc|{ServiceName}",
        CustomizationOperationType.RemoveProvisionedAppx or CustomizationOperationType.RemovePackage
            => $"pkg|{TargetIdentifier}",
        CustomizationOperationType.DisableOptionalFeature
            => $"feat|{TargetIdentifier}",
        CustomizationOperationType.RemoveCapability
            => $"cap|{TargetIdentifier}",
        CustomizationOperationType.RemoveOfflineFile
            => $"file|{TargetIdentifier}",
        _ => OperationId
    };

    /// <summary>
    /// Two operations conflict when one sets a registry value and the other
    /// deletes the same value (or they set the same value to different data).
    /// </summary>
    public bool ConflictsWith(CustomizationOperation other)
    {
        if (other is null || ReferenceEquals(this, other))
        {
            return false;
        }

        if (ConflictKey != other.ConflictKey)
        {
            return false;
        }

        if (OperationType == CustomizationOperationType.SetOfflineRegistryValue &&
            other.OperationType == CustomizationOperationType.DeleteOfflineRegistryValue)
        {
            return true;
        }

        if (OperationType == CustomizationOperationType.DeleteOfflineRegistryValue &&
            other.OperationType == CustomizationOperationType.SetOfflineRegistryValue)
        {
            return true;
        }

        if (OperationType == CustomizationOperationType.SetOfflineRegistryValue &&
            other.OperationType == CustomizationOperationType.SetOfflineRegistryValue &&
            !string.Equals(RegistryValueData, other.RegistryValueData, StringComparison.Ordinal))
        {
            return true;
        }

        if (OperationType == CustomizationOperationType.ConfigureOfflineService &&
            other.OperationType == CustomizationOperationType.ConfigureOfflineService &&
            ServiceStartType != other.ServiceStartType)
        {
            return true;
        }

        return false;
    }
}
