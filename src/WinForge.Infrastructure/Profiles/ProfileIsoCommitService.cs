using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Profiles;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Profiles;

/// <summary>
/// Orchestrates the Phase 16 Stage 16.1 COMMIT/BUILD validation mode: after a
/// successful selected-only apply + read-back, and ONLY through the explicit
/// commit path, this service
///   1. re-validates the pre-commit gate (every attempted op must be Verified),
///   2. verifies commit-mode workspace ownership (session-owned paths + the
///      AUTHORITATIVE DISM mount inventory — an unknown mount aborts the run),
///   3. builds the final bootable ISO through the PRODUCTION pipeline
///      (ImageBuildService: commit → export → media preparation → oscdimg →
///      independent ISO verification → atomic rename),
///   4. re-opens the COMMITTED working WIM and independently re-verifies that
///      representative changes persisted (AppX absence / machine registry /
///      Default-User registry / metadata query),
///   5. validates the produced ISO structure (boot files, sources\boot.wim,
///      install.wim, setup.exe) and records output metadata (path, size,
///      streaming SHA-256).
///
/// The source ISO is NEVER written; the output ISO is written only to the
/// user-chosen output directory; commit intent is explicit in the caller (the
/// CLI has a distinct --commit-profile mode — there is no accidental transition
/// from the discard-only apply path).
/// </summary>
public sealed class ProfileIsoCommitService
{
    private readonly IBuildService _build;
    private readonly IOfflineApplyVerifier _verifier;
    private readonly IMountIdentityValidator _validator;
    private readonly IImageServicingService _servicing;
    private readonly IIsoMountService _isoMount;
    private readonly IProcessRunner _processRunner;
    private readonly ILoggerService _logger;

