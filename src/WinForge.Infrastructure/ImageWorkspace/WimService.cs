using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.WimEngine;

/// <summary>
/// Read-only Phase 3 <see cref="IWimService"/> implementation for Step 3.1. It
/// validates a durable <see cref="ImageWorkspace"/> and resolves a
/// <see cref="SelectedImageContext"/> from it. No DISM export/mount/apply/capture
/// is performed and no image is modified — those belong to later Phase 3/4 steps.
/// </summary>
public sealed class WimService : IWimService
{
    public ImageWorkspaceStatus ValidateWorkspace(ImageWorkspace workspace)
    {
        if (workspace is null)
        {
            return ImageWorkspaceStatus.Invalid;
        }

        if (string.IsNullOrWhiteSpace(workspace.SourceIsoPath))
        {
            return ImageWorkspaceStatus.Invalid;
        }

        if (string.IsNullOrWhiteSpace(workspace.ImageRelativePath) ||
            !workspace.ImageRelativePath.StartsWith("sources", System.StringComparison.OrdinalIgnoreCase))
        {
            return ImageWorkspaceStatus.Invalid;
        }

        if (workspace.ImageType == WindowsImageType.Unknown)
        {
            return ImageWorkspaceStatus.Invalid;
        }

        // A non-positive selected index means the edition selection is missing;
        // the durable source is known but the descriptor is not yet targetable.
        if (workspace.SelectedIndex <= 0)
        {
            return ImageWorkspaceStatus.NotReady;
        }

        return ImageWorkspaceStatus.Ready;
    }

    public SelectedImageContext? ResolveSelectedImage(ImageWorkspace workspace)
    {
        if (workspace is null || ValidateWorkspace(workspace) != ImageWorkspaceStatus.Ready)
        {
            return null;
        }

        return new SelectedImageContext(
            workspace.SourceIsoPath!,
            workspace.ImageRelativePath!,
            workspace.ImageType,
            workspace.SelectedIndex);
    }
}
