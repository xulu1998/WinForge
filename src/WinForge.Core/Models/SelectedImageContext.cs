namespace WinForge.Core.Models;

/// <summary>
/// A minimal, read-only resolution of the selected image: the durable source
/// identifiers a downstream Phase 3 operation needs to acquire its own temporary
/// source-access session (mount the ISO, locate the install image, target the
/// index). It contains no temporary drive letter and performs no I/O.
///
/// Step 3.1 produces this from a <see cref="ImageWorkspace"/> but does not act on
/// it — later steps (Step 3.2 export, Phase 4 mount) will consume it.
/// </summary>
public sealed class SelectedImageContext
{
    public SelectedImageContext(string sourceIsoPath, string imageRelativePath, WindowsImageType imageType, int selectedIndex)
    {
        SourceIsoPath = sourceIsoPath;
        ImageRelativePath = imageRelativePath;
        ImageType = imageType;
        SelectedIndex = selectedIndex;
    }

    /// <summary>Original ISO path (durable source).</summary>
    public string SourceIsoPath { get; }

    /// <summary>Relative path of the install image inside the ISO (durable).</summary>
    public string ImageRelativePath { get; }

    /// <summary>Container format of the install image.</summary>
    public WindowsImageType ImageType { get; }

    /// <summary>Selected image index.</summary>
    public int SelectedIndex { get; }
}
