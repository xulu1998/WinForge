using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.ImageMetadata;

namespace WinForge.Infrastructure.Servicing;

/// <summary>
/// Windows DISM-backed implementation of <see cref="IImageServicingService"/>
/// (Phase 3 Step 3.2). It prepares an isolated working WIM by exporting ONLY the
/// selected source index (single-index working image, working index 1), mounts
/// that working image for later phases, discards an unmount, and validates /
/// recovers sessions. The original ISO and its install.wim/install.esd are never
/// modified — export reads from a transient read-only ISO mount and writes a new
/// file under a WinForge-owned workspace directory.
///
/// <para>All external tooling (DISM, ISO mount) is reached through Core
/// abstractions so the service is fully testable with fakes.</para>
/// </summary>
public sealed class ImageServicingService : IImageServicingService
{
    private readonly IProcessRunner _processRunner;
    private readonly IIsoMountService _isoMount;
    private readonly IWorkspacePathProvider _paths;
    private readonly IWorkspaceSafeDelete _safeDelete;
    private readonly ILoggerService _logger;

    public ImageServicingService(
        IProcessRunner processRunner,
        IIsoMountService isoMount,
        IWorkspacePathProvider paths,
        IWorkspaceSafeDelete safeDelete,
        ILoggerService logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _isoMount = isoMount ?? throw new ArgumentNullException(nameof(isoMount));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _safeDelete = safeDelete ?? throw new ArgumentNullException(nameof(safeDelete));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ServicingResult> PrepareWorkingImageAsync(
        ImageWorkspace source,
        string workspaceId,
        CancellationToken cancellationToken = default)
    {
        _logger.Info($"Servicing: workspace creation started (id={workspaceId}).");

        if (source is null || string.IsNullOrWhiteSpace(source.SourceIsoPath))
        {
            return ServicingResult.Fail(null, "No source ISO path was provided.", ServicingHealth.Invalid);
        }

        if (source.ImageType == WindowsImageType.Unknown)
        {
            return ServicingResult.Fail(null, "Source image type is unknown.", ServicingHealth.Invalid);
        }

        if (source.SelectedIndex <= 0)
        {
            return ServicingResult.Fail(null, "No edition index was selected.", ServicingHealth.Invalid);
        }

        var workingDir = _paths.GetOrCreateWorkspaceDirectory(workspaceId);
        var workingImagePath = _paths.GetWorkingImagePath(workspaceId);
        var mountDir = _paths.GetMountDirectory(workspaceId);

        var workspace = new ImageServicingWorkspace
        {
            SourceIsoPath = source.SourceIsoPath,
            SourceImageRelativePath = source.ImageRelativePath,
            SourceImageType = source.ImageType,
            SelectedIndex = source.SelectedIndex,
            SelectedEditionName = source.SelectedEditionName,
            Architecture = source.Architecture,
            Build = source.Build,
            WorkingDirectory = workingDir,
            WorkingImagePath = workingImagePath,
            MountDirectory = mountDir,
            WorkingImageType = WindowsImageType.Wim,
            WorkingIndex = 1,
            State = ServicingWorkspaceState.Preparing,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            // Transient read-only mount of the SOURCE ISO purely to read the
            // install image. The mount is always released in finally.
            var isoRoot = await _isoMount.MountReadOnlyAsync(source.SourceIsoPath, cancellationToken);
            try
            {
                var sourceImagePath = Path.Combine(isoRoot.TrimEnd('\\', '/'),
                    ImageWorkspace.NormalizeRelativePath(source.ImageRelativePath).Replace('\\', '/'));

                if (!File.Exists(sourceImagePath))
                {
                    return MarkFailed(workspace, "The source install image was not found in the ISO.");
                }

                _logger.Info($"Servicing: exporting selected index {source.SelectedIndex} to working WIM.");

                // DISM export: source index N -> new standalone WIM with index 1.
                var export = await _processRunner.RunAsync(new ProcessRequest
                {
                    FileName = "dism.exe",
                    Arguments = $"/English /Export-Image /SourceImageFile:\"{sourceImagePath}\" " +
                                $"/SourceIndex:{source.SelectedIndex} " +
                                $"/DestinationImageFile:\"{workingImagePath}\" /Compress:max /CheckIntegrity"
                }, cancellationToken);

                if (export.ExitCode != 0)
                {
                    _logger.Warning($"Servicing: DISM export exited with code {export.ExitCode}.");
                    return CleanupAndFail(workspace, workingDir,
                        $"Working image export failed (DISM exit {export.ExitCode}).");
                }

                _logger.Info("Servicing: export completed; validating working image.");
            }
            finally
            {
                await SafeDismountAsync(source.SourceIsoPath, cancellationToken);
            }

            var validation = await ValidateWorkingImageAsync(workspace, source, cancellationToken);
            if (!validation)
            {
                return CleanupAndFail(workspace, workingDir,
                    workspace.LastError ?? "Working image validation failed.");
            }

            workspace.State = ServicingWorkspaceState.Prepared;
            workspace.LastError = null;
            _logger.Info($"Servicing: workspace prepared (working index 1, source index {source.SelectedIndex}).");
            return ServicingResult.Ok(workspace, ServicingHealth.Prepared);
        }
        catch (OperationCanceledException)
        {
            workspace.State = ServicingWorkspaceState.Failed;
            workspace.LastError = "Preparation was cancelled.";
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Servicing: preparation failed: {ex.Message}");
            return CleanupAndFail(workspace, workingDir, "Working image preparation failed unexpectedly.");
        }
    }

    public async Task<ServicingResult> MountAsync(
        ImageServicingWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        _logger.Info("Servicing: mount started.");
        if (!CanMount(workspace, out var blocked))
        {
            return ServicingResult.Fail(workspace, blocked, ServicingHealth.Invalid);
        }

        if (!File.Exists(workspace.WorkingImagePath))
        {
            return MarkFailed(workspace, "Working image does not exist; cannot mount.");
        }

        // Mount directory must be empty/safe and owned by this session.
        if (!EnsureEmptyMountDir(workspace.MountDirectory!))
        {
            return MarkFailed(workspace, "Mount directory is not empty or cannot be prepared.");
        }

        workspace.State = ServicingWorkspaceState.Mounting;
        try
        {
            var mount = await _processRunner.RunAsync(new ProcessRequest
            {
                FileName = "dism.exe",
                Arguments = $"/English /Mount-Image /ImageFile:\"{workspace.WorkingImagePath}\" " +
                            $"/Index:{workspace.WorkingIndex} /MountDir:\"{workspace.MountDirectory}\""
            }, cancellationToken);

            if (mount.ExitCode != 0)
            {
                _logger.Warning($"Servicing: DISM mount exited with code {mount.ExitCode}.");
                return MarkFailed(workspace, $"Working image mount failed (DISM exit {mount.ExitCode}).");
            }

            // Do not trust the exit code alone: verify the mount is registered.
            var verify = await GetMountedImagesAsync(cancellationToken);
            if (!verify.Contains(workspace.MountDirectory!, StringComparer.OrdinalIgnoreCase))
            {
                _logger.Warning("Servicing: DISM reported success but the mount is not registered.");
                await BestEffortUnmountAsync(workspace, cancellationToken);
                return MarkFailed(workspace, "Mount reported success but is not registered; unmounted.");
            }

            workspace.State = ServicingWorkspaceState.Mounted;
            workspace.LastError = null;
            _logger.Info("Servicing: working image mounted.");
            return ServicingResult.Ok(workspace, ServicingHealth.Mounted);
        }
        catch (OperationCanceledException)
        {
            workspace.State = ServicingWorkspaceState.Failed;
            workspace.LastError = "Mount was cancelled.";
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Servicing: mount failed: {ex.Message}");
            return MarkFailed(workspace, "Working image mount failed unexpectedly.");
        }
    }

    public async Task<ServicingResult> UnmountDiscardAsync(
        ImageServicingWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        _logger.Info("Servicing: unmount (discard) started.");

        // A repeated unmount on an already-unmounted session is a safe no-op.
        if (workspace.State != ServicingWorkspaceState.Mounted)
        {
            workspace.State = ServicingWorkspaceState.Prepared;
            return ServicingResult.Ok(workspace, ServicingHealth.Prepared);
        }

        workspace.State = ServicingWorkspaceState.Unmounting;
        try
        {
            var unmount = await _processRunner.RunAsync(new ProcessRequest
            {
                FileName = "dism.exe",
                Arguments = $"/English /Unmount-Image /MountDir:\"{workspace.MountDirectory}\" /Discard"
            }, cancellationToken);

            if (unmount.ExitCode != 0)
            {
                _logger.Warning($"Servicing: DISM unmount exited with code {unmount.ExitCode}.");
                // Preserve error state; do not silently pretend success.
                workspace.State = ServicingWorkspaceState.Failed;
                workspace.LastError = $"Unmount failed (DISM exit {unmount.ExitCode}).";
                return ServicingResult.Fail(workspace, workspace.LastError, ServicingHealth.Failed);
            }

            // Verify the mount is actually gone before declaring Prepared.
            var verify = await GetMountedImagesAsync(cancellationToken);
            if (verify.Contains(workspace.MountDirectory!, StringComparer.OrdinalIgnoreCase))
            {
                workspace.State = ServicingWorkspaceState.Failed;
                workspace.LastError = "Unmount reported success but the mount is still registered.";
                return ServicingResult.Fail(workspace, workspace.LastError, ServicingHealth.Failed);
            }

            workspace.State = ServicingWorkspaceState.Prepared;
            workspace.LastError = null;
            _logger.Info("Servicing: working image unmounted (changes discarded); working image retained.");
            return ServicingResult.Ok(workspace, ServicingHealth.Prepared);
        }
        catch (OperationCanceledException)
        {
            workspace.State = ServicingWorkspaceState.Failed;
            workspace.LastError = "Unmount was cancelled.";
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Servicing: unmount failed: {ex.Message}");
            workspace.State = ServicingWorkspaceState.Failed;
            workspace.LastError = "Unmount failed unexpectedly.";
            return ServicingResult.Fail(workspace, workspace.LastError, ServicingHealth.Failed);
        }
    }

    public async Task<ServicingResult> CommitUnmountAsync(
        ImageServicingWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        _logger.Info("Servicing: unmount (commit) started.");

        // A repeated commit on an already-unmounted session is a safe no-op:
        // there is nothing to commit and the working image is retained.
        if (workspace.State != ServicingWorkspaceState.Mounted)
        {
            workspace.State = ServicingWorkspaceState.Prepared;
            return ServicingResult.Ok(workspace, ServicingHealth.Prepared);
        }

        workspace.State = ServicingWorkspaceState.Unmounting;
        try
        {
            // The Build pipeline's commit path. MUST use /Commit, never /Discard:
            // this writes the customization changes into the working WIM.
            var unmount = await _processRunner.RunAsync(new ProcessRequest
            {
                FileName = "dism.exe",
                Arguments = $"/English /Unmount-Image /MountDir:\"{workspace.MountDirectory}\" /Commit"
            }, cancellationToken);

            if (unmount.ExitCode != 0)
            {
                _logger.Warning($"Servicing: DISM commit unmount exited with code {unmount.ExitCode}.");
                // The mount may still be attached; leave it recoverable and STOP.
                // The build must not proceed to ISO export from a half-committed state.
                workspace.State = ServicingWorkspaceState.Failed;
                workspace.LastError = $"Commit failed (DISM exit {unmount.ExitCode}).";
                return ServicingResult.Fail(workspace, workspace.LastError, ServicingHealth.Failed);
            }

            // Verify the mount is actually gone before declaring Prepared.
            var verify = await GetMountedImagesAsync(cancellationToken);
            if (verify.Contains(workspace.MountDirectory!, StringComparer.OrdinalIgnoreCase))
            {
                workspace.State = ServicingWorkspaceState.Failed;
                workspace.LastError = "Commit reported success but the mount is still registered.";
                return ServicingResult.Fail(workspace, workspace.LastError, ServicingHealth.Failed);
            }

            workspace.State = ServicingWorkspaceState.Prepared;
            workspace.LastError = null;
            _logger.Info("Servicing: working image committed and unmounted; working image retained.");
            return ServicingResult.Ok(workspace, ServicingHealth.Prepared);
        }
        catch (OperationCanceledException)
        {
            workspace.State = ServicingWorkspaceState.Failed;
            workspace.LastError = "Commit was cancelled.";
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Servicing: commit failed: {ex.Message}");
            workspace.State = ServicingWorkspaceState.Failed;
            workspace.LastError = "Working image commit failed unexpectedly.";
            return ServicingResult.Fail(workspace, workspace.LastError, ServicingHealth.Failed);
        }
    }

    public async Task<ServicingResult> ValidateServicingWorkspaceAsync(
        ImageServicingWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        if (workspace is null || string.IsNullOrWhiteSpace(workspace.WorkingImagePath))
        {
            return ServicingResult.Fail(workspace, "Servicing workspace has no working image path.", ServicingHealth.Invalid);
        }

        var workingExists = File.Exists(workspace.WorkingImagePath);
        var mounted = false;
        try
        {
            var registered = await GetMountedImagesAsync(cancellationToken);
            mounted = registered.Contains(workspace.MountDirectory ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            mounted = false;
        }

        // Stale: stored as Mounted but DISM no longer reports it, OR mount dir
        // exists while session is not Mounted.
        if (workspace.State == ServicingWorkspaceState.Mounted && !mounted)
        {
            workspace.State = ServicingWorkspaceState.Failed;
            workspace.LastError = "Session is marked Mounted but DISM reports no such mount (stale).";
            return ServicingResult.Fail(workspace, workspace.LastError, ServicingHealth.Stale);
        }

        if (workspace.State != ServicingWorkspaceState.Mounted && mounted)
        {
            workspace.State = ServicingWorkspaceState.Failed;
            workspace.LastError = "A mount is registered but the session is not Mounted (stale).";
            return ServicingResult.Fail(workspace, workspace.LastError, ServicingHealth.Stale);
        }

        if (!workingExists)
        {
            workspace.State = ServicingWorkspaceState.Failed;
            workspace.LastError = "Working image is missing.";
            return ServicingResult.Fail(workspace, workspace.LastError, ServicingHealth.Invalid);
        }

        if (workspace.State == ServicingWorkspaceState.Mounted)
        {
            return ServicingResult.Ok(workspace, ServicingHealth.Mounted);
        }

        if (workspace.State == ServicingWorkspaceState.Failed)
        {
            return ServicingResult.Fail(workspace, workspace.LastError ?? "Session previously failed.", ServicingHealth.Failed);
        }

        return ServicingResult.Ok(workspace, ServicingHealth.Prepared);
    }

    // ---- internals ----

    private async Task<bool> ValidateWorkingImageAsync(
        ImageServicingWorkspace workspace, ImageWorkspace source, CancellationToken cancellationToken)
    {
        if (!File.Exists(workspace.WorkingImagePath))
        {
            workspace.LastError = "Working image was not produced.";
            return false;
        }

        // Query the produced working image (per-index detail) and confirm the
        // single exported index matches the selected source edition, with
        // architecture/build consistent where the source declares them. The
        // detail query is required: only it reports Architecture/Version, while
        // the index-less enumeration query reports only Index/Name/Description.
        ProcessResult run;
        try
        {
            run = await _processRunner.RunAsync(new ProcessRequest
            {
                FileName = "dism.exe",
                Arguments = $"/English /Get-ImageInfo /ImageFile:\"{workspace.WorkingImagePath}\" /Index:{workspace.WorkingIndex}"
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error($"Servicing: working-image validation query failed: {ex.Message}");
            workspace.LastError = "Working image could not be queried.";
            return false;
        }

        if (run.ExitCode != 0)
        {
            workspace.LastError = "Working image query returned a non-zero exit.";
            return false;
        }

        var only = DismImageInfoParser.ParseImageDetails(run.StandardOutput);
        if (only is null)
        {
            workspace.LastError = "Working image detail could not be parsed.";
            return false;
        }

        // A working image is a single-index export; the working index must be 1.
        if (only.Index != workspace.WorkingIndex)
        {
            workspace.LastError = $"Working image index ({only.Index}) does not match the expected working index ({workspace.WorkingIndex}).";
            return false;
        }

        if (!string.IsNullOrEmpty(source.SelectedEditionName) &&
            !string.Equals(only.Name, source.SelectedEditionName, StringComparison.OrdinalIgnoreCase) &&
            only.Name?.IndexOf(source.SelectedEditionName, StringComparison.OrdinalIgnoreCase) < 0)
        {
            workspace.LastError = "Working image edition does not match the selected source edition.";
            return false;
        }

        if (!string.IsNullOrEmpty(source.Architecture) &&
            !string.Equals(only.Architecture, source.Architecture, StringComparison.OrdinalIgnoreCase))
        {
            workspace.LastError = "Working image architecture does not match the selected source.";
            return false;
        }

        if (!string.IsNullOrEmpty(source.Build) && !string.IsNullOrEmpty(only.Build) &&
            !string.Equals(only.Build, source.Build, StringComparison.OrdinalIgnoreCase))
        {
            workspace.LastError = "Working image build does not match the selected source.";
            return false;
        }

        return true;
    }

    private async Task<System.Collections.Generic.List<string>> GetMountedImagesAsync(CancellationToken cancellationToken)
    {
        var run = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = "dism.exe",
            Arguments = "/English /Get-MountedImageInfo"
        }, cancellationToken);

        var mounted = new System.Collections.Generic.List<string>();
        if (run.ExitCode != 0)
        {
            return mounted;
        }

        // The Mount Dir line reports the mount directory for each mounted image.
        foreach (var line in run.StandardOutput.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Mount Dir :", StringComparison.OrdinalIgnoreCase))
            {
                mounted.Add(trimmed.Substring("Mount Dir :".Length).Trim().TrimEnd('\\'));
            }
        }

        return mounted;
    }

    private bool CanMount(ImageServicingWorkspace workspace, out string reason)
    {
        reason = string.Empty;
        if (workspace is null || string.IsNullOrWhiteSpace(workspace.WorkingImagePath))
        {
            reason = "No servicing workspace is prepared.";
            return false;
        }

        if (workspace.State != ServicingWorkspaceState.Prepared)
        {
            reason = $"Cannot mount from state {workspace.State}; the workspace must be Prepared.";
            return false;
        }

        return true;
    }

    private bool EnsureEmptyMountDir(string mountDir)
    {
        try
        {
            Directory.CreateDirectory(mountDir);
            // A mount point must be empty before DISM mounts into it.
            return Directory.GetFileSystemEntries(mountDir).Length == 0;
        }
        catch
        {
            return false;
        }
    }

    private ServicingResult MarkFailed(ImageServicingWorkspace workspace, string error)
    {
        workspace.State = ServicingWorkspaceState.Failed;
        workspace.LastError = error;
        _logger.Warning($"Servicing: {error}");
        return ServicingResult.Fail(workspace, error, ServicingHealth.Failed);
    }

    private ServicingResult CleanupAndFail(ImageServicingWorkspace workspace, string workingDir, string error)
    {
        // Remove partial disposable output under the workspace directory, proven
        // safe by the guard, so no fake Prepared state survives.
        try
        {
            if (File.Exists(workspace.WorkingImagePath))
            {
                _safeDelete.TryDeleteWithinWorkspace(workingDir, workspace.WorkingImagePath);
            }
        }
        catch
        {
            /* best effort */
        }

        workspace.State = ServicingWorkspaceState.Failed;
        workspace.LastError = error;
        _logger.Warning($"Servicing: {error}");
        return ServicingResult.Fail(workspace, error, ServicingHealth.Failed);
    }

    private async Task SafeDismountAsync(string isoPath, CancellationToken cancellationToken)
    {
        try
        {
            await _isoMount.DismountAsync(isoPath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Warning($"Servicing: source ISO dismount issue: {ex.Message}");
        }
    }

    private async Task BestEffortUnmountAsync(ImageServicingWorkspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            await _processRunner.RunAsync(new ProcessRequest
            {
                FileName = "dism.exe",
                Arguments = $"/English /Unmount-Image /MountDir:\"{workspace.MountDirectory}\" /Discard"
            }, cancellationToken);
        }
        catch
        {
            /* best effort cleanup */
        }
    }
}
