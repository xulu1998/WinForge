using System.Collections.Generic;

namespace WinForge.Core.Models;

/// <summary>
/// Structured metadata describing a single Windows edition (image index) found
/// inside an install WIM/ESD. Fields are nullable so that data WinForge cannot
/// reliably read stays <c>null</c> rather than being guessed; the UI decides
/// whether to show "Not detected" or "Mixed". The model never stores a
/// hardcoded <c>Unknown</c> sentinel derived from a UI assumption.
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
}
