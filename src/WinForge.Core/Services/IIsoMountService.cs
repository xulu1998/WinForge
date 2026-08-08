using System.Threading;
using System.Threading.Tasks;

namespace WinForge.Core.Services;

/// <summary>
/// Abstraction over mounting a disk image (e.g. an ISO) for read-only
/// inspection. The platform-specific implementation (Windows) lives in
/// Infrastructure; tests provide a fake so they never require a real ISO or
/// PowerShell.
/// </summary>
public interface IIsoMountService
{
    /// <summary>
    /// Mounts <paramref name="isoPath"/> read-only and returns the mounted root
    /// path (e.g. <c>E:\</c>). The caller is responsible for dismounting via
    /// <see cref="DismountAsync"/>, typically from a finally block, so the mount
    /// is always released.
    /// </summary>
    Task<string> MountReadOnlyAsync(string isoPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dismounts the image previously mounted from <paramref name="isoPath"/>.
    /// Must be safe to call even if mounting failed partway.
    /// </summary>
    Task DismountAsync(string isoPath, CancellationToken cancellationToken = default);
}
