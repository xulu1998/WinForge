using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;

namespace WinForge.Core.Services;

/// <summary>
/// Inspects a Windows ISO safely and read-only, returning a structured
/// <see cref="IsoInspectionResult"/>. Implementations must never modify the
/// source ISO, call DISM for servicing, or parse WIM/ESD contents.
/// </summary>
public interface IIsoInspectionService
{
    Task<IsoInspectionResult> InspectAsync(string isoPath, CancellationToken cancellationToken = default);
}
