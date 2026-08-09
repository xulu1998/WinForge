using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;

namespace WinForge.Core.Services;

/// <summary>
/// Phase 3 Step 3.2 — offline WIM servicing lifecycle. This contract is the ONLY
/// servicing surface for this step; it prepares an isolated working image, mounts
/// it read/write for later phases, discards an unmount, and validates/recovers a
/// session. It never performs customization (package/component/Appx/registry
/// tweaks), builds an ISO, or services boot.wim. Core declares the contract; the
/// Windows DISM implementation lives in Infrastructure.
/// </summary>
public interface IImageServicingService
{
    /// <summary>
    /// Creates a durable servicing session and exports ONLY the selected source
    /// index into a new standalone working WIM (index 1). The source install
    /// image (WIM or ESD) is never modified. On success the session is
    /// <see cref="ServicingWorkspaceState.Prepared"/> and the produced working
    /// image has been verified (exists, single expected index, edition/build
    /// consistent). On failure the session is <see cref="ServicingWorkspaceState.Failed"/>
    /// and any partial disposable output is cleaned.
    /// </summary>
    Task<ServicingResult> PrepareWorkingImageAsync(
        ImageWorkspace source,
        string workspaceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mounts the prepared working image (working index 1) at the session's
    /// dedicated, empty mount directory. The source image is never mounted. On
    /// success the session is <see cref="ServicingWorkspaceState.Mounted"/> and the
    /// mount is verified to be registered; on failure the session is Failed and any
    /// partially-created mount state is cleared.
    /// </summary>
    Task<ServicingResult> MountAsync(
        ImageServicingWorkspace workspace,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unmounts the working image and DISCARDS any changes (no commit). After the
    /// mount is verified gone the session returns to
    /// <see cref="ServicingWorkspaceState.Prepared"/> (or Completed) so it can be
    /// mounted again; the working WIM is retained. A repeated unmount on an
    /// already-unmounted session is a safe no-op.
    /// </summary>
    Task<ServicingResult> UnmountDiscardAsync(
        ImageServicingWorkspace workspace,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates an existing servicing workspace against reality (files on disk,
    /// mount registration) and classifies its health: Ready, Prepared, Mounted,
    /// Stale, Invalid, or Failed. Used for crash recovery and for detecting
    /// sessions whose DISM state disagrees with the stored <see cref="ImageServicingWorkspace.State"/>.
    /// </summary>
    Task<ServicingResult> ValidateServicingWorkspaceAsync(
        ImageServicingWorkspace workspace,
        CancellationToken cancellationToken = default);
}
