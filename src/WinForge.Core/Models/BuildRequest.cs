namespace WinForge.Core.Models;

/// <summary>
/// Immutable description of a Build / ISO export request. The working image is
/// expected to be committed (mounted) by the build pipeline; the original source
/// ISO is never modified — it is only read to copy its media tree.
/// </summary>
public sealed class BuildRequest
{
    /// <summary>Path to the original Windows ISO the working image was derived from.</summary>
    public string SourceIsoPath { get; init; } = string.Empty;

    /// <summary>
    /// Relative path of the source install image inside the ISO, e.g.
    /// <c>sources\install.wim</c> or <c>sources\install.esd</c>.
    /// </summary>
    public string SourceImageRelativePath { get; init; } = string.Empty;

    /// <summary>Container format of the source install image (WIM or ESD).</summary>
    public WindowsImageType SourceImageType { get; init; } = WindowsImageType.Unknown;

    /// <summary>Absolute path of the mounted, customized working image (always a WIM).</summary>
    public string WorkingImagePath { get; init; } = string.Empty;

    /// <summary>Absolute path of the working image's mount directory (used to commit).</summary>
    public string MountDirectory { get; init; } = string.Empty;

    /// <summary>1-based index of the customized edition inside the working image.</summary>
    public int WorkingIndex { get; init; } = 1;

    /// <summary>Display name of the source edition that was customized.</summary>
    public string? SourceEditionName { get; init; }

    /// <summary>Display name shown for the final edition in the output ISO (defaults to source).</summary>
    public string? FinalEditionName { get; init; }

    /// <summary>Directory the final .iso will be written to (user-chosen).</summary>
    public string OutputDirectory { get; init; } = string.Empty;

    /// <summary>File name (without extension) for the final .iso.</summary>
    public string OutputFileName { get; init; } = string.Empty;

    /// <summary>Output policy (single customized edition for this phase).</summary>
    public BuildMode Mode { get; init; } = BuildMode.SingleCustomizedEdition;

    /// <summary>How to behave when the output path already exists.</summary>
    public BuildOverwritePolicy OverwritePolicy { get; init; } = BuildOverwritePolicy.GenerateUniqueName;

    /// <summary>
    /// WinForge-owned directory for temporary build artifacts (media tree, exported
    /// WIM). Never the user's output directory and never the source ISO root.
    /// </summary>
    public string BuildWorkspaceDirectory { get; init; } = string.Empty;
}
