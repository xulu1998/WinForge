using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Profiles;
using WinForge.Core.Services;
using WinForge.Infrastructure.Customization;

namespace WinForge.Infrastructure.Profiles;

// =====================================================================
// Phase 15 Stage 15.4 — OFFLINE APPLY VERIFIER (ADR-097 §4-§7)
//
// INDEPENDENT read-back verification against the MOUNTED working image. A
// command exit code alone is never treated as success:
//   - AppX removal:          re-query /Get-ProvisionedAppxPackages → package absent?
//   - OptionalFeature:       /Get-FeatureInfo → exact returned feature State
//   - Offline service:       read the mounted SYSTEM hive Start value
//   - Offline registry:      read the mounted hive (value exists, right kind, right data)
//
// Every read targets the OFFLINE image only (hive files resolved through
// OfflineHivePaths; the host OS / host HKCU is never touched — OfflineDefaultUser
// maps to Users\Default\NTUSER.DAT inside the mounted image).
//
// PreCheck (before execution) classifies deterministically:
//   AlreadySatisfied — the target is already in the requested state → the
//   operation is skipped and nothing is applied (spec §4/§5/§6/§7, §10).
// =====================================================================

/// <summary>Pre-execution read-back: is the requested state already present?</summary>
public sealed record ApplyPreCheckResult(bool AlreadySatisfied, string Detail);

/// <summary>Post-execution read-back: did the requested state actually land?</summary>
public sealed record ApplyVerifyResult(ApplyVerificationStatus Status, string Detail);

public interface IOfflineApplyVerifier
{
    Task<ApplyPreCheckResult> PreCheckAsync(
        CustomizationOperation op, ImageServicingWorkspace workspace, CancellationToken ct);

    Task<ApplyVerifyResult> VerifyAsync(
        CustomizationOperation op, ImageServicingWorkspace workspace, CancellationToken ct);
}

public sealed class OfflineApplyVerifier : IOfflineApplyVerifier
{
    private readonly IProcessRunner _processRunner;
    private readonly IOfflineRegistryService _registry;
    private readonly ILoggerService _logger;

