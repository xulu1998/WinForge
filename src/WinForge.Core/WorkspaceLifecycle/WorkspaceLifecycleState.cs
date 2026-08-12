namespace WinForge.Core.WorkspaceLifecycle;

/// <summary>
/// Explicit lifecycle state of a WinForge servicing workspace (Phase 12). A
/// workspace is NEVER classified purely from directory existence — this durable
/// state (persisted in the workspace manifest) plus the ACTUAL DISM mount
/// registration drive every cleanup decision.
///
/// <para>Terminal cleanup-eligible states: <see cref="FailedDisposable"/>,
/// <see cref="Cancelled"/> (disposable), <see cref="Completed"/> (after build
/// output retained elsewhere), <see cref="Cleaned"/>. States that must be
/// protected: <see cref="Mounted"/>, <see cref="FailedRecoverable"/>,
/// <see cref="BuildCheckpoint"/> (recoverable).</para>
/// </summary>
public enum WorkspaceLifecycleState
{
    /// <summary>Workspace directory was created; no servicing operation started yet.</summary>
    Created,

    /// <summary>Index export / prepare in progress.</summary>
    Preparing,

    /// <summary>Working WIM exported; ready to mount (or currently prepared).</summary>
    Prepared,

    /// <summary>Working image is mounted (verify against DISM before cleanup).</summary>
    Mounted,

    /// <summary>Customization applied against the mounted image.</summary>
    Customized,

    /// <summary>Working image committed (unmounted); build may export/rebuild.</summary>
    Committed,

    /// <summary>A durable, resumable build checkpoint exists (exported install.wim etc.).</summary>
    BuildCheckpoint,

    /// <summary>Workflow completed; workspace may become a cleanup candidate once outputs are preserved.</summary>
    Completed,

    /// <summary>Failed but retry requires the checkpoint — retain minimal artifacts only.</summary>
    FailedRecoverable,

    /// <summary>Failed before any meaningful checkpoint — safe to clean automatically.</summary>
    FailedDisposable,

    /// <summary>Build/servicing cancelled; disposable staging may be cleaned.</summary>
    Cancelled,

    /// <summary>Detected at startup without a matching live session — treat carefully (mount check first).</summary>
    Orphaned,

    /// <summary>Cleanup is in progress for this workspace.</summary>
    Cleaning,

    /// <summary>Workspace has been cleaned up (directory may or may not exist yet).</summary>
    Cleaned
}

/// <summary>
/// Why a workspace is being retained (or must not be deleted). Drives the safe
/// cleanup summary and automatic-cleanup policy (Part G).
/// </summary>
public enum WorkspaceRetentionReason
{
    /// <summary>None — the workspace is a cleanup candidate.</summary>
    None,

    /// <summary>The mount path is currently registered as mounted by DISM — never auto-delete.</summary>
    ActiveMount,

    /// <summary>A recoverable build checkpoint needs this workspace (minimal artifacts retained).</summary>
    RecoverableBuildCheckpoint,

    /// <summary>The user explicitly chose to keep this workspace.</summary>
    ExplicitUserKeep,

    /// <summary>A previous cleanup failed — retry later, report the leftover path.</summary>
    CleanupFailure
}

/// <summary>
/// Startup classification of a wf-* workspace (Part F). Never delete
/// <see cref="Unknown"/> / <see cref="Broken"/> without user inspection.
/// </summary>
public enum WorkspaceClassification
{
    /// <summary>Workspace belongs to the active session / is mounted — protected.</summary>
    Active,

    /// <summary>Failed but a recoverable checkpoint exists — keep minimal artifacts.</summary>
    Recoverable,

    /// <summary>Terminal state with no retained value — safe cleanup candidate.</summary>
    Disposable,

    /// <summary>Manifest or directory is corrupt/inconsistent — inspect before acting.</summary>
    Broken,

    /// <summary>Legacy pre-Phase-12 workspace (no manifest). Classify via DISM mount check only.</summary>
    LegacyUnknown,

    /// <summary>Could not be classified (e.g. mount query failed) — fail closed, do not delete.</summary>
    Unknown
}
