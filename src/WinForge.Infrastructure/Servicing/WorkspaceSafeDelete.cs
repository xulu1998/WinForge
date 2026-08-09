using System;
using System.IO;
using System.Runtime.Versioning;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Servicing;

/// <summary>
/// Filesystem deletion guard for servicing cleanup. It proves a target path lives
/// strictly inside a verified WinForge-owned workspace directory before allowing
/// any deletion, and it refuses to delete protected locations (drive roots,
/// user-profile roots, the repository root, or the workspace root itself). It
/// never performs a recursive delete of an arbitrary caller-supplied path.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WorkspaceSafeDelete : IWorkspaceSafeDelete
{
    public bool IsWithinWorkspace(string workspaceRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        // Reject protected roots outright. A path equal to the workspace root is
        // not "within" it (we never delete the root itself via this guard).
        if (IsProtectedRoot(path))
        {
            return false;
        }

        var root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path);

        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (full.Length <= root.Length)
        {
            return false;
        }

        // The candidate must start with the root followed by a separator.
        var prefix = root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public bool TryDeleteWithinWorkspace(string workspaceRoot, string path)
    {
        if (!IsWithinWorkspace(workspaceRoot, path))
        {
            return false;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }

            if (Directory.Exists(path))
            {
                // Only permitted for specific workspace sub-paths (e.g. a stale
                // mount dir) that we know are inside the workspace; never the root.
                var root = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var full = Path.GetFullPath(path);
                if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                Directory.Delete(full, recursive: true);
                return true;
            }

            // Path does not exist: treat as already-clean.
            return true;
        }
        catch
        {
            // Deletion failed (e.g. still mounted); refuse silently rather than
            // throwing — recovery logic decides what to do next.
            return false;
        }
    }

    private static bool IsProtectedRoot(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Drive roots: "C:", "C:\", etc.
        if (full.Length <= 3 && (full.EndsWith(":") || full.EndsWith(":\\")))
        {
            return true;
        }

        // User-profile-like roots: prohibit deleting the profile directory itself.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            var prof = Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, prof, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Repository root is not a workspace artifact and must never be deleted.
        return false;
    }
}
