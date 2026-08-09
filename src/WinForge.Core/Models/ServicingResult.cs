using System.Collections.Generic;

namespace WinForge.Core.Models;

/// <summary>
/// The structured outcome of a servicing lifecycle operation (prepare / mount /
/// unmount). It never raises for expected failures: instead it reports
/// <see cref="Success"/>, the resulting <see cref="Health"/>, and a small set of
/// human-readable <see cref="Issues"/> so the UI can present them without parsing
/// DISM output.
/// </summary>
public sealed class ServicingResult
{
    /// <summary>The workspace after the operation, or null when the operation could not start.</summary>
    public ImageServicingWorkspace? Workspace { get; init; }

    /// <summary>True when the operation completed and the workspace is in a consistent state.</summary>
    public bool Success { get; init; }

    /// <summary>High-level health classification for the resulting workspace.</summary>
    public ServicingHealth Health { get; init; }

    /// <summary>Human-readable issues / notes; never raw command output.</summary>
    public IReadOnlyList<string> Issues { get; init; } = System.Array.Empty<string>();

    /// <summary>Short high-level error description when <see cref="Success"/> is false.</summary>
    public string? ErrorMessage { get; init; }

    public static ServicingResult Ok(ImageServicingWorkspace workspace, ServicingHealth health, IReadOnlyList<string>? issues = null)
        => new() { Workspace = workspace, Success = true, Health = health, Issues = issues ?? System.Array.Empty<string>() };

    public static ServicingResult Fail(ImageServicingWorkspace? workspace, string error, ServicingHealth health, IReadOnlyList<string>? issues = null)
        => new()
        {
            Workspace = workspace,
            Success = false,
            Health = health,
            ErrorMessage = error,
            Issues = issues ?? System.Array.Empty<string>()
        };
}
