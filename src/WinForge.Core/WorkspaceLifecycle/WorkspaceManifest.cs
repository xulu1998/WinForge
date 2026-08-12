using System;
using System.Collections.Generic;

namespace WinForge.Core.WorkspaceLifecycle;

/// <summary>
/// Durable per-workspace manifest persisted as <c>workspace.json</c> inside each
/// <c>wf-*</c> directory (Part A). Persisted metadata lets WinForge determine
/// safe cleanup after a restart WITHOUT trusting directory existence. The
/// manifest's <see cref="IsMountedKnown"/> is only a hint — the ACTUAL DISM
/// mounted-image registration is always authoritative (Part B).
/// </summary>
public sealed class WorkspaceManifest
{
    /// <summary>Stable workspace id (single path segment, e.g. <c>wf-abc123</c>).</summary>
    public string WorkspaceId { get; set; } = string.Empty;

    /// <summary>UTC creation time.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>UTC last touch (state transition or activity).</summary>
    public DateTime LastUsedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Current lifecycle state (see <see cref="WorkspaceLifecycleState"/>).</summary>
    public WorkspaceLifecycleState CurrentState { get; set; } = WorkspaceLifecycleState.Created;

    /// <summary>Path of the original source ISO this workspace was derived from.</summary>
    public string? SourceIsoPath { get; set; }

    /// <summary>Absolute path of the isolated working WIM (e.g. …\wf-xxx\image\install.wim).</summary>
    public string? WorkingWimPath { get; set; }

    /// <summary>Absolute path of the dedicated mount directory (e.g. …\wf-xxx\mount).</summary>
    public string? MountPath { get; set; }

    /// <summary>
    /// Hint recorded at the last transition: did WinForge believe the image was
    /// mounted? NEVER authoritative — the live DISM query overrides this.
    /// </summary>
    public bool IsMountedKnown { get; set; }

    /// <summary>True when a durable build checkpoint artifact exists (resumable build).</summary>
    public bool HasBuildCheckpoint { get; set; }

    /// <summary>Path of the final user-facing ISO output, if the build completed.</summary>
    public string? FinalOutputPath { get; set; }

    /// <summary>True when recovery is required before any further use (failed mount etc.).</summary>
    public bool RecoveryRequired { get; set; }

    /// <summary>
    /// True when this workspace may be deleted safely (never true while
    /// <see cref="IsMountedKnown"/> or when DISM reports the mount active).
    /// </summary>
    public bool CanDeleteSafely { get; set; }

    /// <summary>WinForge version that created the manifest (helps legacy migration).</summary>
    public string? WinForgeVersion { get; set; }

    /// <summary>Why the workspace is currently retained (or <see cref="WorkspaceRetentionReason.None"/>).</summary>
    public WorkspaceRetentionReason RetentionReason { get; set; } = WorkspaceRetentionReason.None;

    /// <summary>Exact leftover path of a failed cleanup, when <see cref="RetentionReason"/> is CleanupFailure.</summary>
    public string? CleanupFailurePath { get; set; }

    /// <summary>Free-form transition log (workspace id, states, timestamps) — lifecycle audit (Part S).</summary>
    public List<WorkspaceTransitionLogEntry> Transitions { get; set; } = new();
}

/// <summary>One lifecycle transition record (Part S telemetry/logging).</summary>
public sealed class WorkspaceTransitionLogEntry
{
    public string Transition { get; set; } = string.Empty; // e.g. Created / Mounted / UnmountCommitted / CleanupCompleted
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public long? BytesReclaimed { get; set; }
}
