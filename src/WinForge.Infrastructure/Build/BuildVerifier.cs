using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.ImageMetadata;

namespace WinForge.Infrastructure.Build;

/// <summary>
/// Independently verifies a produced ISO and its media tree. Success is NEVER
/// derived from the oscdimg exit code alone: the output must exist and have size,
/// the final install.wim must be present and queryable with the expected
/// edition/index, and no WIM may remain mounted. As a best-effort extra, the
/// produced ISO is mounted read-only to confirm sources\install.wim and the boot
/// files survive into the image; a mount failure does not fail verification.
/// </summary>
public sealed class BuildVerifier : IBuildVerifier
{
    private readonly IFileSystem _fileSystem;
    private readonly IProcessRunner _processRunner;
    private readonly IIsoMountService _isoMount;
    private readonly ILoggerService _logger;

    public BuildVerifier(
        IFileSystem fileSystem,
        IProcessRunner processRunner,
        IIsoMountService isoMount,
        ILoggerService logger)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _isoMount = isoMount ?? throw new ArgumentNullException(nameof(isoMount));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BuildVerificationResult> VerifyAsync(BuildVerificationRequest request, CancellationToken cancellationToken = default)
    {
        var outputExists = _fileSystem.FileExists(request.OutputIsoPath);
        var outputSize = outputExists ? _fileSystem.GetFileSize(request.OutputIsoPath) : 0;
        var installWimPresent = _fileSystem.FileExists(request.ExpectedInstallWimPath);

        // No WIM may remain mounted after the build.
        var mountedPresent = await AnyWimMountedAsync(cancellationToken);

        // The expected edition/index must be present in the final WIM.
        var editionPresent = installWimPresent &&
                             await EditionPresentAsync(request.ExpectedInstallWimPath, request.ExpectedIndex, request.ExpectedEditionName, cancellationToken);

        // Best-effort: mount the produced ISO and confirm its contents. A failure
        // here is non-fatal — the media-tree checks above are authoritative.
        await BestEffortVerifyIsoContentsAsync(request.OutputIsoPath, cancellationToken);

        var success = outputExists && outputSize > 0 && installWimPresent && !mountedPresent && editionPresent;
        if (!success)
        {
            var detail = !outputExists ? "the output ISO is missing"
                : outputSize <= 0 ? "the output ISO is empty"
                : !installWimPresent ? "the final install.wim is missing from the media tree"
                : mountedPresent ? "a WIM image is still mounted"
                : "the expected edition/index is not present in the final WIM";
            return BuildVerificationResult.Fail(detail, outputExists, outputSize, installWimPresent, mountedPresent, editionPresent);
        }

        _logger.Info("Build: verification passed.");
        return BuildVerificationResult.Pass(outputSize, installWimPresent, editionPresent);
    }

    private async Task<bool> AnyWimMountedAsync(CancellationToken cancellationToken)
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
                    trimmed.Substring("Mount Dir :".Length).Trim().Length > 0)
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

    private async Task<bool> EditionPresentAsync(string wimPath, int index, string? expectedEdition, CancellationToken cancellationToken)
    {
        try
        {
            var run = await _processRunner.RunAsync(new ProcessRequest
            {
                FileName = "dism.exe",
                Arguments = $"/English /Get-ImageInfo /ImageFile:\"{wimPath}\" /Index:{index}"
            }, cancellationToken);

            if (run.ExitCode != 0)
            {
                return false;
            }

            var info = DismImageInfoParser.ParseImageDetails(run.StandardOutput);
            if (info is null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(expectedEdition))
            {
                return true;
            }

            return string.Equals(info.Name, expectedEdition, StringComparison.OrdinalIgnoreCase)
                   || (info.Name?.IndexOf(expectedEdition, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        catch
        {
            return false;
        }
    }

    private async Task BestEffortVerifyIsoContentsAsync(string isoPath, CancellationToken cancellationToken)
    {
        if (!_fileSystem.FileExists(isoPath))
        {
            return;
        }

        string? isoRoot = null;
        try
        {
            isoRoot = await _isoMount.MountReadOnlyAsync(isoPath, cancellationToken);
            var installWim = _fileSystem.PathCombine(isoRoot.TrimEnd('\\', '/'), "sources", "install.wim");
            var etfs = _fileSystem.PathCombine(isoRoot.TrimEnd('\\', '/'), "boot", "etfsboot.com");
            var efisys = _fileSystem.PathCombine(isoRoot.TrimEnd('\\', '/'), "efi", "microsoft", "boot", "efisys.bin");
            if (!_fileSystem.FileExists(installWim))
            {
                _logger.Warning("Build: best-effort ISO check — sources\\install.wim not found in produced ISO.");
            }

            if (!_fileSystem.FileExists(etfs) || !_fileSystem.FileExists(efisys))
            {
                _logger.Warning("Build: best-effort ISO check — boot files not found in produced ISO.");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Build: best-effort ISO mount verification skipped ({ex.Message}).");
        }
        finally
        {
            if (isoRoot is not null)
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
    }
}
