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
    /// Validation level achieved by this record (ADR-084). Phase 13 baseline is
    /// <see cref="ValidationLevel.VmInstallValidated"/>; deeper post-install
    /// health checks become mandatory in later aggressive component-removal phases.
    /// </summary>
    public ValidationLevel Level { get; init; } = ValidationLevel.NotAssessed;

    /// <summary>
    /// True when every phase REQUIRED BY <see cref="Level"/> is recorded AND passed.
    /// A partial record — or a record with no assessed level — is never a
    /// "Validated" claim (ADR-074/084).
    /// </summary>
    public bool AllPhasesPassed
    {
        get
        {
            if (Level == ValidationLevel.NotAssessed)
            {
                return false;
            }

            return RequiredPhases(Level).All(p => Phases.TryGetValue(p, out var ok) && ok);
        }
    }

    /// <summary>The exact phase set a level demands (single source of truth).</summary>
    public static IReadOnlyList<ValidationPhase> RequiredPhases(ValidationLevel level)
        => level switch
        {
            ValidationLevel.WorkflowValidated => WorkflowPhases,
            ValidationLevel.VmInstallValidated => VmInstallPhases,
            ValidationLevel.FullHealthValidated => Enum.GetValues<ValidationPhase>(),
            _ => Array.Empty<ValidationPhase>(),
        };

    /// <summary>Workflow stages: inspection → ISO verification (no VM involved).</summary>
    public static readonly IReadOnlyList<ValidationPhase> WorkflowPhases = new[]
    {
        ValidationPhase.InspectionPassed,
        ValidationPhase.PreparePassed,
        ValidationPhase.DiscoveryPassed,
        ValidationPhase.CustomizePassed,
        ValidationPhase.ApplyPassed,
        ValidationPhase.BuildPassed,
        ValidationPhase.IsoVerificationPassed,
    };

    /// <summary>VM install acceptance stages (Phase 13 baseline — boot/install/OOBE/desktop).</summary>
    public static readonly IReadOnlyList<ValidationPhase> VmInstallPhases = new[]
    {
        ValidationPhase.IsoVerificationPassed,
        ValidationPhase.VmBootPassed,
        ValidationPhase.SetupPassed,
        ValidationPhase.OobePassed,
        ValidationPhase.DesktopReached,
    };

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

/// <summary>
/// Validation depth (ADR-084): what a "Validated" claim actually covers.
/// Phase 13 baseline = VmInstallValidated — deeper health checks (Windows
/// Update / Defender / Store / DISM ScanHealth / recovery) are REQUIRED only
/// once component removal becomes substantially more aggressive.
/// </summary>
public enum ValidationLevel
{
    NotAssessed,
    WorkflowValidated,
    VmInstallValidated,
    FullHealthValidated,
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