    public ProfileIsoCommitService(
        IBuildService build,
        IOfflineApplyVerifier verifier,
        IMountIdentityValidator validator,
        IImageServicingService servicing,
        IIsoMountService isoMount,
        IProcessRunner processRunner,
        ILoggerService logger)
    {
        _build = build ?? throw new ArgumentNullException(nameof(build));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _servicing = servicing ?? throw new ArgumentNullException(nameof(servicing));
        _isoMount = isoMount ?? throw new ArgumentNullException(nameof(isoMount));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ProfileCommitValidationReport> CommitAsync(
        ProfileIsoCommitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Workspace);

        var report = new ProfileCommitValidationReport
        {
            ProfileId = request.Profile.Id,
            SourceIsoPath = request.SourceIsoPath,
            SourceMediaIdentity = BuildMediaIdentity(request.SourceIsoPath, request.SourceIsoSizeBytes),
            SelectedIndex = request.Workspace.SelectedIndex,
            EditionName = request.Workspace.SelectedEditionName ?? request.SourceEditionName ?? string.Empty,
            BuildPlanOperationCount = request.ApplyReport.BuildPlanOperationCount,
            SelectedOperationCount = request.ApplyReport.SelectedOperationCount,
            Attempted = request.ApplyReport.Attempted,
            Succeeded = request.ApplyReport.Succeeded,
            Failed = request.ApplyReport.Failed,
            Skipped = request.ApplyReport.Skipped,
            PreCommitValidationPassed = request.ApplyReport.ValidationPassed,
            Operations = new List<ProfileApplyOperationReport>(request.ApplyReport.Operations),
        };

        // ---- 1. Pre-commit gate: every attempted operation must be read-back Verified. ----
        if (!report.PreCommitValidationPassed || report.Failed > 0)
        {
            report.PreCommitGateFailure = BuildGateFailure(report);
            _logger.Error($"Commit: pre-commit gate REJECTED (profile {request.Profile.Id}) — nothing will be committed.");
            return report;
        }

        // ---- 2. Commit-mode ownership guard ----
        if (!_validator.MatchesSession(request.Workspace))
        {
            report.PreCommitGateFailure = "Commit-mode ownership guard failed: the workspace (mount dir / working image) " +
                                          "is not contained in the session workspace root — refusing to commit.";
            _logger.Error("Commit: workspace ownership guard failed; refusing to commit.");
            return report;
        }

        var inventory = await AuthoritativeMountInventoryAsync(cancellationToken);
        var owned = request.Workspace.MountDirectory ?? string.Empty;
        var other = inventory.Where(m => !string.Equals(m, owned, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!inventory.Contains(owned, StringComparer.OrdinalIgnoreCase))
        {
            report.PreCommitGateFailure = "Commit-mode ownership guard failed: the working mount is NOT registered in the " +
                                          "authoritative DISM mount inventory — refusing to commit.";
            _logger.Error("Commit: working mount not registered in DISM inventory; refusing to commit.");
            return report;
        }

        if (other.Count > 0)
        {
            report.PreCommitGateFailure = "Commit-mode ownership guard failed: an UNKNOWN mount is registered with DISM " +
                                          $"({string.Join(", ", other)}) — refusing to commit an unrelated image.";
            _logger.Error($"Commit: unknown mounts registered ({string.Join(", ", other)}); refusing to commit.");
            return report;
        }

        // ---- 3. Production build pipeline (commit → export → media → oscdimg → verify) ----
        var buildWs = Path.Combine(request.Workspace.WorkingDirectory ?? string.Empty, "build");
        var build = await _build.BuildAsync(new BuildRequest
        {
            SourceIsoPath = request.SourceIsoPath,
            SourceImageRelativePath = request.SourceImageRelativePath,
            SourceImageType = request.SourceImageType,
            WorkingImagePath = request.Workspace.WorkingImagePath ?? string.Empty,
            MountDirectory = request.Workspace.MountDirectory ?? string.Empty,
            WorkingIndex = 1,
            SourceEditionName = request.SourceEditionName,
            FinalEditionName = request.SourceEditionName,
            OutputDirectory = request.OutputDirectory,
            OutputFileName = request.OutputFileName,
            Mode = BuildMode.SingleCustomizedEdition,
            OverwritePolicy = BuildOverwritePolicy.Fail,
            BuildWorkspaceDirectory = buildWs,
        }, null, cancellationToken);

        if (!build.Success)
        {
            report.Committed = false;
            report.CommitError = build.ErrorMessage ?? "ISO build failed (see BuildResult log).";
            _logger.Error($"Commit: build failed ({build.FailedPhase}): {report.CommitError}");
            return report;
        }

        // BuildAsync committed the owned mount (CommitUnmountAsync /Commit). The
        // committed WIM is retained at the workspace working-image path.
        report.Committed = true;
        _logger.Info($"Commit: working image committed; ISO produced at {build.OutputPath}.");

        // ---- 4. Post-commit: re-open the COMMITTED WIM and re-verify persistence ----
        var (postVerified, postError, checks, readable) = await VerifyCommittedImageAsync(
            request, request.Workspace.WorkingImagePath ?? string.Empty, cancellationToken);
        report.PostCommitVerified = postVerified;
        report.PostCommitError = postError;
        report.PostCommitChecks = checks;
        report.CommittedImageReadable = readable;

        // ---- 5. ISO structure validation + metadata ----
        var isoPath = build.OutputPath ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(isoPath) && File.Exists(isoPath))
        {
            var structure = await ValidateIsoStructureAsync(isoPath, cancellationToken);
            report.Iso = new IsoOutputReport
            {
                OutputPath = isoPath,
                SizeBytes = new FileInfo(isoPath).Length,
                Sha256 = ComputeSha256(isoPath),
                StructureValidated = structure.All(c => c.Contains("present", StringComparison.OrdinalIgnoreCase)),
                StructureChecks = structure,
            };
        }
        else
        {
            report.Iso = new IsoOutputReport
            {
                BuildError = "Final ISO was not produced (build reported success but the file is missing).",
            };
        }

        return report;
    }

