using System.Collections.Generic;

namespace WinForge.Core.Models;

/// <summary>
/// Outcome of attempting to build a durable <see cref="ImageWorkspace"/> from a
/// Phase 2 inspection plus a selected edition. It keeps the structured readiness
/// state (<see cref="ImageWorkspaceStatus"/>) and any validation issues so the
/// UI and callers compare the enum instead of parsing strings, and can inspect
/// why a build was rejected.
/// </summary>
public sealed class ImageWorkspaceBuildResult
{
    public ImageWorkspaceBuildResult(ImageWorkspace? workspace, ImageWorkspaceStatus status, IReadOnlyList<string> issues)
    {
        Workspace = workspace;
        Status = status;
        Issues = issues ?? System.Array.Empty<string>();
    }

    /// <summary>The built workspace, or null when it could not be made ready/invalid.</summary>
    public ImageWorkspace? Workspace { get; }

    /// <summary>Readiness of the build attempt.</summary>
    public ImageWorkspaceStatus Status { get; }

    /// <summary>Human-readable validation issues recorded while building (empty when ready).</summary>
    public IReadOnlyList<string> Issues { get; }

    /// <summary>True when <see cref="Status"/> is <see cref="ImageWorkspaceStatus.Ready"/>.</summary>
    public bool IsReady => Status == ImageWorkspaceStatus.Ready;
}
