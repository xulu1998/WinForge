using WinForge.Core.Models;

namespace WinForge.Core.Services;

/// <summary>
/// Converts a Phase 2 <see cref="IsoInspectionResult"/> plus the user's selected
/// <see cref="WindowsEditionInfo"/> into a durable <see cref="ImageWorkspace"/>
/// descriptor. The result is independent of any temporary mounted drive letter:
/// it references the original ISO path and the image's relative path inside the
/// ISO. This is pure, read-only construction — it never mounts, exports, or
/// modifies an image.
/// </summary>
public interface IImageWorkspaceFactory
{
    /// <summary>
    /// Builds a workspace descriptor. Returns <see cref="ImageWorkspaceStatus.Ready"/>
    /// with a populated <see cref="ImageWorkspaceBuildResult.Workspace"/> only when
    /// every essential durable identifier exists and the selected edition belongs
    /// to the inspected image; otherwise <see cref="ImageWorkspaceStatus.NotReady"/>
    /// or <see cref="ImageWorkspaceStatus.Invalid"/> with the workspace null.
    /// </summary>
    ImageWorkspaceBuildResult BuildWorkspace(IsoInspectionResult inspection, WindowsEditionInfo? selectedEdition);
}
