using System;
using System.Collections.Generic;
using System.Linq;

namespace WinForge.Core.Compatibility;

// =====================================================================
// Phase 13 — Compatibility model (ADR-073..078). "Windows 11 ISO" is NOT a
// uniform format: WinForge detects release/build/edition/language/architecture/
// image-format/media-class from the image itself and NEVER pretends unknown
// combinations are supported. Automated fixture coverage and REAL validated
// targets are kept strictly distinct (ADR-074).
// =====================================================================

/// <summary>Overall compatibility verdict for an image / workflow.</summary>
public enum CompatibilityStatus
{
    /// <summary>Image matches a validated matrix entry.</summary>
    Supported,

    /// <summary>Usable, but at least one non-blocking warning applies.</summary>
    SupportedWithWarnings,

    /// <summary>Some operations may work; the combination is not fully validated.</summary>
    PartiallySupported,

    /// <summary>Pipeline cannot safely handle this image.</summary>
    Unsupported,

    /// <summary>Not enough information to decide.</summary>
    Unknown,
}

/// <summary>Severity of a single compatibility finding.</summary>
public enum CompatibilitySeverity
{
    Info,
    Warning,
    Blocking,
}

/// <summary>Category used to group findings in the UI.</summary>
public enum CompatibilityCategory
{
    ImageFormat,
    Release,
    Edition,
    Language,
    Architecture,
    MediaStructure,
    Security,
    Unknown,
}

/// <summary>Normalized Windows release classification (ADR-073).</summary>
public enum WindowsRelease
{
    Unknown,

    /// <summary>Windows 11 24H2 (build 26100 family).</summary>
    Windows11_24H2,

    /// <summary>Windows 11 25H2 (build 26100.x / 26200 family).</summary>
    Windows11_25H2,

    /// <summary>A future Windows 11 build newer than the validated matrix.</summary>
    Windows11_UnknownNewer,

    /// <summary>Older Windows (10 or earlier) — not a Phase 13 target.</summary>
    OlderWindows,
}

/// <summary>Image container format.</summary>
public enum ImageFormatKind
{
    Unknown,
    None,
    Wim,
    Esd,
    /// <summary>Split WIM (install.swm + install2.swm …) — inspect-only today.</summary>
    Swm,
}

/// <summary>Conservative source-media classification (ADR-077 — never overclaim).</summary>
public enum MediaClassification
{
    Unknown,

    /// <summary>Media structure matches a standard Windows installation media layout.</summary>
    MicrosoftOfficialLike,

    /// <summary>Structure deviates from a standard layout (modified/repacked media).</summary>
    ModifiedMedia,
}

/// <summary>One compatibility finding (localized by key, severity-tagged).</summary>
public sealed class CompatibilityFinding
{
    public string Key { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public CompatibilitySeverity Severity { get; init; } = CompatibilitySeverity.Info;
    public CompatibilityCategory Category { get; init; } = CompatibilityCategory.Unknown;

    public bool IsBlocking => Severity == CompatibilitySeverity.Blocking;
}

/// <summary>
/// Full compatibility profile of an inspected image (Stage 13.1). Populated from
/// ISO layout inspection + WIM/ESD metadata; NEVER from the filename alone.
/// </summary>
public sealed class ImageCompatibilityProfile
{
    public string? IsoPath { get; init; }
    public string? Architecture { get; init; }
    public string? ProductName { get; init; }
    public string? EditionId { get; init; }
    public string? InstallationType { get; init; }
    public string? Version { get; init; }
    public int? Build { get; init; }
    public int? Ubr { get; init; }
    public string? DisplayVersion { get; init; }
    public string? ReleaseId { get; init; }
    public string? DefaultLanguage { get; init; }
    public List<string> AvailableLanguages { get; init; } = new();

    public ImageFormatKind ImageFormat { get; init; } = ImageFormatKind.Unknown;
    public int ImageCount { get; init; }
    public int SelectedIndex { get; init; }

    public MediaClassification MediaType { get; init; } = MediaClassification.Unknown;
    public bool HasBootWim { get; init; }
    public bool HasInstallImage { get; init; }
    public bool HasRecoveryEnvironment { get; init; }
    public bool HasSplitSwm { get; init; }
    public int SwmPartCount { get; init; }

    /// <summary>Normalized release (Stage 13.2).</summary>
    public WindowsRelease Release { get; init; } = WindowsRelease.Unknown;

    /// <summary>Stable, non-filename source identity for provenance.</summary>
    public string SourceFingerprint { get; init; } = string.Empty;

