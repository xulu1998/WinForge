using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;

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
    private readonly ILoggerService _logger;

    public WindowsIsoInspectionService(IIsoMountService mountService, ILoggerService logger)
    {
        _mountService = mountService ?? throw new ArgumentNullException(nameof(mountService));
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
        string? mountedRoot = null;
        try
        {
            _logger.Info("Mounting ISO read-only...");
            mountedRoot = await _mountService.MountReadOnlyAsync(isoPath, cancellationToken);
            _logger.Info($"ISO mounted at {mountedRoot}");

            InspectLayout(mountedRoot!, result);
            _logger.Info("ISO structure inspected.");

            result.Status = IsoInspectionStatus.Completed;
            _logger.Info(result.DetectedType == IsoDetectedType.WindowsIsoCandidate
                ? $"Detected Windows ISO candidate (install image: {result.InstallImageType})."
                : "ISO layout does not match a Windows installation image (Unknown).");
        }
        catch (Exception ex)
        {
            result.Status = IsoInspectionStatus.Failed;
            result.DetectedType = IsoDetectedType.Unknown;
            result.ErrorMessage = Sanitize(ex.Message);
            _logger.Error($"ISO inspection failed: {Sanitize(ex.Message)}");
        }
        finally
        {
            if (mountedRoot != null)
            {
                try
                {
                    await _mountService.DismountAsync(isoPath, cancellationToken);
                    _logger.Info("ISO dismounted.");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"ISO dismount failed (manual cleanup may be required): {Sanitize(ex.Message)}");
                }
            }
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
}
