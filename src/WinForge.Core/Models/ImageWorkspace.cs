using System.Collections.Generic;

namespace WinForge.Core.Models;

/// <summary>
/// A durable description of the Windows image the user has selected for
/// customization. It survives ISO dismount: it references the original ISO file
/// and the image's <em>relative</em> path inside that ISO, never a temporary
/// mounted drive letter such as <c>G:\</c> that disappears after inspection.
///
/// Phase 3 Step 3.1 builds this from a Phase 2 <see cref="IsoInspectionResult"/>
/// plus the user's selected <see cref="WindowsEditionInfo"/>. Future Phase 3
/// operations (export, mount, build) will use these durable identifiers to
/// acquire their own temporary source-access session; nothing here mounts,
/// modifies, or services the image.
/// </summary>
public sealed class ImageWorkspace
{
    /// <summary>
    /// Path to the original Windows ISO the user selected (e.g.
    /// <c>F:\ISOs\Win11.iso</c>). This is the durable source — never a temporary
    /// mounted drive root.
    /// </summary>
    public string? SourceIsoPath { get; set; }

    /// <summary>
    /// Relative path of the install image inside the ISO, using Windows-style
    /// separators, e.g. <c>sources\install.wim</c> or <c>sources\install.esd</c>.
    /// Durable across dismount; derived, not copied from a temp mount root.
    /// </summary>
    public string? ImageRelativePath { get; set; }

    /// <summary>Container format of the selected install image.</summary>
    public WindowsImageType ImageType { get; set; } = WindowsImageType.Unknown;

    /// <summary>1-based image index of the selected edition inside the WIM/ESD.</summary>
    public int SelectedIndex { get; set; }

    /// <summary>Display name of the selected edition, e.g. <c>Windows 11 Pro</c>.</summary>
    public string? SelectedEditionName { get; set; }

    /// <summary>Processor architecture of the selected edition (e.g. <c>x64</c>).</summary>
    public string? Architecture { get; set; }

    /// <summary>Full Windows version of the selected edition, e.g. <c>10.0.26200.0</c>.</summary>
    public string? Version { get; set; }

    /// <summary>Windows build number of the selected edition, e.g. <c>26200</c>.</summary>
    public string? Build { get; set; }

    /// <summary>Languages available in the selected edition (e.g. <c>zh-CN</c>).</summary>
    public List<string> Languages { get; set; } = new();

    /// <summary>
    /// Normalizes a relative path to a durable, Windows-style form: forward and
    /// back slashes are collapsed to a single backslash and leading/trailing
    /// separators are trimmed. The result is deterministic on every host OS, so
    /// the stored path never depends on where the ISO happened to be mounted.
    /// </summary>
    public static string NormalizeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var buffer = new System.Text.StringBuilder(relativePath.Length);
        var previousSeparator = false;

        foreach (var c in relativePath)
        {
            if (c == '/' || c == '\\')
            {
                if (!previousSeparator)
                {
                    buffer.Append('\\');
                }

                previousSeparator = true;
            }
            else
            {
                buffer.Append(c);
                previousSeparator = false;
            }
        }

        return buffer.ToString().Trim('\\');
    }
}
