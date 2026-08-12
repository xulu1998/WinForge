using System;
using System.IO;

namespace WinForge.Core.WorkspaceLifecycle;

/// <summary>
/// Pure validation rules for a workspace root candidate (Part A). IO-free checks
/// are here (non-empty, protected roots); creatable/writable checks are performed
/// by the Infrastructure implementation.
/// </summary>
public static class WorkspaceRootValidator
{
    /// <summary>Default root under the user's local app data.</summary>
    public static string DefaultRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinForge",
            "Workspaces");

    /// <summary>
    /// True when the candidate is an acceptable root (non-empty, not a drive root,
    /// not a user-profile root). Does NOT verify existence/writability.
    /// </summary>
    public static bool IsAcceptablePath(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var full = Path.GetFullPath(candidate).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Length <= 3 && (full.EndsWith(":") || full.EndsWith(":\\")))
        {
            return false; // drive root
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile) &&
            string.Equals(full, Path.GetFullPath(profile).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            return false; // user profile root
        }

        return true;
    }
}
