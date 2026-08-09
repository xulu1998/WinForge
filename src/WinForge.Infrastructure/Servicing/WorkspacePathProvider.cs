using System;
using System.IO;
using System.Text.RegularExpressions;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Servicing;

/// <summary>
/// WinForge-owned workspace path policy. By default all servicing workspaces live
/// under <c>%LOCALAPPDATA%\WinForge\Workspaces\&lt;id&gt;</c> with separate
/// <c>image\</c> and <c>mount\</c> areas. A caller (or a test) can override the
/// root so nothing is ever written to the real application data directory during
/// automated runs. The workspace id is validated as a single path segment so it
/// cannot contain separators that would escape the workspace folder.
/// </summary>
public sealed class WorkspacePathProvider : IWorkspacePathProvider
{
    private static readonly Regex SafeIdRegex = new(@"^[A-Za-z0-9_\-]{1,120}$", RegexOptions.Compiled);

    private readonly string _root;

    public WorkspacePathProvider(string? rootOverride = null)
    {
        _root = string.IsNullOrWhiteSpace(rootOverride)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinForge",
                "Workspaces")
            : rootOverride!;
    }

    public string RootDirectory => _root;

    public string GetOrCreateWorkspaceDirectory(string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(workspaceId);
        if (!SafeIdRegex.IsMatch(workspaceId))
        {
            throw new ArgumentException(
                "Workspace id must be a single safe path segment (alphanumeric, _ or -).", nameof(workspaceId));
        }

        var dir = Path.Combine(_root, workspaceId);
        Directory.CreateDirectory(Path.Combine(dir, "image"));
        Directory.CreateDirectory(Path.Combine(dir, "mount"));
        return dir;
    }

    public string GetWorkingImagePath(string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(workspaceId);
        if (!SafeIdRegex.IsMatch(workspaceId))
        {
            throw new ArgumentException(
                "Workspace id must be a single safe path segment (alphanumeric, _ or -).", nameof(workspaceId));
        }

        return Path.Combine(_root, workspaceId, "image", "install.wim");
    }

    public string GetMountDirectory(string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(workspaceId);
        if (!SafeIdRegex.IsMatch(workspaceId))
        {
            throw new ArgumentException(
                "Workspace id must be a single safe path segment (alphanumeric, _ or -).", nameof(workspaceId));
        }

        return Path.Combine(_root, workspaceId, "mount");
    }
}
