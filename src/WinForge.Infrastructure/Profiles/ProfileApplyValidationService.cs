using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Profiles;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Profiles;

// =====================================================================
// Phase 15 Stage 15.4 — PROFILE APPLY VALIDATION SERVICE (ADR-097)
//
// Proves that a profile-generated BuildPlan EXECUTES safely against a real
// mounted image and that the result is INDEPENDENTLY READ BACK:
//
//   1. guard: the workspace is a mounted, session-matching WinForge workspace
//   2. pre-check every SELECTED operation → deterministic already-satisfied
//      skips (nothing applied for them; they are deselected first)
//   3. execute ONLY the remaining SelectedOperations (Recommend/unselected rows
//      are never executed)
//   4. independently verify every succeeded operation by read-back (AppX /
//      feature / service / registry)
//   5. deterministic counts + per-operation report (spec §3)
//
// Failure handling (§11): per-operation failures are recorded exactly, the run
// continues only where safe, and the profile is NOT reported as successful when
// any operation failed or failed verification. Mount/workspace cleanup is a
// separate concern handled by the caller (a failed cleanup is a BLOCKER).
// =====================================================================

public sealed class ProfileApplyValidationRequest
{
    public required ProfileDefinition Profile { get; init; }
    public required CustomizationPlan Plan { get; init; }
    public required ImageServicingWorkspace Workspace { get; init; }
    public required ICustomizationExecutionService Executor { get; init; }
    public required IOfflineApplyVerifier Verifier { get; init; }
    public required IMountIdentityValidator Validator { get; init; }
    public ILoggerService? Logger { get; init; }
}

public interface IProfileApplyValidationService
{
    Task<ProfileApplyValidationReport> ValidateAsync(
        ProfileApplyValidationRequest request, CancellationToken cancellationToken = default);
}

public sealed class ProfileApplyValidationService : IProfileApplyValidationService
{
    public async Task<ProfileApplyValidationReport> ValidateAsync(
        ProfileApplyValidationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        ArgumentNullException.ThrowIfNull(request.Workspace);

        var logger = request.Logger;
        var plan = request.Plan;
        var report = new ProfileApplyValidationReport { ProfileId = request.Profile.Id };

        // ---- 1. Workspace ownership guard (ADR-097 §10) ----
        var workspaceOwned = request.Workspace.State == ServicingWorkspaceState.Mounted
            && request.Validator.MatchesSession(request.Workspace);
        if (!workspaceOwned)
        {
            logger?.Error("ApplyValidation: refused — workspace is not a mounted, session-matching WinForge workspace.");
            return new ProfileApplyValidationReport
            {
                ProfileId = request.Profile.Id,
                BuildPlanOperationCount = plan.Operations.Count,
                SelectedOperationCount = plan.SelectedOperations.Count,
                Attempted = 0,
                Succeeded = 0,
                Failed = plan.SelectedOperations.Count,
                Skipped = 0,
                ValidationPassed = false,
                Operations = plan.SelectedOperations
                    .Select(op => new ProfileApplyOperationReport
                    {
                        CanonicalKey = op.ConflictKey,
                        OperationType = op.OperationType.ToString(),
                        ExpectedAction = ExpectedActionOf(op),
                        ExecutionStatus = CustomizationOperationStatus.Pending.ToString(),
                        VerificationStatus = ApplyVerificationStatus.NotApplicable.ToString(),
                        VerificationDetail = "Workspace ownership guard failed — no operation was executed.",
                    })
                    .ToList(),
            };
        }

        if (plan.Status != CustomizationPlanStatus.Validated)
        {
            logger?.Error($"ApplyValidation: refused — plan is not Validated (status {plan.Status}).");
            return new ProfileApplyValidationReport
            {
                ProfileId = request.Profile.Id,
                BuildPlanOperationCount = plan.Operations.Count,
                SelectedOperationCount = plan.SelectedOperations.Count,
                Attempted = 0,
                Succeeded = 0,
                Failed = plan.SelectedOperations.Count,
                Skipped = 0,
                ValidationPassed = false,
                Operations = plan.SelectedOperations
                    .Select(op => new ProfileApplyOperationReport
                    {
                        CanonicalKey = op.ConflictKey,
                        OperationType = op.OperationType.ToString(),
                        ExpectedAction = ExpectedActionOf(op),
                        ExecutionStatus = CustomizationOperationStatus.Pending.ToString(),
                        VerificationStatus = ApplyVerificationStatus.NotApplicable.ToString(),
                        VerificationDetail = $"Plan not validated (status {plan.Status}) — nothing was executed.",
                    })
                    .ToList(),
            };
        }

        // ---- 2. Counts (spec §3) ----
        var buildPlanCount = plan.Operations.Count;
        var selectedCount = plan.SelectedOperations.Count;

        // ---- 3. Deterministic already-satisfied pre-check (deselect + skip) ----
        var skipped = 0;
        var skipDetails = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var op in plan.Operations.Where(o => o.IsSelected).ToList())
        {
            var pre = await request.Verifier.PreCheckAsync(op, request.Workspace, cancellationToken);
            if (pre.AlreadySatisfied)
            {
                plan.SetSelected(op.OperationId, false);
                skipDetails[op.OperationId] = pre.Detail;
                skipped++;
                logger?.Info($"ApplyValidation: '{op.ConflictKey}' already satisfied — skipped: {pre.Detail}");
            }
        }

