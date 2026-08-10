namespace WinForge.Core.Models;

/// <summary>
/// Request to build a bootable ISO from a prepared media tree. The boot files are
/// supplied as absolute paths inside the media tree so the backend can construct
/// the correct dual BIOS/UEFI oscdimg command.
/// </summary>
public sealed class IsoBuildRequest
{
    /// <summary>Root of the prepared media tree.</summary>
    public string MediaRoot { get; init; } = string.Empty;

    /// <summary>Final (post-verification) output .iso path.</summary>
    public string OutputIsoPath { get; init; } = string.Empty;

    /// <summary>Absolute path of <c>boot\etfsboot.com</c> within the media tree (BIOS boot).</summary>
    public string BootFileEtfs { get; init; } = string.Empty;

    /// <summary>Absolute path of <c>efi\microsoft\boot\efisys.bin</c> within the media tree (UEFI boot).</summary>
    public string BootFileEfisys { get; init; } = string.Empty;

    /// <summary>Optional ISO volume label.</summary>
    public string? VolumeLabel { get; init; }
}

/// <summary>
/// Outcome of <see cref="IBootableIsoBuilder.BuildAsync"/>. <see cref="ToolMissing"/>
/// distinguishes "oscdimg.exe could not be located" (a product error, not a tool
/// failure) so the UI can show the precise, friendly message required by the spec.
/// </summary>
public sealed class IsoBuildResult
{
    public bool Success { get; init; }

    /// <summary>True when the backend tool (oscdimg.exe) was not found at all.</summary>
    public bool ToolMissing { get; init; }

    public string? OutputPath { get; init; }
    public int ExitCode { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>Normalized stdout of the tool (never raw CLIXML-only).</summary>
    public string? StandardOutput { get; init; }

    /// <summary>Normalized stderr of the tool.</summary>
    public string? StandardError { get; init; }

    public static IsoBuildResult Ok(string outputPath)
        => new() { Success = true, OutputPath = outputPath, ExitCode = 0 };

    public static IsoBuildResult ToolNotFound()
        => new() { Success = false, ToolMissing = true, ExitCode = -1, ErrorMessage = "oscdimg.exe not found." };

    public static IsoBuildResult Fail(string error, int exitCode, string? stdout = null, string? stderr = null)
        => new()
        {
            Success = false,
            ExitCode = exitCode,
            ErrorMessage = error,
            StandardOutput = stdout,
            StandardError = stderr
        };
}
