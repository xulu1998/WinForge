using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;

namespace WinForge.Core.Services;

/// <summary>
/// Phase 10 — Build / ISO Export orchestrator (Core contract). It drives the full
/// pipeline (preflight → commit → export → prepare media → build ISO → verify →
/// report) behind documented, fakeable sub-services, so a real ISO is never
/// required for the bulk of the automated tests. The original source ISO and
/// working image are never modified; only an isolated build workspace and the
/// user-chosen output file are written.
///
/// <para>Guarantees:</para>
/// <list type="bullet">
///   <item><description>Commit uses DISM <c>/Unmount-Image /Commit</c> — never <c>/Discard</c>.</description></item>
///   <item><description>If commit fails, no ISO build begins and the workspace stays recoverable.</description></item>
///   <item><description>The final .iso is written to a <c>.partial</c> file and only renamed to the final path after verification succeeds.</description></item>
///   <item><description>On failure or cancellation, partial output is cleaned where safe and success is never reported.</description></item>
/// </list>
/// </summary>
public interface IBuildService
{
    /// <summary>
    /// Runs the full build pipeline, reporting progress (phase + message + coarse
    /// percent) through <paramref name="progress"/>. Honors
    /// <paramref name="cancellationToken"/> so the build can be cancelled without
    /// leaving a mounted WIM or a half-written ISO reported as final.
    /// </summary>
    Task<BuildResult> BuildAsync(
        BuildRequest request,
        IProgress<BuildProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects an interrupted build by inspecting the WinForge-owned build
    /// workspace for leftover temp artifacts and any persisted recovery snapshot.
    /// Returns null when no interrupted build is found. Does not delete anything.
    /// </summary>
    Task<BuildRecoveryState?> DetectInterruptedBuildAsync(
        string buildWorkspaceDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Safely removes a leftover interrupted-build workspace (temp artifacts only).
    /// Returns true when the directory no longer exists afterward.
    /// </summary>
    Task<bool> CleanupInterruptedBuildAsync(
        string buildWorkspaceDirectory,
        CancellationToken cancellationToken = default);
}
