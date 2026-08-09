using WinForge.Core.Models;

namespace WinForge.Core.Services;

/// <summary>
/// Phase 3 core contract for WIM/ESD handling. Step 3.1 introduces it read-only:
/// it validates a durable <see cref="ImageWorkspace"/> and resolves a
/// <see cref="SelectedImageContext"/> from it. No image is mounted, exported,
/// applied, captured, or modified at this stage.
///
/// Later steps will extend this interface (e.g. ESD → WIM export in Step 3.2);
/// only read-only responsibilities appropriate to Step 3.1 are declared now.
/// </summary>
public interface IWimService
{
    /// <summary>
    /// Re-validates a durable workspace's essential identifiers (ISO path,
    /// relative image path, image type, selected index). Returns the structured
    /// <see cref="ImageWorkspaceStatus"/> rather than raising for invalid input.
    /// </summary>
    ImageWorkspaceStatus ValidateWorkspace(ImageWorkspace workspace);

    /// <summary>
    /// Resolves a minimal <see cref="SelectedImageContext"/> for a ready workspace.
    /// Returns null when the workspace is not ready, so callers cannot obtain a
    /// context for an invalid descriptor. Does not perform any I/O.
    /// </summary>
    SelectedImageContext? ResolveSelectedImage(ImageWorkspace workspace);
}