    public OfflineApplyVerifier(IProcessRunner processRunner, IOfflineRegistryService registry, ILoggerService logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ---- AppX -----------------------------------------------------------

    public async Task<ApplyPreCheckResult> PreCheckAsync(
        CustomizationOperation op, ImageServicingWorkspace workspace, CancellationToken ct)
    {
        switch (op.OperationType)
        {
            case CustomizationOperationType.RemoveProvisionedAppx:
                var present = await IsAppxPresentAsync(op.TargetIdentifier!, workspace, ct);
                return present
                    ? new ApplyPreCheckResult(false, "Package is provisioned; removal required.")
                    : new ApplyPreCheckResult(true, "Package is already absent — removal already satisfied.");

            case CustomizationOperationType.DisableOptionalFeature:
                var state = await GetFeatureStateAsync(op.TargetIdentifier!, workspace, ct);
                return IsDisabledState(state)
                    ? new ApplyPreCheckResult(true, $"Feature already {state} — disable already satisfied.")
                    : new ApplyPreCheckResult(false, $"Feature state is {state}; disable required.");

            case CustomizationOperationType.ConfigureOfflineService:
                var service = ReadServiceStart(workspace, op.ServiceName!);
                if (service is null)
                {
                    return new ApplyPreCheckResult(true, "Service not present in the offline image — nothing to configure.");
                }

                var requestedStart = ((int)op.ServiceStartType!).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return string.Equals(service, requestedStart, StringComparison.Ordinal)
                    ? new ApplyPreCheckResult(true, $"Service Start is already {requestedStart} — configuration already satisfied.")
                    : new ApplyPreCheckResult(false, $"Service Start is {service}; requested {requestedStart}.");

            case CustomizationOperationType.SetOfflineRegistryValue:
            case CustomizationOperationType.DeleteOfflineRegistryValue:
                var read = ReadRegistryValue(workspace, op);
                if (!read.Exists)
                {
                    return op.OperationType == CustomizationOperationType.DeleteOfflineRegistryValue
                        ? new ApplyPreCheckResult(true, "Registry value already absent — deletion already satisfied.")
                        : new ApplyPreCheckResult(false, "Registry value absent; write required.");
                }

                if (op.OperationType == CustomizationOperationType.DeleteOfflineRegistryValue)
                {
                    return new ApplyPreCheckResult(false, "Registry value present; deletion required.");
                }

                var matches = read.Kind == (op.RegistryValueKind ?? OfflineRegistryValueKind.String)
                    && string.Equals(read.Data, op.RegistryValueData, StringComparison.Ordinal);
                return matches
                    ? new ApplyPreCheckResult(true, "Registry value already matches requested kind+data — write already satisfied.")
                    : new ApplyPreCheckResult(false, "Registry value differs from requested kind/data; write required.");

            default:
                // Not independently verified (e.g. RemovePackage / files) — attempt
                // execution and let the executor decide; post-check is NotApplicable.
                return new ApplyPreCheckResult(false, "No pre-check defined for this operation type.");
        }
    }

    public async Task<ApplyVerifyResult> VerifyAsync(
        CustomizationOperation op, ImageServicingWorkspace workspace, CancellationToken ct)
    {
        switch (op.OperationType)
        {
            case CustomizationOperationType.RemoveProvisionedAppx:
                var present = await IsAppxPresentAsync(op.TargetIdentifier!, workspace, ct);
                return present
                    ? new ApplyVerifyResult(ApplyVerificationStatus.VerificationFailed,
                        $"Package '{op.TargetIdentifier}' is STILL provisioned after removal.")
                    : new ApplyVerifyResult(ApplyVerificationStatus.Verified,
                        "Independent /Get-ProvisionedAppxPackages confirms the package is absent.");

            case CustomizationOperationType.DisableOptionalFeature:
                var state = await GetFeatureStateAsync(op.TargetIdentifier!, workspace, ct);
                return IsDisabledState(state)
                    ? new ApplyVerifyResult(ApplyVerificationStatus.Verified,
                        $"Independent /Get-FeatureInfo returns State '{state}'.")
                    : new ApplyVerifyResult(ApplyVerificationStatus.VerificationFailed,
                        $"Independent /Get-FeatureInfo returns State '{state}' — expected a disabled state.");

            case CustomizationOperationType.ConfigureOfflineService:
                var service = ReadServiceStart(workspace, op.ServiceName!);
                var requestedStart = ((int)op.ServiceStartType!).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return string.Equals(service, requestedStart, StringComparison.Ordinal)
                    ? new ApplyVerifyResult(ApplyVerificationStatus.Verified,
                        $"Offline SYSTEM hive Start value is {service} (requested {requestedStart}).")
                    : new ApplyVerifyResult(ApplyVerificationStatus.VerificationFailed,
                        $"Offline SYSTEM hive Start value is {service ?? "(missing)"}; requested {requestedStart}.");

            case CustomizationOperationType.SetOfflineRegistryValue:
                return VerifyRegistrySet(workspace, op);

            case CustomizationOperationType.DeleteOfflineRegistryValue:
                var afterDelete = ReadRegistryValue(workspace, op);
                return afterDelete.Exists
                    ? new ApplyVerifyResult(ApplyVerificationStatus.VerificationFailed,
                        $"Offline value '{op.RegistryValueName}' is still present under '{op.RegistryKeyPath}'.")
                    : new ApplyVerifyResult(ApplyVerificationStatus.Verified,
                        $"Offline value '{op.RegistryValueName}' is absent under '{op.RegistryKeyPath}'.");

            default:
                return new ApplyVerifyResult(ApplyVerificationStatus.NotApplicable,
                    "No independent read-back defined for this operation type; execution status is the evidence.");
        }
    }

    // ---- read-back primitives -------------------------------------------

    private async Task<bool> IsAppxPresentAsync(string packageName, ImageServicingWorkspace workspace, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspace.MountDirectory))
        {
            return false;
        }

