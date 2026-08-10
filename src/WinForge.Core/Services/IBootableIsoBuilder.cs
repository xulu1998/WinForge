using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;

namespace WinForge.Core.Services;

/// <summary>
/// Builds a bootable ISO from a prepared media tree. The contract is backend
/// agnostic: <see cref="OscdimgIsoBuilder"/> is the Windows ADK implementation,
/// but an alternate backend can be added later without touching the pipeline. If
/// the backend tool is unavailable the result sets
/// <see cref="IsoBuildResult.ToolMissing"/> (a product error, not a tool failure).
/// </summary>
public interface IBootableIsoBuilder
{
    /// <summary>
    /// Builds the ISO described by <paramref name="request"/>. The caller is
    /// responsible for the partial-output / verification / rename protocol; this
    /// builder simply produces the ISO at
    /// <see cref="IsoBuildRequest.OutputIsoPath"/>.
    /// </summary>
    Task<IsoBuildResult> BuildAsync(IsoBuildRequest request, CancellationToken cancellationToken = default);
}
