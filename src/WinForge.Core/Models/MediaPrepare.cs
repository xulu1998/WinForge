namespace WinForge.Core.Models;

/// <summary>
/// Request to copy the original ISO media tree into an isolated build workspace
/// and replace its install image payload with the customized final WIM. The
/// source ISO is read (mounted read-only) and never modified.
/// </summary>
public sealed class MediaPrepareRequest
{
    /// <summary>Path to the original Windows ISO (read-only source of the media tree).</summary>
    public string SourceIsoPath { get; init; } = string.Empty;

    /// <summary>WinForge-owned root where the media tree is copied.</summary>
    public string BuildMediaRoot { get; init; } = string.Empty;

    /// <summary>Relative path of the source install image inside the ISO (e.g. <c>sources\install.wim</c>).</summary>
    public string SourceImageRelativePath { get; init; } = string.Empty;

    /// <summary>Container format of the source install image (WIM or ESD).</summary>
    public WindowsImageType SourceImageType { get; init; } = WindowsImageType.Unknown;

    /// <summary>The customized final WIM to place at <c>sources\install.wim</c> in the media tree.</summary>
    public string FinalInstallWimPath { get; init; } = string.Empty;
}

/// <summary>
/// Outcome of <see cref="IIsoMediaPreparer.PrepareAsync"/>. A successful result
/// guarantees the media tree was copied and the install image replaced;
/// <see cref="BootFilesPresent"/> reports whether the dual-boot files required by
/// oscdimg (<c>boot\etfsboot.com</c>, <c>efi\microsoft\boot\efisys.bin</c>) are
/// present in the copied tree, so the caller can stop the build with a clear
/// error when they are missing.
/// </summary>
public sealed class MediaPrepareResult
{
    public bool Success { get; init; }
    public string? MediaRoot { get; init; }
    public string? InstallImagePath { get; init; }
    public bool BootFilesPresent { get; init; }
    public string? ErrorMessage { get; init; }
    public System.Collections.Generic.IReadOnlyList<string> Issues { get; init; }
        = System.Array.Empty<string>();

    public static MediaPrepareResult Ok(string mediaRoot, string installImagePath, bool bootFilesPresent)
        => new()
        {
            Success = true,
            MediaRoot = mediaRoot,
            InstallImagePath = installImagePath,
            BootFilesPresent = bootFilesPresent
        };

    public static MediaPrepareResult Fail(string error, bool bootFilesPresent = false,
        System.Collections.Generic.IReadOnlyList<string>? issues = null)
        => new()
        {
            Success = false,
            ErrorMessage = error,
            BootFilesPresent = bootFilesPresent,
            Issues = issues ?? System.Array.Empty<string>()
        };

    public static MediaPrepareResult MissingBootFiles(string error)
        => new() { Success = false, ErrorMessage = error, BootFilesPresent = false };
}
