using System.Collections.Generic;

namespace WinForge.Core.WorkspaceLifecycle;

/// <summary>
/// User-configurable workspace root (Phase 12 Stage 12.2, Part A/G). The current
/// root applies to NEW workflows only; existing workspaces stay in their original
/// root. Every known root (current + previous) is persisted so cleanup/orphan
/// scanning never loses awareness of old workspaces (Part G).
/// </summary>
public interface IWorkspaceRootSettingsService
{
    /// <summary>Currently selected workspace root (defaults to %LOCALAPPDATA%\WinForge\Workspaces).</summary>
    string CurrentRoot { get; }

    /// <summary>All known roots (current + previous), in insertion order, deduplicated.</summary>
    IReadOnlyList<string> KnownRoots { get; }

    /// <summary>
    /// Validates a candidate root WITHOUT changing anything: path non-empty, not a
    /// protected location (drive root / user profile root), creatable and writable,
    /// and (optionally) free-space checked. Returns false + a localized reason key
    /// when the root is unusable.
    /// </summary>
    bool ValidateRoot(string candidate, out string? errorKey);

    /// <summary>
    /// Switches the workspace root for FUTURE workflows and persists it (plus the
    /// previous root into <see cref="KnownRoots"/>). Existing workspaces are never
    /// moved; an actively mounted session is the caller's responsibility to check.
    /// </summary>
    bool SetCurrentRoot(string candidate, out string? errorKey);

    /// <summary>Restores the default root (%LOCALAPPDATA%\WinForge\Workspaces) and persists it.</summary>
    void RestoreDefault();
}
