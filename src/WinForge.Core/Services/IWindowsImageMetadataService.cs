using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;

namespace WinForge.Core.Services;

/// <summary>
/// Reads read-only metadata from a Windows image (install.wim / install.esd) such
/// as its indexes, editions, architecture, version, and languages. Implementations
/// must never mount, modify, or service the image — only query it. Environmental
/// failures (missing tooling, corrupt image, non-zero exit) surface as a
/// <see cref="WindowsImageMetadataResult"/> with <see cref="WindowsImageMetadataStatus.Failed"/>;
/// only cancellation propagates as <see cref="OperationCanceledException"/>.
/// </summary>
public interface IWindowsImageMetadataService
{
    Task<WindowsImageMetadataResult> InspectAsync(string imagePath, CancellationToken cancellationToken = default);
}
