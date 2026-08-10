using System.Collections.Generic;

namespace WinForge.Core.Models;

/// <summary>
/// The structured outcome of a Build / ISO export. It never raises for expected
/// failures: instead it reports <see cref="Success"/>, the terminal
/// <see cref="FinalState"/>, the produced <see cref="OutputPath"/> and size, and a
/// high-level <see cref="ErrorMessage"/> plus the full <see cref="Log"/> so the UI
/// can present them without parsing raw tool output. When <see cref="Success"/>
/// is false, the build MUST NOT have reported success at any earlier stage — the
/// terminal state is <see cref="BuildState.Failed"/> or
/// <see cref="BuildState.Cancelled"/>, never <see cref="BuildState.Completed"/>.
/// </summary>
public sealed class BuildResult
{
    /// <summary>True only when the ISO was produced, verified, and moved to the final path.</summary>
    public bool Success { get; init; }

    /// <summary>Terminal build state. Equals <see cref="BuildState.Completed"/> iff <see cref="Success"/>.</summary>
    public BuildState FinalState { get; init; }

    /// <summary>Absolute path of the final .iso (only meaningful when <see cref="Success"/>).</summary>
    public string? OutputPath { get; init; }

    /// <summary>Size of the final .iso in bytes (0 when not produced).</summary>
    public long OutputSizeBytes { get; init; }

    /// <summary>Short high-level error description when the build failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The phase at which the build failed or was cancelled (null on success).</summary>
    public BuildState? FailedPhase { get; init; }

    /// <summary>Exit code of the failing external tool, when applicable.</summary>
    public int? ToolExitCode { get; init; }

    /// <summary>High-level issues / notes; never raw command output.</summary>
    public IReadOnlyList<string> Issues { get; init; } = System.Array.Empty<string>();

    /// <summary>Ordered, high-level build log (English). Never raw CLIXML.</summary>
    public IReadOnlyList<string> Log { get; init; } = System.Array.Empty<string>();

    public static BuildResult Ok(string outputPath, long size, IReadOnlyList<string> log)
        => new()
        {
            Success = true,
            FinalState = BuildState.Completed,
            OutputPath = outputPath,
            OutputSizeBytes = size,
            Log = log
        };

    public static BuildResult Fail(
        BuildState failedPhase,
        string error,
        IReadOnlyList<string> log,
        int? exitCode = null,
        BuildState? finalState = null,
        IReadOnlyList<string>? issues = null)
        => new()
        {
            Success = false,
            FinalState = finalState ?? BuildState.Failed,
            FailedPhase = failedPhase,
            ErrorMessage = error,
            ToolExitCode = exitCode,
            Log = log,
            Issues = issues ?? System.Array.Empty<string>()
        };
}
