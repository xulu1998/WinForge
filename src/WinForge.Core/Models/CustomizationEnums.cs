namespace WinForge.Core.Models;

/// <summary>
/// Concrete kinds of offline customization operations WinForge can perform
/// against the isolated, mounted working image (Step 3.3). Every operation is
/// declarative and targets a precise identity discovered from the offline
/// image — there is deliberately NO generic "run command" / "run script"
/// operation, so arbitrary host commands or PowerShell can never be injected.
/// </summary>
public enum CustomizationOperationType
{
    /// <summary>Remove a provisioned Appx package by its exact package identity.</summary>
    RemoveProvisionedAppx,

    /// <summary>Remove a Windows servicing package by its exact package identity.</summary>
    RemovePackage,

    /// <summary>Set a strongly-typed value in an offline registry hive.</summary>
    SetOfflineRegistryValue,

    /// <summary>Delete a value from an offline registry hive.</summary>
    DeleteOfflineRegistryValue,

    /// <summary>Reconfigure an offline Windows service startup type.</summary>
    ConfigureOfflineService,

    /// <summary>Disable an offline scheduled task (only when robust support exists).</summary>
    DisableOfflineScheduledTask,

    /// <summary>Remove a file/directory owned by WinForge or an explicitly sanctioned system target.</summary>
    RemoveOfflineFile
}

/// <summary>
/// High-level grouping used by the Components / Privacy / System UI so each
/// operation can be shown under the correct page and safety boundary.
/// </summary>
public enum CustomizationCategory
{
    /// <summary>Inbox / provisioned application packages (Components → Apps).</summary>
    App,

    /// <summary>Windows servicing / feature packages (Components → Packages).</summary>
    Package,

    /// <summary>Offline registry-backed privacy settings (Privacy page).</summary>
    Privacy,

    /// <summary>Offline registry/service system settings (System page).</summary>
    System,

    /// <summary>Offline Windows services (Components → System components).</summary>
    Service,

    /// <summary>Explicitly sanctioned offline file removals.</summary>
    File
}

/// <summary>
/// Declarative lifecycle of a <see cref="CustomizationPlan"/>. The plan must be
/// <see cref="Validated"/> before it can be executed, and becomes immutable /
/// execution-safe once execution begins.
/// </summary>
public enum CustomizationPlanStatus
{
    /// <summary>Being assembled; operations may be added/removed/toggled.</summary>
    Draft,

    /// <summary>Has been validated and is safe to execute.</summary>
    Validated,

    /// <summary>Execution is in progress.</summary>
    Executing,

    /// <summary>All operations completed successfully.</summary>
    Completed,

    /// <summary>Execution finished but at least one operation failed.</summary>
    CompletedWithErrors,

    /// <summary>Execution failed before completing (critical error).</summary>
    Failed,

    /// <summary>Execution was cancelled between operations.</summary>
    Cancelled
}

/// <summary>
/// Per-operation outcome after (or during) execution.
/// </summary>
public enum CustomizationOperationStatus
{
    /// <summary>Not yet executed.</summary>
    Pending,

    /// <summary>Currently executing.</summary>
    Running,

    /// <summary>Completed successfully.</summary>
    Succeeded,

    /// <summary>Failed but judged recoverable; execution may continue.</summary>
    FailedRecoverable,

    /// <summary>Failed critically; execution must stop.</summary>
    FailedCritical,

    /// <summary>Skipped (e.g. target not present in the offline image).</summary>
    Skipped
}

/// <summary>
/// Safety classification of an operation / discovered item. Drives whether the
/// UI may present it as selectable and whether real removal is permitted.
/// </summary>
public enum RiskClass
{
    /// <summary>Known-safe, explicitly sanctioned WinForge target.</summary>
    Safe,

    /// <summary>Removable only under a small allowlisted category.</summary>
    Removable,

    /// <summary>System / protected — must not be modified by this step.</summary>
    Protected,

    /// <summary>Unknown / unsupported — modification is never permitted.</summary>
    Unsupported
}

/// <summary>
/// Result of validating a single operation or the whole plan.
/// </summary>
public enum OperationValidationResult
{
    /// <summary>The operation is valid and may execute.</summary>
    Valid,

    /// <summary>The operation duplicates another selected operation.</summary>
    Duplicate,

    /// <summary>The operation conflicts with another selected operation.</summary>
    Conflict,

    /// <summary>The operation targets an unsupported / unsafe item.</summary>
    Unsupported,

    /// <summary>The operation is missing required identity/target data.</summary>
    MissingTarget
}
