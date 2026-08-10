using System;

namespace WinForge.Core.Models;

/// <summary>
/// Persisted snapshot of an in-flight build, written by the pipeline into the
/// WinForge-owned build workspace so the application can detect an interrupted
/// build on restart. It is deleted on a clean success / failure / cancel. If it
/// is found at startup, the build was interrupted and recovery (safe cleanup) can
/// be offered without silently deleting user data.
/// </summary>
public sealed class BuildRecoveryState
{
    /// <summary>Last known build state when the snapshot was written.</summary>
    public BuildState State { get; set; } = BuildState.NotStarted;

    /// <summary>Final output path the build was targeting.</summary>
    public string? OutputPath { get; set; }

    /// <summary>WinForge-owned build workspace directory for temp artifacts.</summary>
    public string? WorkspaceDirectory { get; set; }

    /// <summary>Original source ISO path.</summary>
    public string? SourceIsoPath { get; set; }

    /// <summary>UTC timestamp the build started.</summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>True when a <c>.partial</c> output file was on disk at snapshot time.</summary>
    public bool PartialOutputPresent { get; set; }

    /// <summary>
    /// True when the snapshot was written for a non-terminal phase (Preflight …
    /// Verifying) and no clean terminal state followed — i.e. the build was
    /// interrupted and should be offered for recovery.
    /// </summary>
    public bool IsInterrupted => State is BuildState.Preflight or BuildState.CommittingImage
        or BuildState.ExportingImage or BuildState.PreparingMedia or BuildState.BuildingIso or BuildState.Verifying;
}
