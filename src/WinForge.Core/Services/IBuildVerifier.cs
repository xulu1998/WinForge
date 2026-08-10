using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;

namespace WinForge.Core.Services;

/// <summary>
/// Independently verifies a produced ISO and its media tree. The build pipeline
/// must not treat a successful tool exit code as success: this verifier confirms
/// the output exists and has size, the final install.wim is present and
/// queryable with the expected edition/index, and no WIM remains mounted.
/// </summary>
public interface IBuildVerifier
{
    /// <summary>
    /// Verifies the produced ISO. <see cref="BuildVerificationResult.Success"/>
    /// requires every critical check to pass; each flag is reported independently.
    /// </summary>
    Task<BuildVerificationResult> VerifyAsync(BuildVerificationRequest request, CancellationToken cancellationToken = default);
}
