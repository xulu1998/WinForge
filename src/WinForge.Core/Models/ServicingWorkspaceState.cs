namespace WinForge.Core.Models;

/// <summary>
/// Explicit lifecycle state of an offline image servicing session (Step 3.2).
/// The transitions form a strict state machine; the servicing service and the
/// UI only permit operations that are valid from the current state. The session
/// never represents a temporary ISO mount — the durable source is the original
/// ISO and the working image lives under a WinForge-owned directory.
///
/// Valid transitions:
/// NotPrepared → Preparing → Prepared
/// Prepared    → Mounting  → Mounted
/// Mounted     → Unmounting → Prepared (discard) or → Completed/Unmounted
/// any runnable state (Preparing/Mounting/Unmounting) → Failed
/// Failed      → Prepared (after recovery / re-prepare)
/// </summary>
public enum ServicingWorkspaceState
{
    /// <summary>No servicing session has been prepared yet.</summary>
    NotPrepared,

    /// <summary>A prepare (export of the selected index) is in progress.</summary>
    Preparing,

    /// <summary>The working image has been exported and validated; ready to mount.</summary>
    Prepared,

    /// <summary>A DISM mount of the working image is in progress.</summary>
    Mounting,

    /// <summary>The working image is mounted and available for later phases.</summary>
    Mounted,

    /// <summary>A discard-only unmount of the working image is in progress.</summary>
    Unmounting,

    /// <summary>The session has been cleanly unmounted and returned to a safe state.</summary>
    Completed,

    /// <summary>The last servicing operation failed; see <see cref="ImageServicingWorkspace.LastError"/>.</summary>
    Failed
}

/// <summary>
/// Outcome classification returned by the servicing service so callers and the UI
/// can distinguish a healthy ready/active session from a stale or invalid one
/// without inspecting raw DISM output.
/// </summary>
public enum ServicingHealth
{
    /// <summary>Session is ready/active and consistent (Prepared/Mounted/Completed).</summary>
    Ready,

    /// <summary>Session has been prepared but the working image or mount is missing/inconsistent.</summary>
    Prepared,

    /// <summary>The working image is mounted and the mount is registered.</summary>
    Mounted,

    /// <summary>A previous session left artifacts or registrations that must be cleaned up.</summary>
    Stale,

    /// <summary>The session or one of its parameters is invalid (e.g. missing source workspace).</summary>
    Invalid,

    /// <summary>The last operation failed.</summary>
    Failed
}
