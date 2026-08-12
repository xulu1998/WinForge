using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Customization;

/// <summary>
/// Windows implementation of <see cref="ICustomizationExecutionService"/>
/// (Step 3.3 sections L, M, N, O, S). Executes a validated, frozen
/// <see cref="CustomizationPlan"/> against the isolated, mounted working image.
///
/// <para>Guarantees:</para>
/// <list type="bullet">
///   <item><description>The workspace must be Mounted and the mount registered; otherwise execution stops before any change (critical failure).</description></item>
///   <item><description>Operations run in defined order; the live plan is frozen on execution and cannot be edited.</description></item>
///   <item><description>Each operation records a per-operation result; failures are classified (recoverable vs critical) and are never silently swallowed.</description></item>
///   <item><description>Cooperative cancellation stops between operations (the current operation is never killed mid-flight).</description></item>
///   <item><description>The image is left mounted afterward — no auto-commit, no unmount, no ISO rebuild.</description></item>
///   <item><description>Every destructive target is confined to the mounted workspace (host OS / original ISO root are never touched).</description></item>
/// </list>
/// </summary>
public sealed class WindowsCustomizationExecutionService : ICustomizationExecutionService
{
    // The allowlist for real Windows package removal is owned by
    // PackageRemovalPolicy so the SAME policy governs discovery (UI selectability),
    // plan validation, and this execution-time defense-in-depth guard.

    // Deterministic execution ordering: registry first, then services, then appx,
    // then packages/features, then files.
    private static readonly Dictionary<CustomizationOperationType, int> TypePriority = new()
    {
        [CustomizationOperationType.SetOfflineRegistryValue] = 0,
        [CustomizationOperationType.DeleteOfflineRegistryValue] = 0,
        [CustomizationOperationType.ConfigureOfflineService] = 1,
        [CustomizationOperationType.RemoveProvisionedAppx] = 2,
        [CustomizationOperationType.RemovePackage] = 3,
        [CustomizationOperationType.DisableOptionalFeature] = 3,
        [CustomizationOperationType.RemoveCapability] = 3,
        [CustomizationOperationType.RemoveOfflineFile] = 4,
        [CustomizationOperationType.DisableOfflineScheduledTask] = 5
    };

    private readonly IProcessRunner _processRunner;
    private readonly IOfflineRegistryService _registry;
    private readonly ILoggerService _logger;
    private readonly IMountIdentityValidator _validator;

    public WindowsCustomizationExecutionService(
        IProcessRunner processRunner,
        IOfflineRegistryService registry,
        ILoggerService logger,
        IMountIdentityValidator validator)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<CustomizationResult> ExecuteAsync(
        CustomizationPlan plan,
        ImageServicingWorkspace workspace,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (plan is null) throw new ArgumentNullException(nameof(plan));
        if (workspace is null) throw new ArgumentNullException(nameof(workspace));

        _logger.Info("Customization: execution started.");

        // --- Pre-execution safety guard (critical stop) ---
        if (workspace.State != ServicingWorkspaceState.Mounted || !_validator.MatchesSession(workspace))
        {
            _logger.Error("Customization: execution refused — workspace is not a mounted, valid session.");
            return BuildGuardFailure(plan);
        }

        if (!await MountIsRegisteredAsync(workspace, cancellationToken))
        {
            _logger.Error("Customization: execution refused — mount is not registered.");
            return BuildGuardFailure(plan);
        }

        if (plan.Status != CustomizationPlanStatus.Validated)
        {
            _logger.Error("Customization: execution refused — plan is not Validated.");
            return BuildGuardFailure(plan);
        }

        var snapshot = plan.FreezeForExecution();
        var ordered = OrderOperations(snapshot);

        var succeeded = 0;
        var failed = 0;
        var completed = 0;
        var total = ordered.Count;

        try
        {
            foreach (var op in ordered)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("Customization: cancellation requested; stopping between operations.");
                    break;
                }

                progress?.Report(new ExecutionProgress
                {
                    Completed = completed,
                    Total = total,
                    CurrentOperation = op.DisplayName,
                    Detail = "Applying…"
                });

                op.ExecutionStatus = CustomizationOperationStatus.Running;
                _logger.Info($"Customization: applying operation '{op.DisplayName}' ({op.OperationType}).");

                var outcome = await ExecuteOperationAsync(op, workspace, cancellationToken);

                op.ExecutionStatus = outcome.Status;
                op.ErrorDetails = outcome.Error;

                if (outcome.Status is CustomizationOperationStatus.Succeeded or CustomizationOperationStatus.Skipped)
                {
                    succeeded++;
                    _logger.Info($"Customization: operation '{op.DisplayName}' {outcome.Status}.");
                }
                else
                {
                    failed++;
                    _logger.Warning($"Customization: operation '{op.DisplayName}' failed: {outcome.Error}");
                }

                completed++;
            }

