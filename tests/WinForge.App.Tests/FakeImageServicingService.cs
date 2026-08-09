using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.App.Tests;

/// <summary>
/// No-op <see cref="IImageServicingService"/> for ViewModel tests that only
/// exercise ISO inspection / edition selection and do not drive the servicing
/// lifecycle. Every operation reports success without touching DISM or the disk.
/// </summary>
internal sealed class FakeImageServicingService : IImageServicingService
{
    public Task<ServicingResult> PrepareWorkingImageAsync(
        ImageWorkspace source, string workspaceId, CancellationToken cancellationToken = default)
        => Task.FromResult(ServicingResult.Ok(new ImageServicingWorkspace(), ServicingHealth.Prepared));

    public Task<ServicingResult> MountAsync(
        ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
        => Task.FromResult(ServicingResult.Ok(workspace, ServicingHealth.Mounted));

    public Task<ServicingResult> UnmountDiscardAsync(
        ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
        => Task.FromResult(ServicingResult.Ok(workspace, ServicingHealth.Prepared));

    public Task<ServicingResult> ValidateServicingWorkspaceAsync(
        ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
        => Task.FromResult(ServicingResult.Ok(workspace, ServicingHealth.Prepared));
}
