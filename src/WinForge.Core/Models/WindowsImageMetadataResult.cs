using System.Collections.Generic;

namespace WinForge.Core.Models;

/// <summary>
/// Outcome of a read-only Windows image metadata inspection (Step 2.2). It holds
/// the structured, nullable data produced by parsing <c>dism /Get-ImageInfo</c>
/// output. Core keeps the raw/nullable values; the UI decides how to present
/// missing or inconsistent data (e.g. "Not detected" or "Mixed").
/// </summary>
public sealed class WindowsImageMetadataResult
{
    /// <summary>Path to the image that was inspected (install.wim / install.esd).</summary>
    public string? ImagePath { get; set; }

    /// <summary>Container format of the inspected image.</summary>
    public WindowsImageType ImageType { get; set; } = WindowsImageType.Unknown;

    /// <summary>Outcome of the metadata inspection run.</summary>
    public WindowsImageMetadataStatus Status { get; set; } = WindowsImageMetadataStatus.NotInspected;

    /// <summary>
    /// Top-level Windows version. Populated only when every edition reports the
    /// same version; null when editions disagree (the UI may show "Mixed").
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Top-level build number. Populated only when every edition agrees; null
    /// when editions disagree.
    /// </summary>
    public string? Build { get; set; }

    /// <summary>
    /// Top-level architecture. Populated only when every edition agrees; null
    /// when editions disagree.
    /// </summary>
    public string? Architecture { get; set; }

    /// <summary>
    /// Top-level language set. Populated only when every edition carries an
    /// identical language list; null when they differ.
    /// </summary>
    public List<string>? Languages { get; set; }

    /// <summary>All editions (image indexes) found in the image.</summary>
    public List<WindowsEditionInfo> Editions { get; set; } = new();

    /// <summary>User-facing error description when <see cref="Status"/> is Failed.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Outcome of a Windows image metadata inspection run. <see cref="NotInspected"/>
/// is the initial state; the final result is <see cref="Completed"/> or
/// <see cref="Failed"/>.
/// </summary>
public enum WindowsImageMetadataStatus
{
    NotInspected,
    Completed,
    Failed
}