    private static string BuildMediaIdentity(string isoPath, long sizeBytes)
    {
        var name = Path.GetFileName(isoPath);
        return sizeBytes > 0 ? $"{name} ({sizeBytes / (1024.0 * 1024.0 * 1024.0):F1} GB)" : name;
    }

    private static string BuildGateFailure(ProfileCommitValidationReport report)
    {
        var detail = report.Operations
            .Where(o => o.ExecutionStatus == "Succeeded" && o.VerificationStatus != "Verified")
            .Select(o => o.CanonicalKey)
            .Take(5)
            .ToList();
        var suffix = detail.Count == 0 ? string.Empty : $" Non-verified succeeded ops: {string.Join(", ", detail)}.";
        return $"Pre-commit read-back gate rejected the run (succeeded={report.Succeeded}, failed={report.Failed}, " +
               $"skipped={report.Skipped}); nothing was committed.{suffix}";
    }

    /// <summary>
    /// Authoritative DISM mount inventory. Returns the set of registered mount
    /// directories. An unknown registered mount must never be committed or
    /// discarded by this validation run.
    /// </summary>
    private async Task<List<string>> AuthoritativeMountInventoryAsync(CancellationToken cancellationToken)
    {
        var result = new List<string>();
        try
        {
            var run = await _processRunner.RunAsync(new ProcessRequest
            {
                FileName = "dism.exe",
                Arguments = "/English /Get-MountedImageInfo",
            }, cancellationToken);
            if (run.ExitCode != 0)
            {
                return result;
            }

            foreach (var line in run.StandardOutput.Replace("\r\n", "\n").Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Mount Dir :", StringComparison.OrdinalIgnoreCase))
                {
                    var dir = trimmed.Substring("Mount Dir :".Length).Trim();
                    if (dir.Length > 0)
                    {
                        result.Add(Path.GetFullPath(dir));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Commit: mount inventory query failed ({ex.Message}).");
        }

        return result;
    }

    private async Task<(bool Verified, string? Error, List<ProfilePostCommitCheck> Checks, bool Readable)>
        VerifyCommittedImageAsync(
            ProfileIsoCommitRequest request, string committedWimPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(committedWimPath) || !File.Exists(committedWimPath))
        {
            return (false, "Committed working WIM is missing; cannot re-verify persistence.", new List<ProfilePostCommitCheck>(), false);
        }

        var checks = new List<ProfilePostCommitCheck>();

        // Metadata query: the committed image must remain readable/queryable.
        var metaRun = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = "dism.exe",
            Arguments = $"/English /Get-ImageInfo /ImageFile:\"{committedWimPath}\" /Index:1",
        }, cancellationToken);
        var readable = metaRun.ExitCode == 0;

        // Re-open the committed image and independently re-verify every attempted op.
        var verifyWs = new ImageServicingWorkspace
        {
            SourceIsoPath = request.Workspace.SourceIsoPath,
            SourceImageRelativePath = request.Workspace.SourceImageRelativePath,
            SourceImageType = request.Workspace.SourceImageType,
            SelectedIndex = request.Workspace.SelectedIndex,
            SelectedEditionName = request.Workspace.SelectedEditionName,
            WorkingDirectory = request.Workspace.WorkingDirectory,
            WorkingImagePath = committedWimPath,
            MountDirectory = Path.Combine(request.Workspace.WorkingDirectory ?? string.Empty, "mount-verify"),
            WorkingIndex = 1,
            State = ServicingWorkspaceState.Prepared,
        };

        var mounted = await _servicing.MountAsync(verifyWs, cancellationToken);
        if (!mounted.Success)
        {
            return (false, $"Post-commit re-mount failed: {mounted.ErrorMessage}", checks, readable);
        }

        try
        {
            foreach (var op in request.Plan.Operations
                .Where(o => o.IsSelected && o.ExecutionStatus == CustomizationOperationStatus.Succeeded)
                .ToList())
            {
                var v = await _verifier.VerifyAsync(op, verifyWs, cancellationToken);
                checks.Add(new ProfilePostCommitCheck
                {
                    CanonicalKey = op.ConflictKey,
                    OperationType = op.OperationType.ToString(),
                    ExpectedAction = op.ActionKind?.ToString() ?? op.OperationType.ToString(),
                    VerificationStatus = v.Status.ToString(),
                    VerificationDetail = v.Detail,
                });
            }
        }
        finally
        {
            await _servicing.UnmountDiscardAsync(verifyWs, cancellationToken);
        }

        var failed = checks.Where(c => c.VerificationStatus != "Verified").ToList();
        if (failed.Count > 0)
        {
            return (false, $"Post-commit read-back failed for {failed.Count} operation(s): " +
                           string.Join(", ", failed.Select(f => f.CanonicalKey)), checks, readable);
        }

        if (!readable)
        {
            return (false, "Committed WIM metadata query failed (DISM /Get-ImageInfo).", checks, readable);
        }

        return (true, null, checks, readable);
    }

