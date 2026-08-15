using System.Collections.Generic;

namespace WinForge.Core.Profiles;

// =====================================================================
// Phase 15 Stage 15.4 — REAL OFFLINE APPLY VALIDATION (ADR-097)
//
// Report/schema models for `--apply-profile`: proof that profile-generated
// BuildPlans do not merely validate structurally but actually EXECUTE safely
// against a real mounted Windows image (selected operations only) and that the
// result is INDEPENDENTLY READ BACK (never trusting a command exit code alone).
// =====================================================================

/// <summary>
/// Independent read-back classification for one applied operation.
/// </summary>
public enum ApplyVerificationStatus
{
    /// <summary>Nothing was applied (pre-check already satisfied / executor skipped / never attempted).</summary>
    NotApplicable = 0,

    /// <summary>Independent read-back confirmed the requested final state on the mounted image.</summary>
    Verified,

    /// <summary>The target was already in the requested state BEFORE execution — deterministically skipped, nothing applied.</summary>
    AlreadySatisfied,

    /// <summary>Execution reported success but independent read-back could NOT confirm the final state.</summary>
    VerificationFailed,
}

/// <summary>Per-operation entry in the apply-validation report (spec §3).</summary>
public sealed class ProfileApplyOperationReport
{
    /// <summary>Plan canonical operation key (ConflictKey: svc:|opt:|feat:|pkg:…).</summary>
    public string CanonicalKey { get; init; } = string.Empty;

    public string OperationType { get; init; } = string.Empty;

    /// <summary>The requested executable state (Remove / Disable / Configure) — what this operation was supposed to do.</summary>
    public string ExpectedAction { get; init; } = string.Empty;

    /// <summary>Final execution status from the execution engine (Succeeded / FailedRecoverable / FailedCritical / Skipped / Pending).</summary>
    public string ExecutionStatus { get; init; } = string.Empty;

    public string VerificationStatus { get; init; } = string.Empty;

    /// <summary>Human-readable verification evidence (exact feature state returned, hive path read, DISM output, …).</summary>
    public string VerificationDetail { get; init; } = string.Empty;
}

/// <summary>Mount/workspace cleanup outcome (spec §3 mountCleanup).</summary>
public sealed class ProfileApplyMountCleanupReport
{
    public bool DiscardSucceeded { get; init; }
    public bool WorkspaceCleanupSucceeded { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// The deterministic `profile-apply-validation.json` schema (spec §3). One report
/// per `--apply-profile` invocation.
/// </summary>
public sealed class ProfileApplyValidationReport
{
    public string ProfileId { get; init; } = string.Empty;

    /// <summary>Total BuildPlan operations (candidates, including present-unselected Recommend rows).</summary>
    public int BuildPlanOperationCount { get; init; }

    /// <summary>AutoApply operations actually selected for execution.</summary>
    public int SelectedOperationCount { get; init; }

    /// <summary>Operations handed to the executor (selected minus deterministic already-satisfied skips).</summary>
    public int Attempted { get; init; }

    /// <summary>Attempted operations that executed AND passed independent read-back.</summary>
    public int Succeeded { get; init; }

    /// <summary>Attempted operations that failed execution or failed read-back verification.</summary>
    public int Failed { get; init; }

    /// <summary>Selected operations deterministically skipped (already-satisfied pre-check / executor skip).</summary>
    public int Skipped { get; init; }

    /// <summary>
    /// True only when every attempted operation succeeded+verified and no
    /// verification failed. Mount cleanup is reported separately — a failed
    /// cleanup is a BLOCKER that stops further profile validation.
    /// </summary>
    public bool ValidationPassed { get; init; }

    public List<ProfileApplyOperationReport> Operations { get; init; } = new();

    /// <summary>Set by the CLI after cleanup (cleanup always runs; a failed discard is a blocker).</summary>
    public ProfileApplyMountCleanupReport MountCleanup { get; set; } = new();
}
