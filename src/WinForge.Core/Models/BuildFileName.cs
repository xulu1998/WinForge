using System.IO;
using System.Text;

namespace WinForge.Core.Models;

/// <summary>
/// Pure helpers for producing safe, deterministic build output file names. The
/// rules deliberately refuse to silently overwrite user data: invalid filename
/// characters are stripped (replaced with <c>_</c>), reserved device names are
/// escaped, and uniqueness is generated on demand by the pipeline using the
/// filesystem.
/// </summary>
public static class BuildFileName
{
    /// <summary>
    /// Invalid characters in a Windows file name (excluding the directory
    /// separator, which is handled separately).
    /// </summary>
    private static readonly char[] InvalidChars =
        Path.GetInvalidFileNameChars();

    /// <summary>
    /// Reserved DOS device names that must not be used verbatim as a file name.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Sanitizes a base file name (without extension) by removing/replacing every
    /// invalid character, trimming whitespace and separators, and escaping a bare
    /// reserved device name. Returns <c>WinForge_Image</c> when nothing usable
    /// remains after sanitization.
    /// </summary>
    public static string SanitizeBaseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "WinForge_Image";
        }

        var sb = new StringBuilder(name.Length);
        foreach (var c in name!)
        {
            if (Array.IndexOf(InvalidChars, c) >= 0)
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }

        var trimmed = sb.ToString().Trim().Trim('.', ' ', '\t');
        // Collapse runs of underscores for readability.
        var collapsed = System.Text.RegularExpressions.Regex.Replace(trimmed, "_+", "_");

        if (string.IsNullOrWhiteSpace(collapsed))
        {
            return "WinForge_Image";
        }

        // Escape a reserved device name used verbatim (e.g. "con") so it cannot
        // be misinterpreted by the filesystem.
        var baseName = Path.GetFileNameWithoutExtension(collapsed);
        if (ReservedNames.Contains(baseName))
        {
            return "_" + collapsed;
        }

        return collapsed;
    }

    /// <summary>
    /// Builds the default output file name
    /// <c>WinForge_&lt;Edition&gt;_&lt;yyyyMMdd-HHmm&gt;.iso</c> from an edition
    /// name and a timestamp, sanitizing the edition segment.
    /// </summary>
    public static string DefaultIsoName(string? editionName, DateTime when)
    {
        var edition = SanitizeBaseName(editionName);
        // Edition names (e.g. "Windows 11 Pro") may contain spaces. Normalize them
        // to underscores so the default file name matches the WinForge_<Edition>_<stamp>
        // template uniformly and stays shell-friendly.
        edition = edition.Replace(' ', '_');
        var stamp = when.ToString("yyyyMMdd-HHmm");
        return $"WinForge_{edition}_{stamp}.iso";
    }
}
