using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.ImageMetadata;

namespace WinForge.Infrastructure.ImageMetadata;

/// <summary>
/// Read-only Windows image metadata inspector. It queries an install.wim /
/// install.esd through <c>dism.exe /English /Get-WimInfo</c> and parses the
/// result with <see cref="DismWimInfoParser"/>. It never mounts, modifies, or
/// services the image. DISM is always invoked with <c>/English</c> so the parsed
/// fields are stable regardless of the host's UI language.
/// </summary>
public sealed class WindowsImageMetadataService : IWindowsImageMetadataService
{
    private readonly IProcessRunner _processRunner;
    private readonly ILoggerService _logger;

    public WindowsImageMetadataService(IProcessRunner processRunner, ILoggerService logger)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WindowsImageMetadataResult> InspectAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        _logger.Info("Windows image metadata inspection started");

        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return Failed(imagePath, "No image path was provided.");
        }

        if (!File.Exists(imagePath))
        {
            return Failed(imagePath, "The image file does not exist.");
        }

        var imageType = IsEsd(imagePath) ? WindowsImageType.Esd : WindowsImageType.Wim;
        _logger.Info($"Found {(IsEsd(imagePath) ? "install.esd" : "install.wim")}");

        _logger.Info("Querying image indexes");

        ProcessResult run;
        try
        {
            run = await _processRunner.RunAsync(
                new ProcessRequest
                {
                    FileName = "dism.exe",
                    Arguments = $"/English /Get-WimInfo /ImageFile:\"{imagePath}\""
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation propagates; cleanup of the surrounding ISO mount is
            // handled independently and is not affected by this failure.
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"DISM process could not be started or completed: {ex}");
            return Failed(imagePath, "The Windows image could not be queried.");
        }

        if (run.ExitCode != 0)
        {
            _logger.Warning($"DISM exited with code {run.ExitCode} while reading image metadata.");
            return Failed(imagePath, "The Windows image could not be read.");
        }

        var result = DismWimInfoParser.Parse(run.StandardOutput, imagePath, imageType);

        _logger.Info($"Found {result.Editions.Count} image index(es)");
        if (result.Status == WindowsImageMetadataStatus.Completed)
        {
            foreach (var edition in result.Editions)
            {
                _logger.Debug($"Reading image index {edition.Index}");
                _logger.Info($"Detected: {edition.Name} / {edition.Architecture} / build {edition.Build}");
            }
        }

        _logger.Info("Metadata inspection completed");
        return result;
    }

    private static bool IsEsd(string path)
        => string.Equals(Path.GetExtension(path), ".esd", StringComparison.OrdinalIgnoreCase);

    private static WindowsImageMetadataResult Failed(string? imagePath, string message) => new()
    {
        ImagePath = imagePath,
        ImageType = string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)
            ? WindowsImageType.Unknown
            : (string.Equals(Path.GetExtension(imagePath), ".esd", StringComparison.OrdinalIgnoreCase)
                ? WindowsImageType.Esd
                : WindowsImageType.Wim),
        Status = WindowsImageMetadataStatus.Failed,
        ErrorMessage = message
    };
}
