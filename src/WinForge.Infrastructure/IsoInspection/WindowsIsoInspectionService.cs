using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.ImageMetadata;

namespace WinForge.Infrastructure.IsoInspection;

/// <summary>
/// Read-only ISO inspector. It validates the file on disk, then — through
/// <see cref="IIsoMountService"/> — mounts the ISO read-only, inspects the
/// on-disk directory layout, and always dismounts (even on failure). It never
/// modifies the ISO, runs DISM servicing, or parses WIM/ESD contents.
/// </summary>
public sealed class WindowsIsoInspectionService : IIsoInspectionService
{
    private readonly IIsoMountService _mountService;
    private readonly IWindowsImageMetadataService _metadataService;
    private readonly ILoggerService _logger;

    public WindowsIsoInspectionService(
        IIsoMountService mountService,
        IWindowsImageMetadataService metadataService,
        ILoggerService logger)
    {
        _mountService = mountService ?? throw new ArgumentNullException(nameof(mountService));
        _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IsoInspectionResult> InspectAsync(string isoPath, CancellationToken cancellationToken = default)
    {
        var result = new IsoInspectionResult { IsoPath = isoPath };

        if (string.IsNullOrWhiteSpace(isoPath))
        {
            result.Status = IsoInspectionStatus.Failed;
            result.ErrorMessage = "No ISO path was provided.";
            _logger.Warning("ISO inspection skipped: no path provided.");
            return result;
        }

        result.FileName = Path.GetFileName(isoPath);
        result.ExtensionValid = IsIsoExtension(isoPath);

        if (!File.Exists(isoPath))
        {
            result.Exists = false;
            result.Status = IsoInspectionStatus.Completed;
            result.DetectedType = IsoDetectedType.Unknown;
            result.ErrorMessage = "The selected file does not exist.";
            _logger.Warning($"ISO inspection: file not found: {isoPath}");
            return result;
        }

        result.Exists = true;
        result.FileSizeBytes = new FileInfo(isoPath).Length;
        result.IsReadable = IsReadable(isoPath);

        if (!result.ExtensionValid || !result.IsReadable)
        {
            result.Status = IsoInspectionStatus.Completed;
            result.DetectedType = IsoDetectedType.Unknown;
            result.ErrorMessage = !result.ExtensionValid
                ? "The selected file is not an .iso file."
                : "The selected file could not be read.";
            _logger.Warning(
                $"ISO inspection: rejected {isoPath} (extension valid={result.ExtensionValid}, readable={result.IsReadable}).");
            return result;
        }

        // Passed preconditions: mount read-only and inspect the layout.
        _logger.Info($"ISO inspection started: {isoPath}");

        // mountAttempted tracks whether a mount call was ever made. Once we
        // attempt a mount, a dismount MUST always be attempted in the finally
        // block — even if cancellation or failure occurred before we learned the
        // mounted root. This is the core safety property: an ISO can never be
        // left mounted because cleanup was itself cancelled or skipped.
        bool mountAttempted = false;
        string? mountedRoot = null;
        Exception? originalError = null;
        try
        {
            mountAttempted = true;
            _logger.Info("Mounting ISO read-only...");
            mountedRoot = await _mountService.MountReadOnlyAsync(isoPath, cancellationToken);
            _logger.Info($"ISO mounted at {mountedRoot}");

            if (string.IsNullOrEmpty(mountedRoot))
            {
                // The OS mount may have completed but no drive root was obtained.
                // Treat this as an inspection failure so cleanup still runs.
                throw new InvalidOperationException(
                    "The ISO was mounted but no drive root was returned.");
            }

            InspectLayout(mountedRoot, result);
            _logger.Info("ISO structure inspected.");

            // Step 2.2: read install-image metadata *while the ISO is still
            // mounted*. The high-level inspection session owns the mount
            // lifecycle, so the ViewModel never mounts/unmounts or coordinates
            // this itself. Metadata inspection uses the same cancellation token,
            // but its failure cannot prevent the guaranteed dismount below.
            string? imagePath = null;
            if (result.HasInstallWim)
            {
                imagePath = Path.Combine(mountedRoot, "sources", "install.wim");
            }
            else if (result.HasInstallEsd)
            {
                imagePath = Path.Combine(mountedRoot, "sources", "install.esd");
            }

            if (imagePath is not null)
            {
                _logger.Info($"Found {(result.HasInstallWim ? "install.wim" : "install.esd")}");
                result.ImageMetadata = await _metadataService.InspectAsync(imagePath, cancellationToken);

                if (result.ImageMetadata.Status == WindowsImageMetadataStatus.Failed)
                {
                    // The layout was a valid Windows ISO candidate, but its image
                    // metadata could not be read. Surface a friendly failure; the
                    // finally block still dismounts the ISO.
                    result.Status = IsoInspectionStatus.Failed;
                    result.DetectedType = IsoDetectedType.WindowsIsoCandidate;
                    result.ErrorMessage = result.ImageMetadata.ErrorMessage
                        ?? "The Windows image metadata could not be read.";
                    _logger.Error("Windows image metadata inspection failed.");
                }
            }

            if (result.Status != IsoInspectionStatus.Failed)
            {
                result.Status = IsoInspectionStatus.Completed;
            }

            _logger.Info(result.DetectedType == IsoDetectedType.WindowsIsoCandidate
                ? $"Detected Windows ISO candidate (install image: {result.InstallImageType})."
                : "ISO layout does not match a Windows installation image (Unknown).");
        }
        catch (Exception ex)
        {
            originalError = ex;
            result.Status = IsoInspectionStatus.Failed;
            result.DetectedType = IsoDetectedType.Unknown;
            // User-facing message: never leak raw PowerShell/HRESULT/command
            // internals. A concise, non-technical message is shown in the UI; the
            // full technical detail is retained only in the log (below).
            result.ErrorMessage = FriendlyErrorMessage(ex);
            _logger.Error($"ISO inspection failed: {ex}");
        }
        finally
        {
            if (mountAttempted)
            {
                // Cleanup MUST complete even if the operation was cancelled. Use a
                // non-cancellable token so the dismount itself cannot be aborted,
                // which would otherwise leave the ISO mounted. A dismount on an
                // unmounted image is handled safely by WindowsIsoMountService;
                // failures here are logged, never propagated.
                try
                {
                    await _mountService.DismountAsync(isoPath, CancellationToken.None);
                    _logger.Info("ISO dismounted.");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"ISO dismount failed (manual cleanup may be required): {Sanitize(ex.Message)}");
                }
            }
        }

        // Preserve cancellation: never swallow an OperationCanceledException just
        // because cleanup succeeded. Other unexpected failures surface as a
        // Failed result with a friendly message (set above). Cleanup failures do
        // not replace the original failure/cancellation.
        if (originalError is OperationCanceledException operationCanceled)
        {
            throw operationCanceled;
        }

        return result;
    }