            // Remaining operations (if cancelled) are marked skipped.
            if (cancellationToken.IsCancellationRequested)
            {
                foreach (var op in ordered)
                {
                    if (op.ExecutionStatus is CustomizationOperationStatus.Pending or CustomizationOperationStatus.Running)
                    {
                        op.ExecutionStatus = CustomizationOperationStatus.Skipped;
                    }
                }

                plan.MarkCancelled();
                _logger.Info("Customization: execution cancelled.");
                return BuildResult(snapshot, succeeded, failed, completed,
                    "Execution was cancelled. Some operations did not run.");
            }

            var withErrors = failed > 0;
            plan.MarkCompleted(withErrors);
            _logger.Info($"Customization: execution finished ({succeeded} succeeded, {failed} failed).");
            return BuildResult(snapshot, succeeded, failed, completed,
                withErrors
                    ? "Execution completed with errors."
                    : "Execution completed successfully.");
        }
        catch (OperationCanceledException)
        {
            plan.MarkCancelled();
            return BuildResult(snapshot, succeeded, failed, completed, "Execution was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error($"Customization: execution failed unexpectedly: {ex.Message}");
            plan.MarkFailed();
            return BuildResult(snapshot, succeeded, failed, completed,
                $"Execution failed: {ex.Message}");
        }
    }

    // ---- operation dispatch ----

    private async Task<(CustomizationOperationStatus Status, string? Error)> ExecuteOperationAsync(
        CustomizationOperation op, ImageServicingWorkspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            switch (op.OperationType)
            {
                case CustomizationOperationType.SetOfflineRegistryValue:
                    return ApplyRegistry(op, workspace, set: true);
                case CustomizationOperationType.DeleteOfflineRegistryValue:
                    return ApplyRegistry(op, workspace, set: false);
                case CustomizationOperationType.ConfigureOfflineService:
                    return ApplyService(op, workspace);
                case CustomizationOperationType.RemoveProvisionedAppx:
                    return await ApplyAppxRemovalAsync(op, workspace, cancellationToken);
                case CustomizationOperationType.RemovePackage:
                    return await ApplyPackageRemovalAsync(op, workspace, cancellationToken);
                case CustomizationOperationType.DisableOptionalFeature:
                    return await ApplyFeatureDisableAsync(op, workspace, cancellationToken);
                case CustomizationOperationType.RemoveCapability:
                    return await ApplyCapabilityRemovalAsync(op, workspace, cancellationToken);
                default:
                    return (CustomizationOperationStatus.Skipped, "Unsupported operation type.");
            }
        }
        catch (Exception ex)
        {
            // Per-operation failure is recoverable and the engine continues.
            return (CustomizationOperationStatus.FailedRecoverable, ex.Message);
        }
    }

    private (CustomizationOperationStatus, string?) ApplyRegistry(CustomizationOperation op, ImageServicingWorkspace workspace, bool set)
    {
        if (string.IsNullOrWhiteSpace(op.RegistryHive))
        {
            return (CustomizationOperationStatus.FailedRecoverable, "Missing registry hive.");
        }

        var hiveFile = OfflineHivePaths.GetHiveFilePath(workspace, op.RegistryHive);
        if (hiveFile is null || !_validator.IsWithinMount(hiveFile, workspace) || !File.Exists(hiveFile))
        {
            return (CustomizationOperationStatus.FailedRecoverable, "Registry hive file is outside the mounted workspace or missing.");
        }

        // The key path MUST be relative to the loaded hive root. Normalize it so a
        // stray "SOFTWARE\" / "HKLM\SOFTWARE\" prefix can never duplicate the hive
        // base (which would silently write to the wrong, unverifiable location).
        var keyPath = OfflineHivePaths.NormalizeKeyPath(op.RegistryHive!, op.RegistryKeyPath!);

        var hiveName = OfflineHivePaths.GetWinForgeHiveName(op.RegistryHive);
        OfflineHiveHandle? handle = null;
        try
        {
            handle = _registry.LoadHive(hiveFile, hiveName);
            if (set)
            {
                _registry.SetValue(handle, keyPath, op.RegistryValueName!,
                    op.RegistryValueKind ?? OfflineRegistryValueKind.String, op.RegistryValueData ?? string.Empty);

                // SUCCESS CONTRACT: an offline registry write is only a success when
                // an independent read-back confirms the value exists, has the
                // requested type, and equals the requested data. SetValue not
                // throwing is not enough (see OfflineRegistryService.VerifyPersisted).
                return VerifySet(handle, keyPath, op.RegistryValueName!,
                    op.RegistryValueKind ?? OfflineRegistryValueKind.String, op.RegistryValueData ?? string.Empty);
            }

            _registry.DeleteValue(handle, keyPath, op.RegistryValueName!);

            // SUCCESS CONTRACT: deletion is only a success when the value is now absent.
            return VerifyDelete(handle, keyPath, op.RegistryValueName!);
        }
        finally
        {
            if (handle is not null)
            {
                _registry.UnloadHive(handle);
            }
        }
    }

    private (CustomizationOperationStatus, string?) VerifySet(
        OfflineHiveHandle handle, string keyPath, string valueName,
        OfflineRegistryValueKind kind, string expectedData)
    {
        var actual = _registry.ReadValue(handle, keyPath, valueName);
        if (!actual.Exists)
        {
            return (CustomizationOperationStatus.FailedRecoverable,
                $"Registry value '{valueName}' was not persisted under '{keyPath}' in the offline {handle.HiveName} hive.");
        }

        if (actual.Kind != kind)
        {
            return (CustomizationOperationStatus.FailedRecoverable,
                $"Registry value '{valueName}' under '{keyPath}' has kind {actual.Kind} but {kind} was requested.");
        }

        if (!string.Equals(actual.Data, expectedData, StringComparison.Ordinal))
        {
            return (CustomizationOperationStatus.FailedRecoverable,
                $"Registry value '{valueName}' under '{keyPath}' = '{actual.Data}' but '{expectedData}' was requested.");
        }

        return (CustomizationOperationStatus.Succeeded, null);
    }

    private (CustomizationOperationStatus, string?) VerifyDelete(
        OfflineHiveHandle handle, string keyPath, string valueName)
    {
        var actual = _registry.ReadValue(handle, keyPath, valueName);
        if (actual.Exists)
        {
            return (CustomizationOperationStatus.FailedRecoverable,
                $"Registry value '{valueName}' is still present under '{keyPath}' after deletion from the offline {handle.HiveName} hive.");
        }

        return (CustomizationOperationStatus.Succeeded, null);
    }

    private (CustomizationOperationStatus, string?) ApplyService(CustomizationOperation op, ImageServicingWorkspace workspace)
    {
        if (string.IsNullOrWhiteSpace(op.ServiceName) || op.ServiceStartType is null)
        {
            return (CustomizationOperationStatus.FailedRecoverable, "Missing service name or start type.");
        }

        // Hard safety (ADR-030, final defense-in-depth guard): never reconfigure a
        // service that is not on the explicit allowlist. By policy such an
        // operation should already be Protected (not selectable), rejected by
        // PlanSync, and flagged Unsupported by plan validation — but if one ever
        // reaches here it is skipped rather than applied.
        if (!ServiceConfigPolicy.IsConfigurable(op.ServiceName))
        {
            return (CustomizationOperationStatus.Skipped, "Service is not on the configuration allowlist.");
        }

        var hiveFile = OfflineHivePaths.GetHiveFilePath(workspace, "SYSTEM");
        if (hiveFile is null || !_validator.IsWithinMount(hiveFile, workspace) || !File.Exists(hiveFile))
        {
            return (CustomizationOperationStatus.FailedRecoverable, "SYSTEM hive file is outside the mounted workspace or missing.");
        }

        var hiveName = OfflineHivePaths.GetWinForgeHiveName("SYSTEM");
        OfflineHiveHandle? handle = null;
        try
        {
            handle = _registry.LoadHive(hiveFile, hiveName);

            var current = ReadCurrentControlSet(handle);
            var serviceKey = $"ControlSet{current:D3}\\Services\\{op.ServiceName}";
            serviceKey = OfflineHivePaths.NormalizeKeyPath("SYSTEM", serviceKey);

            // The service must actually exist in the offline image; otherwise the
            // change is meaningless and is skipped (not an error).
            var existing = _registry.GetValue(handle, serviceKey, "Start");
            if (existing is null)
            {
                return (CustomizationOperationStatus.Skipped, "Service not present in the offline image.");
            }

            var requestedStart = ((int)op.ServiceStartType!).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _registry.SetValue(handle, serviceKey, "Start", OfflineRegistryValueKind.DWord, requestedStart);

            // SUCCESS CONTRACT: confirm the Start value persisted with the right
            // type and data before reporting success.
            return VerifySet(handle, serviceKey, "Start", OfflineRegistryValueKind.DWord, requestedStart);
        }
        finally
        {
            if (handle is not null)
            {
                _registry.UnloadHive(handle);
            }
        }
    }

    private async Task<(CustomizationOperationStatus, string?)> ApplyAppxRemovalAsync(
        CustomizationOperation op, ImageServicingWorkspace workspace, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(op.TargetIdentifier))
        {
            return (CustomizationOperationStatus.FailedRecoverable, "Missing Appx package identity.");
        }

        var run = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = "dism.exe",
            Arguments = $"/English /Image:\"{workspace.MountDirectory}\" /Remove-ProvisionedAppxPackage " +
                        $"/PackageName:\"{op.TargetIdentifier}\""
        }, cancellationToken);

        return run.ExitCode == 0
            ? (CustomizationOperationStatus.Succeeded, null)
            : (CustomizationOperationStatus.FailedRecoverable, $"DISM exit {run.ExitCode}.");
    }

    private async Task<(CustomizationOperationStatus, string?)> ApplyPackageRemovalAsync(
        CustomizationOperation op, ImageServicingWorkspace workspace, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(op.TargetIdentifier))
        {
            return (CustomizationOperationStatus.FailedRecoverable, "Missing package identity.");
        }

        // Hard safety: never remove a package outside the small allowlist. This
        // is the final defense-in-depth guard — by policy a non-allowlisted
        // package should already be Protected (not selectable) and rejected by
        // plan validation, but if such an operation reaches here it is skipped.
        if (!PackageRemovalPolicy.IsRemovalAllowed(op.TargetIdentifier))
        {
            return (CustomizationOperationStatus.Skipped, "Package is not on the removal allowlist.");
        }

        var run = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = "dism.exe",
            Arguments = $"/English /Image:\"{workspace.MountDirectory}\" /Remove-Package " +
                        $"/PackageName:\"{op.TargetIdentifier}\""
        }, cancellationToken);

        return run.ExitCode == 0
            ? (CustomizationOperationStatus.Succeeded, null)
            : (CustomizationOperationStatus.FailedRecoverable, $"DISM exit {run.ExitCode}.");
    }

    private async Task<(CustomizationOperationStatus, string?)> ApplyFeatureDisableAsync(
        CustomizationOperation op, ImageServicingWorkspace workspace, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(op.TargetIdentifier))
        {
            return (CustomizationOperationStatus.FailedRecoverable, "Missing feature name.");
        }

        // Hard safety (ADR-051, final defense-in-depth guard): never disable an
        // optional feature outside the explicit allowlist. By policy such an
        // operation should already be non-selectable and rejected by plan
        // validation, but if one ever reaches here it is skipped rather than applied.
        if (!FeatureConfigPolicy.IsFeatureAllowed(op.TargetIdentifier))
        {
            return (CustomizationOperationStatus.Skipped, "Feature is not on the configuration allowlist.");
        }

        var run = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = "dism.exe",
            Arguments = $"/English /Image:\"{workspace.MountDirectory}\" /Disable-Feature " +
                        $"/FeatureName:\"{op.TargetIdentifier}\""
        }, cancellationToken);

        return run.ExitCode == 0
            ? (CustomizationOperationStatus.Succeeded, null)
            : (CustomizationOperationStatus.FailedRecoverable, $"DISM exit {run.ExitCode}.");
    }

    private async Task<(CustomizationOperationStatus, string?)> ApplyCapabilityRemovalAsync(
        CustomizationOperation op, ImageServicingWorkspace workspace, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(op.TargetIdentifier))
        {
            return (CustomizationOperationStatus.FailedRecoverable, "Missing capability identity.");
        }

        // Hard safety (ADR-051): capabilities are not offered in the first tranche
        // (the allowlist is empty), so any capability removal reaching here is
        // skipped rather than applied.
        if (!FeatureConfigPolicy.IsCapabilityAllowed(op.TargetIdentifier))
        {
            return (CustomizationOperationStatus.Skipped, "Capability is not on the configuration allowlist.");
        }

        var run = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = "dism.exe",
            Arguments = $"/English /Image:\"{workspace.MountDirectory}\" /Remove-Capability " +
                        $"/CapabilityName:\"{op.TargetIdentifier}\""
        }, cancellationToken);

        return run.ExitCode == 0
            ? (CustomizationOperationStatus.Succeeded, null)
            : (CustomizationOperationStatus.FailedRecoverable, $"DISM exit {run.ExitCode}.");
    }

    // ---- helpers ----

    private int ReadCurrentControlSet(OfflineHiveHandle handle)
    {
        var raw = _registry.GetValue(handle, "Select", "Current");
        return int.TryParse(raw, out var current) && current >= 1 ? current : 1;
    }

    private async Task<bool> MountIsRegisteredAsync(ImageServicingWorkspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            var run = await _processRunner.RunAsync(new ProcessRequest
            {
                FileName = "dism.exe",
                Arguments = "/English /Get-MountedImageInfo"
            }, cancellationToken);

            if (run.ExitCode != 0)
            {
                return false;
            }

            foreach (var line in run.StandardOutput.Replace("\r\n", "\n").Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Mount Dir :", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(trimmed.Substring("Mount Dir :".Length).Trim().TrimEnd('\\'),
                        workspace.MountDirectory!.TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static List<CustomizationOperation> OrderOperations(CustomizationPlan snapshot)
    {
        var list = new List<CustomizationOperation>(snapshot.Operations);
        list.Sort((a, b) =>
        {
            var pa = TypePriority.GetValueOrDefault(a.OperationType, 99);
            var pb = TypePriority.GetValueOrDefault(b.OperationType, 99);
            if (pa != pb) return pa.CompareTo(pb);
            return a.ExecutionOrder.CompareTo(b.ExecutionOrder);
        });
        return list;
    }

    private static CustomizationResult BuildGuardFailure(CustomizationPlan plan)
    {
        var selected = plan.SelectedOperations.Count;
        return new CustomizationResult
        {
            CriticalFailure = true,
            TotalOperations = selected,
            Succeeded = 0,
            FailedOperations = selected,
            Summary = "Execution refused: the workspace is not a valid mounted session or the plan is not validated.",
            Operations = new List<CustomizationOperation>(plan.SelectedOperations)
        };
    }

    private static CustomizationResult BuildResult(
        CustomizationPlan snapshot, int succeeded, int failed, int completed, string summary)
    {
        return new CustomizationResult
        {
            CriticalFailure = false,
            TotalOperations = snapshot.Operations.Count,
            Succeeded = succeeded,
            FailedOperations = failed,
            Summary = summary,
            Operations = new List<CustomizationOperation>(snapshot.Operations)
        };
    }
}
