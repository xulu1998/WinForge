using System;
using System.Collections.Generic;

namespace WinForge.Core.Compatibility;

// =====================================================================
// Phase 13 — validation matrix data model (Stage 13.11) + initial targets
// (Stage 13.12) + VM acceptance phases (Stage 13.13/13.14).
// "Validated" means real media + real workflow + VM install validation
// (ADR-074); automated fixture coverage is recorded SEPARATELY.
// =====================================================================

/// <summary>A stage of the validation pipeline (matrix column).</summary>
public enum ValidationPhase
{
    InspectionPassed,
    PreparePassed,
    DiscoveryPassed,
    CustomizePassed,
    ApplyPassed,
    BuildPassed,
    IsoVerificationPassed,
    VmBootPassed,
    SetupPassed,
    OobePassed,
    DesktopReached,
    WindowsUpdatePassed,
    StorePassed,
    DefenderHealthy,
    DriverInstallPassed,
    RecoveryEnvironmentPassed,
}

/// <summary>One row of the compatibility matrix (what combination we claim to validate).</summary>
public sealed class ValidationTarget
{
    public string Id { get; init; } = string.Empty;
    public WindowsRelease Release { get; init; }
    public string? BuildRange { get; init; }
    public string EditionId { get; init; } = string.Empty;
    public string Architecture { get; init; } = "x64";
    public string Language { get; init; } = "zh-CN";
    public ImageFormatKind ImageFormat { get; init; } = ImageFormatKind.Wim;
    public MediaClassification MediaType { get; init; } = MediaClassification.MicrosoftOfficialLike;

    public string DisplayName => $"{EditionId}-{Language}-{Architecture}-{Release}";

    public override string ToString() => $"{Id} ({DisplayName})";
}

/// <summary>Outcome of validating one target (a filled matrix cell).</summary>
public sealed class ValidationResult
{
    public string TargetId { get; init; } = string.Empty;
    public string? WinForgeCommit { get; init; }
    public string? WinForgeVersion { get; init; }
    public DateTimeOffset Date { get; init; } = DateTimeOffset.UtcNow;

    public Dictionary<ValidationPhase, bool> Phases { get; init; } = new();

    /// <summary>
    /// True only when EVERY pipeline phase is recorded AND passed — a partial
    /// record is never a "Validated" claim (ADR-074).
    /// </summary>
    public bool AllPhasesPassed
        => Enum.GetValues<ValidationPhase>().All(p => Phases.TryGetValue(p, out var ok) && ok);

    public string? SourceImageMetadata { get; init; }
    public int? SelectedIndex { get; init; }
    public string? CustomizationProfile { get; init; }
    public int? OperationsCount { get; init; }
    public string? BuildIsoPath { get; init; }
    public string? IsoSha256 { get; init; }
    public long? IsoSizeBytes { get; init; }
    public string? Notes { get; init; }

    /// <summary>How this result was obtained — REAL VM validation vs automated fixtures (ADR-074).</summary>
    public ValidationEvidenceKind Evidence { get; init; } = ValidationEvidenceKind.NotRecorded;
}

/// <summary>Strict separation between real validation and automated coverage (ADR-074).</summary>
public enum ValidationEvidenceKind
{
    NotRecorded,

    /// <summary>Real media + real WinForge workflow + VM install validation.</summary>
    RealVmValidation,

    /// <summary>Automated synthetic fixtures only — NEVER claim "Validated".</summary>
    AutomatedFixturesOnly,
}

/// <summary>Initial supported validation targets (Stage 13.12). Tier A must validate first.</summary>
public static class InitialValidationTargets
{
    public static readonly IReadOnlyList<ValidationTarget> All = new[]
    {
        // Tier A — must validate first (baseline already proven on 25H2 zh-CN Pro).
        new ValidationTarget { Id = "25H2-Pro-zh-CN-x64", Release = WindowsRelease.Windows11_25H2, EditionId = "Professional", Language = "zh-CN", Architecture = "x64", ImageFormat = ImageFormatKind.Wim },
        new ValidationTarget { Id = "25H2-Pro-en-US-x64", Release = WindowsRelease.Windows11_25H2, EditionId = "Professional", Language = "en-US", Architecture = "x64", ImageFormat = ImageFormatKind.Wim },
        // Tier B.
        new ValidationTarget { Id = "25H2-Home-zh-CN-x64", Release = WindowsRelease.Windows11_25H2, EditionId = "Core", Language = "zh-CN", Architecture = "x64", ImageFormat = ImageFormatKind.Wim },
        new ValidationTarget { Id = "25H2-Education-zh-CN-x64", Release = WindowsRelease.Windows11_25H2, EditionId = "Education", Language = "zh-CN", Architecture = "x64", ImageFormat = ImageFormatKind.Wim },
        new ValidationTarget { Id = "25H2-Enterprise-zh-CN-x64", Release = WindowsRelease.Windows11_25H2, EditionId = "Enterprise", Language = "zh-CN", Architecture = "x64", ImageFormat = ImageFormatKind.Wim },
        // Tier C.
        new ValidationTarget { Id = "24H2-Pro-zh-CN-x64", Release = WindowsRelease.Windows11_24H2, EditionId = "Professional", Language = "zh-CN", Architecture = "x64", ImageFormat = ImageFormatKind.Wim },
        new ValidationTarget { Id = "24H2-Pro-en-US-x64", Release = WindowsRelease.Windows11_24H2, EditionId = "Professional", Language = "en-US", Architecture = "x64", ImageFormat = ImageFormatKind.Wim },
        new ValidationTarget { Id = "25H2-Pro-zh-CN-x64-ESD", Release = WindowsRelease.Windows11_25H2, EditionId = "Professional", Language = "zh-CN", Architecture = "x64", ImageFormat = ImageFormatKind.Esd },
        new ValidationTarget { Id = "25H2-MultiIndex-Consumer", Release = WindowsRelease.Windows11_25H2, EditionId = "Core", Language = "zh-CN", Architecture = "x64", ImageFormat = ImageFormatKind.Wim },
        new ValidationTarget { Id = "25H2-MultiIndex-Business", Release = WindowsRelease.Windows11_25H2, EditionId = "Professional", Language = "zh-CN", Architecture = "x64", ImageFormat = ImageFormatKind.Wim },
    };

    public static bool IsKnownTarget(string id)
        => All.Any(t => t.Id == id);
}