    private static void InspectLayout(string root, IsoInspectionResult result)
    {
        var sources = Path.Combine(root, "sources");
        var boot = Path.Combine(root, "boot");

        result.HasSourcesDirectory = Directory.Exists(sources);
        result.HasBootDirectory = Directory.Exists(boot);
        result.HasBootWim = File.Exists(Path.Combine(sources, "boot.wim"));
        result.HasInstallWim = File.Exists(Path.Combine(sources, "install.wim"));
        result.HasInstallEsd = File.Exists(Path.Combine(sources, "install.esd"));

        result.InstallImageType = result.HasInstallWim
            ? InstallImageType.Wim
            : result.HasInstallEsd ? InstallImageType.Esd : InstallImageType.Unknown;

        result.DetectedType = result.HasSourcesDirectory && result.HasBootDirectory &&
                              (result.HasInstallWim || result.HasInstallEsd)
            ? IsoDetectedType.WindowsIsoCandidate
            : IsoDetectedType.Unknown;
    }

    private static bool IsIsoExtension(string path)
        => string.Equals(Path.GetExtension(path), ".iso", StringComparison.OrdinalIgnoreCase);

    private static bool IsReadable(string path)
    {
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Sanitize(string message)
        => string.IsNullOrWhiteSpace(message) ? "Unspecified inspection error." : message.Trim();

    /// <summary>
    /// Produces the message shown to the user when inspection fails. It is
    /// deliberately generic so it can never expose raw PowerShell errors, HRESULT
    /// codes, command text, or internal exception details. The full technical
    /// detail is recorded via <see cref="ILoggerService"/>, not the UI.
    /// </summary>
    private static string FriendlyErrorMessage(Exception ex)
        => ex is OperationCanceledException
            ? "The ISO inspection was cancelled."
            : "The ISO could not be inspected. See the application log for technical details.";
}
