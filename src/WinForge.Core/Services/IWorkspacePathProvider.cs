using System;

namespace WinForge.Core.Services;

/// <summary>
/// Abstracts the location of WinForge-owned servicing workspaces so the rest of
/// the application (and tests) never hard-codes <c>%LOCALAPPDATA%</c> or scatters
/// directories. A workspace is addressed by an opaque id and always contains a
/// known sub-layout (image\, mount\, and optionally logs\). Tests can substitute a
/// temporary root directory.
/// </summary>
public interface IWorkspacePathProvider
{
    /// <summary>
    /// The configured WinForge-owned root under which all workspaces live, e.g.
    /// <c>%LOCALAPPDATA%\WinForge\Workspaces</c>. Never a drive root or user profile root.
    /// </summary>
    string RootDirectory { get; }

    /// <summary>
    /// Returns the absolute directory for the given workspace id, creating the
    /// directory (and its <c>image\</c> / <c>mount\</c> sub-directories) if needed.
    /// The id is treated as a single path segment (no separators) so a workspace
    /// can never escape its own folder.
    /// </summary>
    string GetOrCreateWorkspaceDirectory(string workspaceId);

    /// <summary>Path of the working image for the given workspace id.</summary>
    string GetWorkingImagePath(string workspaceId);

    /// <summary>Path of the mount directory for the given workspace id.</summary>
    string GetMountDirectory(string workspaceId);
}

/// <summary>
/// Guards filesystem deletion so servicing cleanup can never recursively remove a
/// drive root, a user profile root, the repository root, or any caller-supplied
/// arbitrary path. Deletion is only permitted inside a verified WinForge-owned
/// workspace directory.
/// </summary>
public interface IWorkspaceSafeDelete
{
    /// <summary>
    /// Returns true when <paramref name="path"/> is safely inside
    /// <paramref name="workspaceRoot"/> and the path is not a protected location
    /// (drive root, profile root, repository root, or the workspace root itself).
    /// </summary>
    bool IsWithinWorkspace(string workspaceRoot, string path);

    /// <summary>
    /// Deletes <paramref name="path"/> only if <see cref="IsWithinWorkspace"/>
    /// confirms it is safe. Returns true when the deletion was performed or the
    /// path did not exist; false when the deletion was refused for safety.
    /// </summary>
    bool TryDeleteWithinWorkspace(string workspaceRoot, string path);
}