        var attempted = plan.SelectedOperations.Count;
        logger?.Info($"ApplyValidation: {selectedCount} selected, {skipped} already-satisfied, {attempted} to execute.");

        // ---- 4. Execute ONLY selected operations ----
        if (attempted > 0)
        {
            await request.Executor.ExecuteAsync(plan, request.Workspace, cancellationToken: cancellationToken);
        }

        // ---- 5. Independent read-back verification + deterministic counts ----
        var succeeded = 0;
        var failed = 0;
        var operations = new List<ProfileApplyOperationReport>();

        foreach (var op in plan.Operations)
        {
            // Skipped (already-satisfied) rows — nothing was applied.
            if (skipDetails.TryGetValue(op.OperationId, out var skipDetail))
            {
                operations.Add(new ProfileApplyOperationReport
                {
                    CanonicalKey = op.ConflictKey,
                    OperationType = op.OperationType.ToString(),
                    ExpectedAction = ExpectedActionOf(op),
                    ExecutionStatus = CustomizationOperationStatus.Skipped.ToString(),
                    VerificationStatus = ApplyVerificationStatus.AlreadySatisfied.ToString(),
                    VerificationDetail = skipDetail,
                });
                continue;
            }

            // Unselected rows (Recommend-only candidates) — never executed.
            if (!op.IsSelected)
            {
                continue;
            }

            var executionStatus = op.ExecutionStatus;
            ApplyVerificationStatus verification;
            string detail;
            switch (executionStatus)
            {
                case CustomizationOperationStatus.Succeeded:
                    var verify = await request.Verifier.VerifyAsync(op, request.Workspace, cancellationToken);
                    verification = verify.Status;
                    detail = verify.Detail;
                    if (verification == ApplyVerificationStatus.Verified)
                    {
                        succeeded++;
                    }
                    else
                    {
                        failed++;
                    }

                    break;

                case CustomizationOperationStatus.Skipped:
                    skipped++;
                    verification = ApplyVerificationStatus.NotApplicable;
                    detail = string.IsNullOrWhiteSpace(op.ErrorDetails)
                        ? "Execution engine skipped the operation."
                        : op.ErrorDetails;
                    break;

                case CustomizationOperationStatus.Pending:
                    // The executor never touched this operation (guard failure /
                    // critical stop before it ran). Never report success.
                    failed++;
                    verification = ApplyVerificationStatus.NotApplicable;
                    detail = "Operation was never executed (execution did not reach it).";
                    break;

                default:
                    failed++;
                    verification = ApplyVerificationStatus.NotApplicable;
                    detail = string.IsNullOrWhiteSpace(op.ErrorDetails)
                        ? $"Execution failed ({executionStatus})."
                        : op.ErrorDetails;
                    break;
            }

            operations.Add(new ProfileApplyOperationReport
            {
                CanonicalKey = op.ConflictKey,
                OperationType = op.OperationType.ToString(),
                ExpectedAction = ExpectedActionOf(op),
                ExecutionStatus = executionStatus.ToString(),
                VerificationStatus = verification.ToString(),
                VerificationDetail = detail,
            });
        }

        return new ProfileApplyValidationReport
        {
            ProfileId = request.Profile.Id,
            BuildPlanOperationCount = buildPlanCount,
            SelectedOperationCount = selectedCount,
            Attempted = attempted,
            Succeeded = succeeded,
            Failed = failed,
            Skipped = skipped,
            ValidationPassed = failed == 0,
            Operations = operations,
        };
    }

    private static string ExpectedActionOf(CustomizationOperation op)
        => op.ActionKind?.ToString() ?? op.OperationType switch
        {
            CustomizationOperationType.RemoveProvisionedAppx => "Remove",
            CustomizationOperationType.DisableOptionalFeature => "Disable",
            CustomizationOperationType.ConfigureOfflineService => "Configure",
            CustomizationOperationType.SetOfflineRegistryValue => "Set",
            CustomizationOperationType.DeleteOfflineRegistryValue => "Delete",
            _ => op.OperationType.ToString(),
        };
}
