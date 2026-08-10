using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;

namespace WinForge.Core.Services;

/// <summary>
/// Exports a single index from the committed working WIM into a clean final
/// install.wim. Implemented in Infrastructure via DISM <c>/Export-Image</c> so the
/// pipeline never reuses a potentially bloated servicing WIM blindly. Core declares
/// the contract; the DISM implementation lives in Infrastructure and is testable
/// with a fake <see cref="IProcessRunner"/>.
/// </summary>
public interface IWimExporter
{
    /// <summary>
    /// Exports <see cref="WimExportRequest.SourceIndex"/> from the committed
    /// working WIM into a fresh destination WIM. On success the destination WIM
    /// contains exactly the customized edition at <see cref="WimExportResult.ExportedIndex"/>.
    /// </summary>
    Task<WimExportResult> ExportAsync(WimExportRequest request, CancellationToken cancellationToken = default);
}
