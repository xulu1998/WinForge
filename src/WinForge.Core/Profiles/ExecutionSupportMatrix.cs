using System;
using WinForge.Core.Models;

namespace WinForge.Core.Profiles;

// =====================================================================
// Phase 15 Stage 15.1 — EXECUTION SUPPORT MATRIX (ADR-094 §4)
//
// Separates RECOMMENDATION from EXECUTION SUPPORT. This is the auditable,
// honest statement of which operation types WinForge can actually execute
// on an offline mounted image TODAY. Classification (Known) NEVER promotes
// itself into execution capability (ADR-086/093).
// =====================================================================

public enum ExecutionSupportStatus
{
    Unknown = 0,

    /// <summary>A validated execution path exists.</summary>
    Supported,

    /// <summary>Executable only under specific conditions (e.g. allowlisted services).</summary>
    Conditional,

    /// <summary>No safe execution path exists — never placed in an executable plan.</summary>
    NotSupported,
}

/// <summary>
/// Canonical, auditable execution support matrix (Stage 15.1 §4).
///
/// Supported today:
///   - AppX removal            (RemoveProvisionedAppx)
///   - Registry policy         (SetOfflineRegistryValue / DeleteOfflineRegistryValue)
///   - Privacy settings        (registry-backed PrivacyPolicy)
///   - Personalization         (registry-backed VisualPreference / ExplorerPreference)
///   - Service configuration   (ConfigureOfflineService — allowlisted services only)
///   - OptionalFeature disable (DisableOptionalFeature on the mounted image)
///
/// NOT supported today (kept honest, never silently promoted):
///   - Capability removal      (RemoveCapability exists as an op type but the
///                              execution path is NOT reviewed — Phase 11 note)
///   - CBS package removal     (RemovePackage — NO destructive CBS removal, ADR-093)
///   - Driver removal          (no operation type; never invented)
///   - Scheduled-task disable  (only when robust support exists — currently not)
/// </summary>
public static class ExecutionSupportMatrix
{
    public static ExecutionSupportStatus SupportFor(ExecutionOperationType type) => type switch
    {
        ExecutionOperationType.AppX => ExecutionSupportStatus.Supported,
        ExecutionOperationType.RegistryPolicy => ExecutionSupportStatus.Supported,
        ExecutionOperationType.Privacy => ExecutionSupportStatus.Supported,
        ExecutionOperationType.Personalization => ExecutionSupportStatus.Supported,
        ExecutionOperationType.Service => ExecutionSupportStatus.Conditional,
        ExecutionOperationType.OptionalFeature => ExecutionSupportStatus.Supported,
        ExecutionOperationType.Capability => ExecutionSupportStatus.NotSupported,
        ExecutionOperationType.CbsPackage => ExecutionSupportStatus.NotSupported,
        ExecutionOperationType.Driver => ExecutionSupportStatus.NotSupported,
        _ => ExecutionSupportStatus.Conditional,
    };

    public static ExecutionSupportStatus SupportFor(CustomizationOperationType type) => type switch
    {
        CustomizationOperationType.RemoveProvisionedAppx => ExecutionSupportStatus.Supported,
        CustomizationOperationType.SetOfflineRegistryValue
            or CustomizationOperationType.DeleteOfflineRegistryValue => ExecutionSupportStatus.Supported,
        CustomizationOperationType.ConfigureOfflineService => ExecutionSupportStatus.Conditional,
        CustomizationOperationType.DisableOptionalFeature => ExecutionSupportStatus.Supported,
        CustomizationOperationType.RemoveCapability => ExecutionSupportStatus.NotSupported,
        CustomizationOperationType.RemovePackage => ExecutionSupportStatus.NotSupported,
        CustomizationOperationType.RemoveOfflineFile => ExecutionSupportStatus.Conditional,
        _ => ExecutionSupportStatus.Conditional,
    };

    /// <summary>True when a safe executable path exists for the operation type.</summary>
    public static bool IsExecutable(ExecutionOperationType type)
        => SupportFor(type) != ExecutionSupportStatus.NotSupported;

    /// <summary>True when a safe executable path exists for the concrete operation type.</summary>
    public static bool IsExecutable(CustomizationOperationType type)
        => SupportFor(type) != ExecutionSupportStatus.NotSupported;

    /// <summary>
    /// The canonical reason key shown when an item is blocked because its change
    /// type has no supported execution path (deterministic, localized).
    /// </summary>
    public static string BlockReasonKey => "Profile.Reason.Execution.Unsupported";
}