    private async Task<List<string>> ValidateIsoStructureAsync(string isoPath, CancellationToken cancellationToken)
    {
        var checks = new List<string>();
        string? root = null;
        try
        {
            root = await _isoMount.MountReadOnlyAsync(isoPath, cancellationToken);
            var r = root.TrimEnd('\\', '/');
            var bootEtfs = Path.Combine(r, "boot", "etfsboot.com");
            var efiSys = Path.Combine(r, "efi", "microsoft", "boot", "efisys.bin");
            var bootWim = Path.Combine(r, "sources", "boot.wim");
            var installWim = Path.Combine(r, "sources", "install.wim");
            var setupExe = Path.Combine(r, "setup.exe");
            checks.Add(File.Exists(bootEtfs) ? "boot\\etfsboot.com present" : "boot\\etfsboot.com MISSING");
            checks.Add(File.Exists(efiSys) ? "efi\\microsoft\\boot\\efisys.bin present (UEFI boot)" : "efi\\microsoft\\boot\\efisys.bin MISSING (UEFI boot)");
            checks.Add(File.Exists(bootWim) ? "sources\\boot.wim present" : "sources\\boot.wim MISSING");
            checks.Add(File.Exists(installWim) ? "sources\\install.wim present" : "sources\\install.wim MISSING");
            checks.Add(File.Exists(setupExe) ? "setup.exe present" : "setup.exe MISSING");
        }
        catch (Exception ex)
        {
            checks.Add($"ISO structure validation could not mount the produced ISO: {ex.Message}");
        }
        finally
        {
            if (root is not null)
            {
                try
                {
                    await _isoMount.DismountAsync(isoPath, cancellationToken);
                }
                catch
                {
                    /* best effort */
                }
            }
        }

        return checks;
    }

    private static string ComputeSha256(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}

public sealed class ProfileIsoCommitRequest
{
    public required ProfileDefinition Profile { get; init; }
    public required CustomizationPlan Plan { get; init; }
    public required ImageServicingWorkspace Workspace { get; init; }
    public required ProfileApplyValidationReport ApplyReport { get; init; }

    /// <summary>Absolute path of the read-only source ISO (never modified).</summary>
    public required string SourceIsoPath { get; init; }

    /// <summary>Source ISO file size in bytes (for the media identity report).</summary>
    public long SourceIsoSizeBytes { get; init; }

    /// <summary>Relative path of the source install image inside the ISO.</summary>
    public string SourceImageRelativePath { get; init; } = string.Empty;

    /// <summary>Container format of the source install image.</summary>
    public WindowsImageType SourceImageType { get; init; } = WindowsImageType.Unknown;

    /// <summary>Source edition display name (e.g. "Windows 11 Pro").</summary>
    public string? SourceEditionName { get; init; }

    /// <summary>User-chosen directory for the final ISO output (never the repo, never the source ISO root).</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Deterministic file name (without extension) for the final ISO.</summary>
    public required string OutputFileName { get; init; }
}
