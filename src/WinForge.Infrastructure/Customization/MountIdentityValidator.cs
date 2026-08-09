using System;
using System.IO;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Customization;

/// <summary>
/// Enforces that every modification targets ONLY the active mounted WinForge
/// working image and never the host OS, the original ISO mount root, or any path
/// outside the workspace (Step 3.3 section S). Used by the discovery and
/// execution engines before each destructive group.
/// </summary>
public sealed class MountIdentityValidator : IMountIdentityValidator
{
    public bool IsWithinMount(string path, ImageServicingWorkspace workspace)
    {
        if (string.IsNullOrWhiteSpace(path) || workspace?.MountDirectory is null)
        {
            return false;
        }

        var mount = Path.GetFullPath(workspace.MountDirectory!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(path);

        if (full.Length <= mount.Length)
        {
            return false;
        }

        var prefix = mount + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public bool MatchesSession(ImageServicingWorkspace workspace)
    {
        if (workspace is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(workspace.MountDirectory) ||
            string.IsNullOrWhiteSpace(workspace.WorkingDirectory) ||
            string.IsNullOrWhiteSpace(workspace.WorkingImagePath))
        {
            return false;
        }

        // The mount directory and the working image must both live under the
        // workspace directory — proving this session owns them and they are not a
        // host path, a drive root, or the original ISO mount root.
        var root = Path.GetFullPath(workspace.WorkingDirectory!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var mount = Path.GetFullPath(workspace.MountDirectory!);
        var image = Path.GetFullPath(workspace.WorkingImagePath!);

        if (mount.Length <= root.Length + 1 || !mount.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (image.Length <= root.Length + 1 || !image.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
