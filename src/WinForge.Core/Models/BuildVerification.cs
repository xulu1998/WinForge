namespace WinForge.Core.Models;

/// <summary>
/// Request to verify a produced ISO + its media tree. Verification never trusts
/// the tool exit code alone: it independently confirms the output exists, has
/// size, the final install.wim is present and queryable, the expected edition is
/// present at the expected index, and no WIM remains mounted.
/// </summary>
public sealed class BuildVerificationRequest
{
    /// <summary>Path to the produced (post-build, pre-rename) .iso.</summary>
    public string OutputIsoPath { get; init; } = string.Empty;

    /// <summary>Path to the final install.wim inside the media tree (pre-ISO).</summary>
    public string ExpectedInstallWimPath { get; init; } = string.Empty;

    /// <summary>Expected edition name to confirm in the final WIM (optional).</summary>
    public string? ExpectedEditionName { get; init; }

    /// <summary>Expected 1-based index of the customized edition in the final WIM.</summary>
    public int ExpectedIndex { get; init; } = 1;
}

/// <summary>
/// Outcome of <see cref="IBuildVerifier.VerifyAsync"/>. Each flag is checked
/// independently; <see cref="Success"/> requires every critical check to pass.
/// </summary>
public sealed class BuildVerificationResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>The produced .iso file exists.</summary>
    public bool OutputExists { get; init; }

    /// <summary>Size in bytes of the produced .iso (0 when absent).</summary>
    public long OutputSize { get; init; }

    /// <summary>The final install.wim is present in the media tree.</summary>
    public bool InstallWimPresent { get; init; }

    /// <summary>A WIM image is still mounted (must be false).</summary>
    public bool MountedImagesPresent { get; init; }

    /// <summary>The expected edition/index is present in the final WIM.</summary>
    public bool EditionPresent { get; init; }

    public static BuildVerificationResult Pass(long outputSize, bool installWimPresent, bool editionPresent)
        => new()
        {
            Success = true,
            OutputExists = true,
            OutputSize = outputSize,
            InstallWimPresent = installWimPresent,
            MountedImagesPresent = false,
            EditionPresent = editionPresent
        };

    public static BuildVerificationResult Fail(string error, bool outputExists = false, long outputSize = 0,
        bool installWimPresent = false, bool mountedImagesPresent = false, bool editionPresent = false)
        => new()
        {
            Success = false,
            ErrorMessage = error,
            OutputExists = outputExists,
            OutputSize = outputSize,
            InstallWimPresent = installWimPresent,
            MountedImagesPresent = mountedImagesPresent,
            EditionPresent = editionPresent
        };
}
