using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Build;

/// <summary>
/// DISM-backed implementation of <see cref="IWimExporter"/>. Exports the
/// customized working image index into a clean destination WIM via
/// <c>DISM /Export-Image</c>, so the final install.wim is a fresh, optimized
/// image rather than a potentially bloated servicing WIM reused blindly.
/// </summary>
public sealed class DismWimExporter : IWimExporter
{
    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fileSystem;
    private readonly ILoggerService _logger;

    public DismWimExporter(IProcessRunner processRunner, IFileSystem fileSystem, ILoggerService logger)
    {
        _processRunner = processRunner ?? throw new System.ArgumentNullException(nameof(processRunner));
        _fileSystem = fileSystem ?? throw new System.ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new System.ArgumentNullException(nameof(logger));
    }

    public async Task<WimExportResult> ExportAsync(WimExportRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SourceImagePath) || string.IsNullOrWhiteSpace(request.DestinationImagePath))
        {
            return WimExportResult.Fail("Export request is missing a source or destination path.", -1);
        }

        if (!_fileSystem.FileExists(request.SourceImagePath))
        {
            return WimExportResult.Fail("The committed working image was not found for export.", -1);
        }

        _logger.Info("Build: exporting final install.wim from working image.");

        var run = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = "dism.exe",
            Arguments = $"/English /Export-Image /SourceImageFile:\"{request.SourceImagePath}\" " +
                        $"/SourceIndex:{request.SourceIndex} " +
                        $"/DestinationImageFile:\"{request.DestinationImagePath}\" /Compress:max /CheckIntegrity"
        }, cancellationToken);

        if (run.ExitCode != 0)
        {
            _logger.Warning($"Build: DISM export exited with code {run.ExitCode}.");
            return WimExportResult.Fail($"Final image export failed (DISM exit {run.ExitCode}).", run.ExitCode);
        }

        if (!_fileSystem.FileExists(request.DestinationImagePath))
        {
            return WimExportResult.Fail("Final image export reported success but the file is missing.", run.ExitCode);
        }

        _logger.Info("Build: final WIM exported.");
        return WimExportResult.Ok(request.DestinationImagePath, request.SourceIndex);
    }
}
