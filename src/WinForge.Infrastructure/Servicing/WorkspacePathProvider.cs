using System;
using System.IO;
using System.Text.RegularExpressions;
using WinForge.Core.Services;
using WinForge.Core.WorkspaceLifecycle;

namespace WinForge.Infrastructure.Servicing;

/// <summary>
/// WinForge-owned workspace path policy. The authoritative creation root is the
/// CURRENT workspace root from <see cref="IWorkspaceRootSettingsService"/> when
/// available (Stage 12.7 — real-desktop blocker: the servicing service used a
/// standalone default root while the lifecycle manifest used the configured
/// root, producing SPLIT workspaces: real data under the old C: default and a
/// manifest-only shell under the configured root, so Finish cleaned the shell
/// and leaked the data). A fixed <paramref name="rootOverride"/> (tests /
/// legacy callers) always wins over the settings service. KnownRoots are NEVER
/// a creation destination — only the current root creates new workspace ids.
/// </summary>
public sealed class WorkspacePathProvider : IWorkspacePathProvider
{
    private static readonly Regex SafeIdRegex = new(@"^[A-Za-z0-9_\-]{1,120}$", RegexOptions.Compiled);

    private readonly string? _fixedRoot;
    private readonly IWorkspaceRootSettingsService? _rootSettings;

    public WorkspacePathProvider(string? rootOverride = null, IWorkspaceRootSettingsService? rootSettings = null)
    {
        _fixedRoot = string.IsNullOrWhiteSpace(rootOverride) ? null : rootOverride;
        _rootSettings = rootSettings;
    }

    /// <summary>
    /// The root under which NEW workspace ids are created. Resolution order:
    /// fixed override (tests) → configured current root (settings service,
    /// re-evaluated on every access so a root change affects all services
    /// immediately) → platform default. Never KnownRoots.
    /// </summary>
    public string RootDirectory =>
        _fixedRoot
        ?? _rootSettings?.CurrentRoot
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinForge",
            "Workspaces");

    public string GetOrCreateWorkspaceDirectory(string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(workspaceId);
        if (!SafeIdRegex.IsMatch(workspaceId))
        {
            throw new ArgumentException(
                "Workspace id must be a single safe path segment (alphanumeric, _ or -).", nameof(workspaceId));
        }

        var dir = Path.Combine(RootDirectory, workspaceId);
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

        return Path.Combine(RootDirectory, workspaceId, "image", "install.wim");
    }

    public string GetMountDirectory(string workspaceId)
    {
        ArgumentNullException.ThrowIfNull(workspaceId);
        if (!SafeIdRegex.IsMatch(workspaceId))
        {
            throw new ArgumentException(
                "Workspace id must be a single safe path segment (alphanumeric, _ or -).", nameof(workspaceId));
        }

        return Path.Combine(RootDirectory, workspaceId, "mount");
    }
}
