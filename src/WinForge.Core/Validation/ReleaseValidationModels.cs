namespace WinForge.Core.Validation;

/// <summary>
/// Phase 17 — Release-candidate hardening models.
///
/// Deterministic validation-artifact archival replaces the single-file overwrite
/// artifacts (profile-commit-validation.json / full-health-report.json). Each
/// validation run is archived under a unique runId so history is never lost,
/// and a "latest" pointer indexes the most recent run without destroying the
/// archive. The release validation manifest is machine-readable evidence that
/// never claims a higher validation level than actually demonstrated.
/// </summary>
public sealed class ValidationArtifactRun
{
    /// <summary>Unique run id (UTC sortable, e.g. 20260816-130500-<short-sha>).</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>UTC timestamp of the run.</summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>Absolute path of the source ISO (read-only input).</summary>
    public string SourceIsoPath { get; set; } = string.Empty;

    /// <summary>SHA-256 of the source ISO when available, else null.</summary>
    public string? SourceIsoSha256 { get; set; }

    /// <summary>WinForge profile being validated (e.g. Balanced).</summary>
    public string Profile { get; set; } = string.Empty;

    /// <summary>WIM index selected in the source image.</summary>
    public int WindowsIndex { get; set; }

    /// <summary>Edition name from the source image (e.g. Windows 11 Pro).</summary>
    public string Edition { get; set; } = string.Empty;

    /// <summary>Language tag (e.g. zh-CN).</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Architecture (e.g. x64).</summary>
    public string Architecture { get; set; } = string.Empty;

    /// <summary>WinForge commit SHA the validation was produced against.</summary>
    public string WinForgeCommitSha { get; set; } = string.Empty;

    /// <summary>Absolute path of the generated ISO (commit runs only).</summary>
    public string? GeneratedIsoPath { get; set; }

    /// <summary>SHA-256 of the generated ISO (commit runs only).</summary>
    public string? GeneratedIsoSha256 { get; set; }

    /// <summary>Validation level achieved (ADR-084 vocabulary).</summary>
    public string ValidationLevel { get; set; } = "WorkflowValidated";

    /// <summary>Result status of the run (Succeeded / Failed / Interrupted / Prepared).</summary>
    public string ResultStatus { get; set; } = "Prepared";

    /// <summary>Pipeline phase reached (Plan / ExpectedState / Commit / IsoBuild / Health). Recovery metadata.</summary>
    public string Phase { get; set; } = "Plan";

    /// <summary>Relative file names archived under this run directory.</summary>
    public List<string> Files { get; set; } = new();

    /// <summary>Free-form notes (e.g. warnings, non-blocking observations).</summary>
    public List<string> Notes { get; set; } = new();
}

/// <summary>
/// Machine-readable release validation manifest — evidence, not marketing.
/// Summarizes every built-in profile with its ACTUAL demonstrated validation
/// level and outstanding debt. Never claims a higher level than demonstrated.
/// </summary>
public sealed class ReleaseValidationManifest
{
    public DateTime GeneratedUtc { get; set; }

    /// <summary>WinForge commit SHA this manifest describes.</summary>
    public string WinForgeCommitSha { get; set; } = string.Empty;

    public ManifestMedia Media { get; set; } = new();

    public List<ProfileValidationEntry> Profiles { get; set; } = new();
}

public sealed class ManifestMedia
{
    public string SourceIsoPath { get; set; } = string.Empty;
    public int WindowsIndex { get; set; } = 4;
    public string Edition { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string WindowsBuild { get; set; } = string.Empty;
}

/// <summary>Per-profile validation record. Levels are boolean flags so the
/// manifest can never accidentally claim a level not demonstrated.</summary>
public sealed class ProfileValidationEntry
{
    public string ProfileId { get; set; } = string.Empty;
    public bool WorkflowValidated { get; set; }
    public bool VmInstallValidated { get; set; }
    public bool FullHealthValidated { get; set; }

    /// <summary>Last WinForge commit the profile's validation evidence applies to.</summary>
    public string LastValidatedCommit { get; set; } = string.Empty;

    /// <summary>Source Windows build (e.g. 26200.8037).</summary>
    public string SourceWindowsBuild { get; set; } = string.Empty;

    public int BuildPlanOperationCount { get; set; }
    public int SelectedOperationCount { get; set; }

    /// <summary>Generated ISO SHA-256 when the profile has been VM validated.</summary>
    public string? IsoSha256 { get; set; }

    /// <summary>Reference to the archived health-report artifact (runId/file).</summary>
    public string? HealthReportRef { get; set; }

    public List<string> Warnings { get; set; } = new();
    public List<string> ValidationDebt { get; set; } = new();
}

/// <summary>Machine-readable six-profile delta audit (Stage 17.4).</summary>
public sealed class ProfileDeltaAudit
{
    public DateTime GeneratedUtc { get; set; }

    /// <summary>Canonical operation keys common to every profile (selected only).</summary>
    public List<string> CommonSelectedKeys { get; set; } = new();

    /// <summary>Per-profile exclusive selected keys and type distribution.</summary>
    public List<ProfileDeltaEntry> Profiles { get; set; } = new();

    /// <summary>Detected accidental convergence (profiles whose selected sets are identical).</summary>
    public List<string> ConvergenceWarnings { get; set; } = new();
}

public sealed class ProfileDeltaEntry
{
    public string ProfileId { get; set; } = string.Empty;
    public int SelectedCount { get; set; }
    public List<string> ExclusiveKeys { get; set; } = new();
    public Dictionary<string, int> OperationTypeDistribution { get; set; } = new();
    public List<string> RecommendOnlyKeys { get; set; } = new();
}

/// <summary>Release safety invariant — a built-in profile must never violate it.</summary>
public sealed class ReleaseSafetyInvariant
{
    public string Id { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Canonical key prefixes the invariant protects (a plan op whose
    /// ConflictKey starts with any prefix violates the invariant).</summary>
    public List<string> ProtectedKeyPrefixes { get; set; } = new();

    /// <summary>Service names that must never be touched.</summary>
    public List<string> ProtectedServices { get; set; } = new();
}

/// <summary>Result of checking a plan against the invariant set.</summary>
public sealed class InvariantCheckResult
{
    public bool Passed { get; set; }
    public List<string> Violations { get; set; } = new();
}
