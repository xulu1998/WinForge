namespace WinForge.Core.Models;

/// <summary>
/// Runtime state of the customization engine, surfaced through
/// <see cref="IAppState.CustomizationExecutionState"/> so the UI can show a
/// busy / ready / result state and disable controls during execution.
/// </summary>
public enum CustomizationExecutionState
{
    /// <summary>No plan, nothing discovered yet.</summary>
    Idle,

    /// <summary>A discovery pass is running against the mounted image.</summary>
    Discovering,

    /// <summary>Discovery finished; a plan may be assembled.</summary>
    Ready,

    /// <summary>A plan is being executed.</summary>
    Executing,

    /// <summary>All operations succeeded.</summary>
    Completed,

    /// <summary>Execution finished with at least one failure.</summary>
    CompletedWithErrors,

    /// <summary>Execution failed critically.</summary>
    Failed,

    /// <summary>Execution was cancelled.</summary>
    Cancelled
}

/// <summary>
/// Strongly-typed offline registry value kinds. Core stays platform-agnostic
/// (no <c>Microsoft.Win32.RegistryValueKind</c> reference); the Infrastructure
/// Win32 implementation maps these to native kinds.
/// </summary>
public enum OfflineRegistryValueKind
{
    /// <summary>A 32-bit number.</summary>
    DWord,

    /// <summary>A string.</summary>
    String,

    /// <summary>A string that may contain unexpanded environment variables.</summary>
    ExpandString,

    /// <summary>A multi-line / multi-string value.</summary>
    MultiString,

    /// <summary>A 64-bit number.</summary>
    QWord,

    /// <summary>Binary data.</summary>
    Binary
}

/// <summary>
/// Windows service startup semantics, mapped to the native service Start value
/// by the offline registry implementation.
/// </summary>
public enum ServiceStartType
{
    /// <summary>Boot (kernel) start — never set by WinForge.</summary>
    Boot = 0,

    /// <summary>System start — never set by WinForge.</summary>
    System = 1,

    /// <summary>Automatic (demand for legacy naming).</summary>
    Automatic = 2,

    /// <summary>Manual.</summary>
    Manual = 3,

    /// <summary>Disabled.</summary>
    Disabled = 4
}
