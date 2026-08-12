using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WinForge.Core.WorkspaceLifecycle;

/// <summary>
/// Result of one workspace classification pass (Part F/B). <see cref="MountedPaths"/>
/// is the live DISM mount registration (authoritative); a workspace whose mount
/// path appears in it is NEVER a cleanup candidate.
/// </summary>
public sealed class WorkspaceClassificationResult
{
    public string WorkspaceId { get; init; } = string.Empty;
    public string WorkspaceDirectory { get; init; } = string.Empty;
    public WorkspaceClassification Classification { get; init; } = WorkspaceClassification.Unknown;
    public WorkspaceLifecycleState? ManifestState { get; init; }
    public WorkspaceRetentionReason RetentionReason { get; init; } = WorkspaceRetentionReason.None;
    public string? ManifestPath { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Result of a live DISM mount-state query. Query failure must FAIL CLOSED
/// (<see cref="QuerySucceeded"/> false → no deletion decisions may be made).
/// </summary>
public sealed class MountStateQueryResult
{
    public bool QuerySucceeded { get; init; }
    public IReadOnlyCollection<string> MountedPaths { get; init; } = new List<string>();
    public string? Error { get; init; }
}

/// <summary>
/// The workspace lifecycle engine (Phase 12, Parts A–G). Creates and persists
/// workspace manifests, transitions lifecycle state, queries the ACTUAL DISM
/// mount registration, classifies workspaces at startup, and computes safe
/// cleanup candidates. Platform-agnostic contract; the DISM-backed
/// implementation lives in Infrastructure.
/// </summary>
public interface IWorkspaceLifecycleManager
{
    /// <summary>Root directory that holds the wf-* workspaces.</summary>
    string WorkspaceRoot { get; }

    /// <summary>
    /// Creates the workspace directory + manifest and records the Created
    /// transition. Returns the workspace directory path.
    /// </summary>
    string CreateWorkspace(string workspaceId, string? sourceIsoPath);

    /// <summary>Loads a manifest if present; null for legacy/absent.</summary>
    WorkspaceManifest? TryLoadManifest(string workspaceId);

    /// <summary>Transitions state, persists the manifest, and logs the transition.</summary>
    void Transition(string workspaceId, WorkspaceLifecycleState newState, string transitionName,
        long? bytesReclaimed = null);

    /// <summary>Updates manifest fields in place (paths, checkpoint/output, recovery).</summary>
    void UpdateManifest(string workspaceId, Action<WorkspaceManifest> mutate);

    /// <summary>
    /// Live DISM mount-state query — the AUTHORITATIVE guard. Failures fail closed.
    /// </summary>
    Task<MountStateQueryResult> QueryMountedStateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Classifies every wf-* workspace under the root (Part F). Unknown/Broken
    /// workspaces are never cleanup candidates. Legacy (no manifest) workspaces
    /// are classified from the DISM mount check only.
    /// </summary>
    Task<IReadOnlyList<WorkspaceClassificationResult>> ClassifyAllAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Workspaces that are safe automatic-cleanup candidates under Part G:
    /// terminal disposable states, successfully discarded, completed (output
    /// preserved elsewhere), stale staging — excluding every active mount and
    /// every recoverable checkpoint unless explicitly released.
    /// </summary>
    Task<IReadOnlyList<WorkspaceClassificationResult>> GetCleanupCandidatesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a workspace directory safely (attributes, partial-failure recording).</summary>
    Task<CleanupResult> CleanupWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default);

    /// <summary>Asynchronously measures a directory's total size (cancellable).</summary>
    Task<long> MeasureDirectorySizeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stage 12.2 Finish cleanup (Part C): safely cleans a COMPLETED workspace
    /// (authoritative DISM mount check first). Recoverable checkpoints and active
    /// mounts are retained with their size; everything disposable is deleted.
    /// </summary>
    Task<CompletedWorkspaceCleanupResult> CleanupCompletedWorkspaceAsync(
        string workspaceId, CancellationToken cancellationToken = default);
}

/// <summary>Result of the Finish-triggered workspace cleanup (Part C/D).</summary>
public sealed class CompletedWorkspaceCleanupResult
{
    public bool Cleaned { get; init; }
    public long BytesReclaimed { get; init; }
    public long BytesRetained { get; init; }
    public WorkspaceRetentionReason RetentionReason { get; init; } = WorkspaceRetentionReason.None;
    public string? Error { get; init; }
}

/// <summary>Result of a cleanup attempt (Part O — never claim success on partial failure).</summary>
public sealed class CleanupResult
{
    public bool Succeeded { get; init; }
    public long BytesReclaimed { get; init; }
    public string? LeftoverPath { get; init; }
    public string? Error { get; init; }
}
