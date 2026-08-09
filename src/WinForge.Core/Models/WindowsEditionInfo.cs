using System.Collections.Generic;

namespace WinForge.Core.Models;

/// <summary>
/// Structured metadata describing a single Windows edition (image index) found
/// inside an install WIM/ESD. Fields are nullable so that data WinForge cannot
/// reliably read stays <c>null</c> rather than being guessed; the UI decides
/// whether to show "Not detected" or "Mixed". The model never stores a
/// hardcoded <c>Unknown</c> sentinel derived from a UI assumption.
///
/// Per Step 2.2's two-stage DISM flow, the enumeration query always populates
/// <see cref="Index"/>, <see cref="Name"/>, and <see cref="Description"/>. The
/// detailed per-index query then fills the remaining fields; if that query fails
/// for an index, those fields stay <c>null</c> and <see cref="DetailStatus"/>
/// records the failure so the UI never silently pretends full metadata arrived.
/// </summary>
public sealed class WindowsEditionInfo
{
    /// <summary>1-based image index inside the WIM/ESD.</summary>
    public int Index { get; set; }

    /// <summary>Edition display name, e.g. <c>Windows 11 Home</c>.</summary>
    public string? Name { get; set; }

    /// <summary>Edition description (often equal to <see cref="Name"/>).</summary>
    public string? Description { get; set; }

    /// <summary>Processor architecture, e.g. <c>x64</c>, or null if not reported.</summary>
    public string? Architecture { get; set; }

    /// <summary>Edition identifier supplied by Microsoft metadata, if present.</summary>
    public string? EditionId { get; set; }

    /// <summary>Full Windows version, e.g. <c>10.0.26100.1742</c>.</summary>
    public string? Version { get; set; }

    /// <summary>Windows build number derived from <see cref="Version"/>, e.g. <c>26100</c>.</summary>
    public string? Build { get; set; }

    /// <summary>Installation type, e.g. <c>Client</c> / <c>Server</c>.</summary>
    public string? InstallationType { get; set; }

    /// <summary>Languages available in this edition (e.g. <c>en-US</c>).</summary>
    public List<string> Languages { get; set; } = new();

    /// <summary>Default (fallback) language for this edition, if reported by DISM.</summary>
    public string? DefaultLanguage { get; set; }

    /// <summary>
    /// Outcome of the per-index detailed metadata query for this edition. The
    /// enumeration query always reports <see cref="NotQueried"/> until a
    /// successful <see cref="Queried"/> detail pass, or <see cref="Failed"/> when
    /// the detail query could not be read.
    /// </summary>
    public WindowsEditionDetailStatus DetailStatus { get; set; } = WindowsEditionDetailStatus.NotQueried;

    /// <summary>
    /// Optional note recorded when the per-index detail query fails. It is logged,
    /// never shown raw to the user; <see cref="DetailStatus"/> is what the UI
    /// inspects to decide how to present missing detail.
    /// </summary>
    public string? DetailErrorMessage { get; set; }
}

/// <summary>
/// Outcome of the detailed, per-index DISM metadata query for a single edition.
/// The high-level enumeration query only guarantees <see cref="Index"/>,
/// <see cref="WindowsEditionInfo.Name"/>, and <see cref="WindowsEditionInfo.Description"/>.
/// </summary>
public enum WindowsEditionDetailStatus
{
    /// <summary>Detail not yet queried (only enumeration data is present).</summary>
    NotQueried,

    /// <summary>Detail query succeeded; the edition's detailed fields are populated.</summary>
    Queried,

    /// <summary>Detail query failed; the edition keeps its enumeration data but its detailed fields stay null.</summary>
    Failed
}