    public CompatibilityStatus Status { get; set; } = CompatibilityStatus.Unknown;

    /// <summary>Findings, sorted by severity (Blocking → Warning → Info) at evaluation time.</summary>
    public List<CompatibilityFinding> Findings { get; set; } = new();

    public bool HasBlockers => Findings.Any(f => f.IsBlocking);
    public bool HasWarnings => Findings.Any(f => f.Severity == CompatibilitySeverity.Warning);

    public static ImageCompatibilityProfile UnknownResult() => new() { Status = CompatibilityStatus.Unknown };
}

/// <summary>
/// Edition capability facts (Stage 13.3). Capability availability varies by
/// edition; recommendations must never show edition-gated actions as universally
/// valid. Data — not behavior — so views/UI can consume it.
/// </summary>
public sealed class EditionCapabilityProfile
{
    public string EditionId { get; init; } = string.Empty;

    /// <summary>Windows Sandbox availability.</summary>
    public bool HasSandbox { get; init; }

    /// <summary>Hyper-V platform availability.</summary>
    public bool HasHyperV { get; init; }

    /// <summary>Remote Desktop HOST availability.</summary>
    public bool HasRdpHost { get; init; }

    /// <summary>BitLocker availability.</summary>
    public bool HasBitLocker { get; init; }

    /// <summary>Pro features (Group Policy, Hyper-V, Sandbox, RDP host, WSL2 defaults).</summary>
    public bool HasProFeatures { get; init; }

    /// <summary>Whether a targeted edition-specific operation is compatible.</summary>
    public bool IsSupportedBy(EditionCapabilityRequirement requirement)
        => requirement switch
        {
            EditionCapabilityRequirement.None => true,
            EditionCapabilityRequirement.ProOrHigher => HasProFeatures,
            EditionCapabilityRequirement.Sandbox => HasSandbox,
            EditionCapabilityRequirement.HyperV => HasHyperV,
            EditionCapabilityRequirement.RdpHost => HasRdpHost,
            EditionCapabilityRequirement.BitLocker => HasBitLocker,
            _ => false,
        };
}

/// <summary>Capability a definition may require from the edition (Stage 13.20).</summary>
public enum EditionCapabilityRequirement
{
    None,
    ProOrHigher,
    Sandbox,
    HyperV,
    RdpHost,
    BitLocker,
}

/// <summary>Known Windows 11 edition identities (Stage 13.3) with capability facts.</summary>
public static class EditionCompatibilityCatalog
{
    public static readonly IReadOnlyList<EditionCapabilityProfile> All = new[]
    {
        new EditionCapabilityProfile { EditionId = "Core", HasBitLocker = true },
        new EditionCapabilityProfile { EditionId = "CoreSingleLanguage", HasBitLocker = true },
        new EditionCapabilityProfile { EditionId = "Professional", HasProFeatures = true, HasSandbox = true, HasHyperV = true, HasRdpHost = true, HasBitLocker = true },
        new EditionCapabilityProfile { EditionId = "ProfessionalEducation", HasProFeatures = true, HasSandbox = true, HasHyperV = true, HasRdpHost = true, HasBitLocker = true },
        new EditionCapabilityProfile { EditionId = "ProfessionalWorkstation", HasProFeatures = true, HasSandbox = true, HasHyperV = true, HasRdpHost = true, HasBitLocker = true },
        new EditionCapabilityProfile { EditionId = "Education", HasProFeatures = true, HasSandbox = true, HasHyperV = true, HasRdpHost = true, HasBitLocker = true },
        new EditionCapabilityProfile { EditionId = "Enterprise", HasProFeatures = true, HasSandbox = true, HasHyperV = true, HasRdpHost = true, HasBitLocker = true },
        new EditionCapabilityProfile { EditionId = "ServerDatacenter", HasProFeatures = true, HasHyperV = true, HasRdpHost = true },
        new EditionCapabilityProfile { EditionId = "ServerStandard", HasProFeatures = true, HasHyperV = true, HasRdpHost = true },
        new EditionCapabilityProfile { EditionId = "IoTEnterprise", HasProFeatures = true, HasSandbox = true, HasHyperV = true, HasRdpHost = true, HasBitLocker = true },
    };

    /// <summary>Look up edition capability by EditionId (case-insensitive); null when unknown.</summary>
    public static EditionCapabilityProfile? For(string? editionId)
        => All.FirstOrDefault(p => string.Equals(p.EditionId, editionId?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsKnown(string? editionId) => For(editionId) is not null;
}
