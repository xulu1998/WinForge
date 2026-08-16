namespace WinForge.Core.Profiles;

/// <summary>
/// Structured report for the REAL offline COMMIT/BUILD validation mode
/// (Phase 16 Stage 16.1, ADR-098). Unlike the discard-only apply validation
/// (Stage 15.4), this run COMMITS the customized working WIM, builds a final
/// bootable ISO and verifies the committed image + ISO structure with
/// independent read-backs. The source ISO is never modified and the output is
/// written only to user-chosen, workspace-owned paths.
/// </summary>
public sealed class ProfileCommitValidationReport
{
    /// <summary>Profile id that was executed (e.g. "Balanced").</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>Absolute path of the read-only source ISO (never modified).</summary>
    public string SourceIsoPath { get; set; } = string.Empty;

    /// <summary>Source media identity (file name + size, e.g. "Win11_25H2_…_x64_v2.iso (5.1 GB)").</summary>
    public string SourceMediaIdentity { get; set; } = string.Empty;

    /// <summary>1-based index inside the source ISO that was customized.</summary>
    public int SelectedIndex { get; set; }

    /// <summary>Edition display name, e.g. "Windows 11 Pro".</summary>
    public string EditionName { get; set; } = string.Empty;

    // ---- Pre-commit (apply + read-back) ----

    /// <summary>Total BuildPlan operations (candidates).</summary>
    public int BuildPlanOperationCount { get; set; }

    /// <summary>Operations selected for execution (AutoApply).</summary>
    public int SelectedOperationCount { get; set; }

    /// <summary>Operations actually executed.</summary>
    public int Attempted { get; set; }

    /// <summary>Executed operations that succeeded AND read-back verified.</summary>
    public int Succeeded { get; set; }

    /// <summary>Executed operations that failed or failed read-back.</summary>
    public int Failed { get; set; }

    /// <summary>Operations skipped deterministically (already-satisfied).</summary>
    public int Skipped { get; set; }

    /// <summary>
    /// True only when the pre-commit apply report passed AND every attempted
    /// operation was read-back Verified. The commit is GATED on this.
    /// </summary>
    public bool PreCommitValidationPassed { get; set; }

    /// <summary>Non-null when the pre-commit gate rejected the run (nothing committed).</summary>
    public string? PreCommitGateFailure { get; set; }

    /// <summary>Per-operation pre-commit verification rows (from the apply report).</summary>
    public List<ProfileApplyOperationReport> Operations { get; set; } = new();

    // ---- Commit ----

    /// <summary>True when the working WIM was committed (DISM /Commit) successfully.</summary>
    public bool Committed { get; set; }

    /// <summary>Non-null when the commit/build step failed (working image left recoverable).</summary>
    public string? CommitError { get; set; }

    // ---- Post-commit (independent re-verification of the COMMITTED image) ----

    /// <summary>True when re-mounting the committed WIM and re-verifying succeeded.</summary>
    public bool PostCommitVerified { get; set; }

    /// <summary>Non-null when post-commit verification failed.</summary>
    public string? PostCommitError { get; set; }

    /// <summary>Per-operation post-commit read-back rows against the committed WIM.</summary>
    public List<ProfilePostCommitCheck> PostCommitChecks { get; set; } = new();

    /// <summary>True when the committed WIM is still mountable/readable (DISM metadata query).</summary>
    public bool CommittedImageReadable { get; set; }

    // ---- ISO output ----

    /// <summary>Final ISO metadata (null when the ISO was not produced).</summary>
    public IsoOutputReport? Iso { get; set; }

    /// <summary>Cleanup result (always reported; a failed discard is a blocker).</summary>
    public ProfileApplyMountCleanupReport MountCleanup { get; set; } = new();
}

/// <summary>Independent post-commit read-back row for one executed operation.</summary>
public sealed class ProfilePostCommitCheck
{
    public string CanonicalKey { get; init; } = string.Empty;
    public string OperationType { get; init; } = string.Empty;
    public string ExpectedAction { get; init; } = string.Empty;

    /// <summary>ApplyVerificationStatus name: Verified / VerificationFailed / NotApplicable.</summary>
    public string VerificationStatus { get; init; } = string.Empty;
    public string VerificationDetail { get; init; } = string.Empty;
}

/// <summary>Metadata + structural validation of the produced final ISO.</summary>
public sealed class IsoOutputReport
{
    /// <summary>Absolute path of the final ISO (inside the user's output directory).</summary>
    public string OutputPath { get; init; } = string.Empty;

    /// <summary>File size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>SHA-256 of the produced ISO (streamed; never the source ISO).</summary>
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>True when every structural check below passed.</summary>
    public bool StructureValidated { get; init; }

    /// <summary>Individual ISO structure checks (boot/efisys, sources\boot.wim, install.wim, setup.exe, UEFI).</summary>
    public List<string> StructureChecks { get; set; } = new();

    /// <summary>Non-null when the build/verification step failed.</summary>
    public string? BuildError { get; set; }
}
