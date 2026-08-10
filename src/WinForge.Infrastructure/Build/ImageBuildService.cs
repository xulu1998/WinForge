using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Build;

/// <summary>
/// Phase 10 — Build / ISO Export orchestrator. Drives the full pipeline
/// (preflight → commit → export → prepare media → build ISO → verify → report)
/// behind documented, fakeable sub-services. Safety guarantees (see
/// <see cref="IBuildService"/>): commit uses DISM <c>/Unmount-Image /Commit</c>
/// (never <c>/Discard</c>); a commit failure stops the build with the workspace
/// left recoverable; the final ISO is written to a <c>.partial</c> file and only
/// renamed to the final path after verification succeeds; on failure or
/// cancellation the partial output is cleaned where safe and success is never
/// reported.
/// </summary>
public sealed class ImageBuildService : IBuildService
{
    private const string RecoveryFileName = "build.recovery.json";

    private readonly IImageServicingService _servicing;
    private readonly IWimExporter _exporter;
    private readonly IIsoMediaPreparer _media;
    private readonly IBootableIsoBuilder _isoBuilder;
    private readonly IBuildVerifier _verifier;
    private readonly IAdkToolLocator _adk;
    private readonly IFileSystem _fileSystem;
    private readonly ILoggerService _logger;

    public ImageBuildService(
        IImageServicingService servicing,
        IWimExporter exporter,
        IIsoMediaPreparer media,
        IBootableIsoBuilder isoBuilder,
        IBuildVerifier verifier,
        IAdkToolLocator adk,
        IFileSystem fileSystem,
        ILoggerService logger)
    {
        _servicing = servicing ?? throw new ArgumentNullException(nameof(servicing));
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _isoBuilder = isoBuilder ?? throw new ArgumentNullException(nameof(isoBuilder));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _adk = adk ?? throw new ArgumentNullException(nameof(adk));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BuildResult> BuildAsync(
        BuildRequest request,
        IProgress<BuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var log = new List<string>();
        string? buildWs = null;
        string? finalOutputPath = null;
        string? partialPath = null;
        var phase = BuildState.NotStarted;
        // Declared outside the try so the catch/finally blocks can use them.
        BuildRecoveryState? recovery = null;
        bool committed = false;
        bool exported = false;
        string? finalWim = null;
        string? mediaRoot = null;

        void Report(BuildState p, string message, int percent)
        {
            phase = p;
            log.Add(message);
            _logger.Info("Build: " + message);
            progress?.Report(BuildProgress.Of(p, message, percent));
        }

        void WriteRecovery(BuildState p)
        {
            if (buildWs is null)
            {
                return;
            }

            var state = new BuildRecoveryState
            {
                State = p,
                OutputPath = finalOutputPath,
                WorkspaceDirectory = buildWs,
                SourceIsoPath = request.SourceIsoPath,
                PartialOutputPresent = partialPath is not null && _fileSystem.FileExists(partialPath)
            };
            try
            {
                _fileSystem.WriteAllText(_fileSystem.PathCombine(buildWs, RecoveryFileName), JsonSerializer.Serialize(state));
            }
            catch
            {
                /* best effort */
            }
        }

        void DeletePartial()
        {
            if (partialPath is not null && _fileSystem.FileExists(partialPath))
            {
                _fileSystem.DeleteFile(partialPath);
            }
        }

        void CleanupWorkspace()
        {
            if (buildWs is not null)
            {
                _fileSystem.DeleteDirectory(buildWs, recursive: true);
            }
        }

        try
        {
            log.Add("Build started");
            _logger.Info("Build: started.");

            // ---- Preflight ----
            Report(BuildState.Preflight, "Preflight validation started", 5);
            if (request is null || string.IsNullOrWhiteSpace(request.OutputDirectory) ||
                string.IsNullOrWhiteSpace(request.OutputFileName) ||
                string.IsNullOrWhiteSpace(request.WorkingImagePath) ||
                string.IsNullOrWhiteSpace(request.MountDirectory) ||
                string.IsNullOrWhiteSpace(request.BuildWorkspaceDirectory) ||
                string.IsNullOrWhiteSpace(request.SourceIsoPath))
            {
                return BuildResult.Fail(BuildState.Preflight, "Build request is missing required parameters.", log);
            }

            if (!_fileSystem.FileExists(request.SourceIsoPath))
            {
                return BuildResult.Fail(BuildState.Preflight, "The source ISO was not found.", log);
            }

            if (!_fileSystem.FileExists(request.WorkingImagePath))
            {
                return BuildResult.Fail(BuildState.Preflight, "The working image was not found (it must be mounted and committed).", log);
            }

            if (!_adk.IsAvailable())
            {
                return BuildResult.Fail(BuildState.Preflight,
                    "Windows ADK Deployment Tools (oscdimg.exe) is required to build the final bootable ISO.", log);
            }

            if (!_fileSystem.DirectoryExists(request.OutputDirectory))
            {
                _fileSystem.CreateDirectory(request.OutputDirectory);
            }

            buildWs = request.BuildWorkspaceDirectory;
            _fileSystem.CreateDirectory(buildWs);

            finalOutputPath = ResolveOutputPath(request, _fileSystem);
            if (finalOutputPath is null)
            {
                return BuildResult.Fail(BuildState.Preflight, "The output path already exists and cannot be overwritten.", log);
            }

            partialPath = finalOutputPath + ".partial";

            // ---- Resumable checkpoint detection ----
            // A prior run may have already committed and/or exported the working
            // image. Those artifacts are durable (the committed WIM lives outside
            // the build workspace; the exported final WIM lives inside it). If a
            // checkpoint exists we RESUME from the latest completed step instead of
            // re-committing an already-unmounted image or re-exporting — and we keep
            // the artifacts on failure so the user can retry without re-applying.
            recovery = await DetectInterruptedBuildAsync(buildWs, cancellationToken);
            committed = recovery is not null && recovery.State >= BuildState.CommittingImage
                && _fileSystem.FileExists(request.WorkingImagePath);
            finalWim = _fileSystem.PathCombine(buildWs, "install.wim");
            exported = committed && _fileSystem.FileExists(finalWim);

            if (!committed && !exported)
            {
                // Genuinely fresh (or pre-commit) run: start from a clean workspace so
                // a stale partial or crashed media tree never blocks the new run.
                _fileSystem.DeleteDirectory(buildWs, recursive: true);
                _fileSystem.CreateDirectory(buildWs);
                WriteRecovery(BuildState.Preflight);
            }
            else
            {
                _logger.Info($"Build: resuming from checkpoint (committed={committed}, exported={exported}); " +
                             "skipping already-completed steps.");
            }

            log.Add("Preflight passed");
            _logger.Info("Build: preflight passed.");

            // ---- Commit ----
            Report(BuildState.CommittingImage, "Committing working image", 20);
            if (committed)
            {
                // Resuming past a successful commit: the working image is already
                // committed and unmounted, so re-committing would target a
                // non-mounted image and fail. The durable artifact is reused.
                _logger.Info("Build: image already committed (resume); skipping Commit step.");
            }
            else
            {
                var commitWs = new ImageServicingWorkspace
                {
                    WorkingImagePath = request.WorkingImagePath,
                    MountDirectory = request.MountDirectory,
                    State = ServicingWorkspaceState.Mounted
                };
                var commit = await _servicing.CommitUnmountAsync(commitWs, cancellationToken);
                if (!commit.Success)
                {
                    // Stop here. No ISO build begins; the workspace is left recoverable.
                    CleanupWorkspace();
                    return BuildResult.Fail(BuildState.CommittingImage,
                        commit.ErrorMessage ?? "Committing the working image failed.", log);
                }

                committed = true;
                log.Add("Working image committed");
                _logger.Info("Build: working image committed.");
                WriteRecovery(BuildState.CommittingImage);
            }

            // ---- Export ----
            Report(BuildState.ExportingImage, "Exporting final image", 40);
            if (exported)
            {
                // Resuming past a successful export: the final WIM already exists in
                // the build workspace; reuse it instead of re-exporting.
                _logger.Info("Build: final WIM already exported (resume); skipping Export step.");
            }
            else
            {
                var export = await _exporter.ExportAsync(new WimExportRequest
                {
                    SourceImagePath = request.WorkingImagePath,
                    SourceIndex = request.WorkingIndex,
                    DestinationImagePath = finalWim
                }, cancellationToken);

                if (!export.Success)
                {
                    // Keep the workspace: the committed working image + recovery file
                    // let a retry resume directly from Export. Do NOT discard the
                    // durable committed artifact.
                    DeletePartial();
                    return BuildResult.Fail(BuildState.ExportingImage, export.ErrorMessage ?? "Final image export failed.", log, export.ExitCode);
                }

                exported = true;
                log.Add("Final WIM exported");
                _logger.Info("Build: final WIM exported.");
                WriteRecovery(BuildState.ExportingImage);
            }

            // ---- Prepare media ----
            Report(BuildState.PreparingMedia, "Preparing media tree", 60);
            mediaRoot = _fileSystem.PathCombine(buildWs, "media");
            var media = await _media.PrepareAsync(new MediaPrepareRequest
            {
                SourceIsoPath = request.SourceIsoPath,
                BuildMediaRoot = mediaRoot,
                SourceImageRelativePath = request.SourceImageRelativePath,
                SourceImageType = request.SourceImageType,
                FinalInstallWimPath = finalWim
            }, cancellationToken);

            if (!media.Success)
            {
                // Retain the committed/exported artifact so the build can be retried
                // from PrepareMedia without re-committing or re-applying. Only discard
                // the (potentially dirty) media tree.
                DeletePartial();
                DeleteMediaTree(mediaRoot);
                return BuildResult.Fail(BuildState.PreparingMedia, media.ErrorMessage ?? "Media preparation failed.", log);
            }

            if (!media.BootFilesPresent)
            {
                DeletePartial();
                DeleteMediaTree(mediaRoot);
                return BuildResult.Fail(BuildState.PreparingMedia,
                    "Required boot files (boot\\etfsboot.com / efi\\microsoft\\boot\\efisys.bin) are missing from the source media.", log);
            }

            log.Add("Preparing media tree");
            log.Add("Source media copied");
            log.Add("install.wim replaced");
            _logger.Info("Build: media tree prepared; install.wim replaced.");
            WriteRecovery(BuildState.PreparingMedia);

            // ---- Build ISO (to a .partial file) ----
            Report(BuildState.BuildingIso, "Building bootable ISO", 80);
            if (_fileSystem.FileExists(partialPath))
            {
                _fileSystem.DeleteFile(partialPath);
            }

            var bootEtfs = _fileSystem.PathCombine(media.MediaRoot!, "boot", "etfsboot.com");
            var bootEfi = _fileSystem.PathCombine(media.MediaRoot!, "efi", "microsoft", "boot", "efisys.bin");
            var iso = await _isoBuilder.BuildAsync(new IsoBuildRequest
            {
                MediaRoot = media.MediaRoot!,
                OutputIsoPath = partialPath,
                BootFileEtfs = bootEtfs,
                BootFileEfisys = bootEfi
            }, cancellationToken);

            log.Add($"ISO builder exit code {iso.ExitCode}");
            _logger.Info($"Build: ISO builder exit code {iso.ExitCode}.");

            if (!iso.Success)
            {
                if (iso.ToolMissing)
                {
                    // Keep the committed/exported artifact so the build can be retried
                    // once the ADK is available; do not discard it.
                    DeletePartial();
                    return BuildResult.Fail(BuildState.BuildingIso,
                        "Windows ADK Deployment Tools (oscdimg.exe) is required to build the final bootable ISO.", log, iso.ExitCode);
                }

                // Keep the committed/exported artifact; only the produced ISO is lost.
                DeletePartial();
                return BuildResult.Fail(BuildState.BuildingIso, iso.ErrorMessage ?? "ISO creation failed.", log, iso.ExitCode);
            }

            // ---- Verify ----
            Report(BuildState.Verifying, "Verifying ISO", 95);
            var verify = await _verifier.VerifyAsync(new BuildVerificationRequest
            {
                OutputIsoPath = partialPath,
                ExpectedInstallWimPath = media.InstallImagePath!,
                ExpectedEditionName = request.FinalEditionName ?? request.SourceEditionName,
                ExpectedIndex = 1
            }, cancellationToken);

            if (!verify.Success)
            {
                // Keep the committed/exported artifact; the produced ISO is discarded.
                DeletePartial();
                return BuildResult.Fail(BuildState.Verifying, verify.ErrorMessage ?? "ISO verification failed.", log);
            }

            // ---- Atomic rename partial -> final (only after verification) ----
            if (_fileSystem.FileExists(finalOutputPath))
            {
                _fileSystem.DeleteFile(finalOutputPath);
            }

            _fileSystem.MoveFile(partialPath, finalOutputPath);

            log.Add("Build completed");
            log.Add("Cleanup completed");
            _logger.Info("Build: completed; temporary artifacts cleaned.");

            // Remove the temp build workspace (recovery file lives inside it).
            CleanupWorkspace();

            var size = _fileSystem.GetFileSize(finalOutputPath);
            Report(BuildState.Completed, "Build completed", 100);
            return BuildResult.Ok(finalOutputPath, size, log);
        }
        catch (OperationCanceledException)
        {
            log.Add("Build cancelled");
            _logger.Info("Build: cancelled.");
            DeletePartial();
            // If a durable committed/exported checkpoint exists, keep it so the build
            // can be resumed; otherwise discard the workspace.
            if (committed)
            {
                DeleteMediaTree(mediaRoot);
            }
            else
            {
                CleanupWorkspace();
            }

            return BuildResult.Fail(phase, "Build cancelled.", log, finalState: BuildState.Cancelled);
        }
        catch (Exception ex)
        {
            log.Add($"Build failed: {ex.Message}");
            _logger.Error($"Build: failed: {ex.Message}");
            DeletePartial();
            if (committed)
            {
                DeleteMediaTree(mediaRoot);
            }
            else
            {
                CleanupWorkspace();
            }

            return BuildResult.Fail(phase, ex.Message, log);
        }
    }

    private void DeleteMediaTree(string? mediaRoot)
    {
        if (!string.IsNullOrEmpty(mediaRoot) && _fileSystem.DirectoryExists(mediaRoot))
        {
            _fileSystem.DeleteDirectory(mediaRoot, recursive: true);
        }
    }

    public Task<BuildRecoveryState?> DetectInterruptedBuildAsync(
        string buildWorkspaceDirectory,
        CancellationToken cancellationToken = default)
    {
        var path = _fileSystem.PathCombine(buildWorkspaceDirectory, RecoveryFileName);
        if (!_fileSystem.FileExists(path))
        {
            return Task.FromResult<BuildRecoveryState?>(null);
        }

        try
        {
            var json = _fileSystem.ReadAllText(path);
            var state = JsonSerializer.Deserialize<BuildRecoveryState>(json);
            return Task.FromResult<BuildRecoveryState?>(state);
        }
        catch
        {
            return Task.FromResult<BuildRecoveryState?>(null);
        }
    }

    public Task<bool> CleanupInterruptedBuildAsync(
        string buildWorkspaceDirectory,
        CancellationToken cancellationToken = default)
    {
        _fileSystem.DeleteDirectory(buildWorkspaceDirectory, recursive: true);
        return Task.FromResult(!_fileSystem.DirectoryExists(buildWorkspaceDirectory));
    }

    private static string? ResolveOutputPath(BuildRequest request, IFileSystem fileSystem)
    {
        var name = request.OutputFileName;
        if (name.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            name = name.Substring(0, name.Length - 4);
        }

        if (request.OverwritePolicy == BuildOverwritePolicy.Fail &&
            fileSystem.FileExists(fileSystem.PathCombine(request.OutputDirectory, name + ".iso")))
        {
            return null;
        }

        var candidate = name + ".iso";
        var full = fileSystem.PathCombine(request.OutputDirectory, candidate);

        if (request.OverwritePolicy == BuildOverwritePolicy.GenerateUniqueName)
        {
            var i = 1;
            while (fileSystem.FileExists(full))
            {
                candidate = $"{name}({i}).iso";
                full = fileSystem.PathCombine(request.OutputDirectory, candidate);
                i++;
            }
        }

        return full;
    }
}