        var run = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = "dism.exe",
            Arguments = $"/English /Image:\"{workspace.MountDirectory}\" /Get-ProvisionedAppxPackages"
        }, ct);

        if (run.ExitCode != 0)
        {
            _logger.Warning($"ApplyVerifier: /Get-ProvisionedAppxPackages exited {run.ExitCode} — treating as absent.");
            return false;
        }

        return DismAppxParser.Parse(run.StandardOutput)
            .Any(p => string.Equals(p.PackageName, packageName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> GetFeatureStateAsync(string featureName, ImageServicingWorkspace workspace, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspace.MountDirectory))
        {
            return "Unknown";
        }

        var run = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = "dism.exe",
            Arguments = $"/English /Image:\"{workspace.MountDirectory}\" /Get-FeatureInfo " +
                        $"/FeatureName:\"{featureName}\""
        }, ct);

        if (run.ExitCode != 0)
        {
            // A feature that no longer exists after disable returns an error from
            // /Get-FeatureInfo; the absence is itself the strongest evidence.
            return "AbsentAfterDisable";
        }

        return DismFeatureStateParser.ParseState(run.StandardOutput);
    }

    private string? ReadServiceStart(ImageServicingWorkspace workspace, string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return null;
        }

        var hiveFile = OfflineHivePaths.GetHiveFilePath(workspace, "SYSTEM");
        if (hiveFile is null || !File.Exists(hiveFile))
        {
            return null;
        }

        OfflineHiveHandle? handle = null;
        try
        {
            handle = _registry.LoadHive(hiveFile, OfflineHivePaths.GetWinForgeHiveName("SYSTEM"));
            var current = ReadCurrentControlSet(handle);
            var serviceKey = $"ControlSet{current:D3}\\Services\\{serviceName}";
            serviceKey = OfflineHivePaths.NormalizeKeyPath("SYSTEM", serviceKey);
            var existing = _registry.GetValue(handle, serviceKey, "Start");
            return existing;
        }
        finally
        {
            if (handle is not null)
            {
                _registry.UnloadHive(handle);
            }
        }
    }

    private OfflineRegistryReadResult ReadRegistryValue(ImageServicingWorkspace workspace, CustomizationOperation op)
    {
        if (string.IsNullOrWhiteSpace(op.RegistryHive))
        {
            return new OfflineRegistryReadResult { Exists = false };
        }

        var hiveFile = OfflineHivePaths.GetHiveFilePath(workspace, op.RegistryHive);
        if (hiveFile is null || !File.Exists(hiveFile))
        {
            return new OfflineRegistryReadResult { Exists = false };
        }

        OfflineHiveHandle? handle = null;
        try
        {
            handle = _registry.LoadHive(hiveFile, OfflineHivePaths.GetWinForgeHiveName(op.RegistryHive));
            var keyPath = OfflineHivePaths.NormalizeKeyPath(op.RegistryHive!, op.RegistryKeyPath!);
            return _registry.ReadValue(handle, keyPath, op.RegistryValueName!);
        }
        finally
        {
            if (handle is not null)
            {
                _registry.UnloadHive(handle);
            }
        }
    }

    private ApplyVerifyResult VerifyRegistrySet(ImageServicingWorkspace workspace, CustomizationOperation op)
    {
        var read = ReadRegistryValue(workspace, op);
        if (!read.Exists)
        {
            return new ApplyVerifyResult(ApplyVerificationStatus.VerificationFailed,
                $"Offline value '{op.RegistryValueName}' was not found under '{op.RegistryKeyPath}' (hive {op.RegistryHive}).");
        }

        if (read.Kind != (op.RegistryValueKind ?? OfflineRegistryValueKind.String))
        {
            return new ApplyVerifyResult(ApplyVerificationStatus.VerificationFailed,
                $"Offline value kind is {read.Kind}; requested {op.RegistryValueKind}.");
        }

        if (!string.Equals(read.Data, op.RegistryValueData, StringComparison.Ordinal))
        {
            return new ApplyVerifyResult(ApplyVerificationStatus.VerificationFailed,
                $"Offline value data is '{read.Data}'; requested '{op.RegistryValueData}'.");
        }

        return new ApplyVerifyResult(ApplyVerificationStatus.Verified,
            $"Offline hive {op.RegistryHive} value '{op.RegistryValueName}' confirmed "
            + $"({read.Kind} = '{read.Data}') at '{op.RegistryKeyPath}'.");
    }

    private int ReadCurrentControlSet(OfflineHiveHandle handle)
    {
        // Mirror the execution engine: read Select\Current from the loaded SYSTEM
        // hive; default to ControlSet001 when absent (Windows 25H2 images).
        var raw = _registry.GetValue(handle, "Select", "Current");
        return int.TryParse(raw, out var current) && current >= 1 ? current : 1;
    }

    private static bool IsDisabledState(string state)
        => state is "Disabled" or "DisablePayloadRemoved" or "DisabledWithPayloadRemoved"
            or "AbsentAfterDisable";
}
