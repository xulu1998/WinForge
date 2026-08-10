namespace WinForge.Core.Models;

/// <summary>
/// Request to export a single index from a (committed) working WIM into a clean
/// destination WIM. Mirrors the DISM <c>/Export-Image</c> operation but is
/// expressed abstractly so it is fully testable behind <see cref="IWimExporter"/>.
/// </summary>
public sealed class WimExportRequest
{
    /// <summary>Source working WIM (post-commit) to export from.</summary>
    public string SourceImagePath { get; init; } = string.Empty;

    /// <summary>1-based index inside the source to export (the customized edition).</summary>
    public int SourceIndex { get; init; } = 1;

    /// <summary>Destination WIM to create (the final install.wim payload).</summary>
    public string DestinationImagePath { get; init; } = string.Empty;
}

/// <summary>
/// Outcome of <see cref="IWimExporter.ExportAsync"/>. On success the destination
/// WIM contains exactly the exported single index and <see cref="ExportedIndex"/>
/// is the working index (1) of that clean image.
/// </summary>
public sealed class WimExportResult
{
    public bool Success { get; init; }
    public string? DestinationPath { get; init; }
    public int ExportedIndex { get; init; }
    public string? ErrorMessage { get; init; }
    public int ExitCode { get; init; }

    public static WimExportResult Ok(string destinationPath, int index)
        => new() { Success = true, DestinationPath = destinationPath, ExportedIndex = index, ExitCode = 0 };

    public static WimExportResult Fail(string error, int exitCode)
        => new() { Success = false, ErrorMessage = error, ExitCode = exitCode };
}
