using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Build;

/// <summary>
/// Copies the original ISO media tree into an isolated WinForge-owned build
/// workspace (read-only mount of the source, never modified) and replaces its
/// install image payload with the customized final WIM. Preserves the original
/// media structure (boot files, EFI boot support, setup files, metadata) so the
/// rebuilt ISO is structurally faithful. For an ESD source, the original
/// <c>install.esd</c> is removed and the customized payload is written as
/// <c>install.wim</c>, so Setup references the resulting WIM correctly. The
/// required dual-boot files are validated in the copied tree so a missing file
/// fails the build with a clear error.
/// </summary>
public sealed class IsoMediaPreparer : IIsoMediaPreparer
{
    private readonly IIsoMountService _isoMount;
    private readonly IFileSystem _fileSystem;
    private readonly ILoggerService _logger;

    public IsoMediaPreparer(IIsoMountService isoMount, IFileSystem fileSystem, ILoggerService logger)
    {
        _isoMount = isoMount ?? throw new ArgumentNullException(nameof(isoMount));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<MediaPrepareResult> PrepareAsync(MediaPrepareRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.SourceIsoPath) || string.IsNullOrWhiteSpace(request.BuildMediaRoot))
        {
            return MediaPrepareResult.Fail("Media prepare request is missing the source ISO or build media root.");
        }

        if (!_fileSystem.FileExists(request.SourceIsoPath))
        {
            return MediaPrepareResult.Fail("The source ISO was not found.");
        }

        if (string.IsNullOrWhiteSpace(request.FinalInstallWimPath) || !_fileSystem.FileExists(request.FinalInstallWimPath))
        {
            return MediaPrepareResult.Fail("The final install.wim to embed was not found.");
        }

        _fileSystem.CreateDirectory(request.BuildMediaRoot);

        string? isoRoot = null;
        try
        {
            _logger.Info("Build: mounting source ISO read-only to copy media tree.");
            isoRoot = await _isoMount.MountReadOnlyAsync(request.SourceIsoPath, cancellationToken);

            _logger.Info("Build: copying original media tree into the build workspace.");
            CopyTree(isoRoot, request.BuildMediaRoot);

            var bootFilesPresent = ValidateBootFiles(request.BuildMediaRoot);

            // Replace the install image payload with the customized final WIM.
            var sourcesDir = _fileSystem.PathCombine(request.BuildMediaRoot, "sources");
            var originalInstallFile = Path.GetFileName(ImageWorkspace.NormalizeRelativePath(request.SourceImageRelativePath));
            var originalInstallPath = _fileSystem.PathCombine(sourcesDir, originalInstallFile);
            if (_fileSystem.FileExists(originalInstallPath))
            {
                _fileSystem.DeleteFile(originalInstallPath);
            }

            var finalInstallPath = _fileSystem.PathCombine(sourcesDir, "install.wim");
            _fileSystem.CopyFile(request.FinalInstallWimPath, finalInstallPath, overwrite: true);

            if (!_fileSystem.FileExists(finalInstallPath))
            {
                return MediaPrepareResult.Fail("The customized install.wim could not be written into the media tree.");
            }

            _logger.Info("Build: source media copied; install image replaced.");
            return MediaPrepareResult.Ok(request.BuildMediaRoot, finalInstallPath, bootFilesPresent);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error($"Build: media preparation failed: {ex.Message}");
            return MediaPrepareResult.Fail("Media preparation failed unexpectedly.");
        }
        finally
        {
            if (isoRoot is not null)
            {
                try
                {
                    await _isoMount.DismountAsync(request.SourceIsoPath, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Build: source ISO dismount issue: {ex.Message}");
                }
            }
        }
    }

    private void CopyTree(string source, string destination)
    {
        _fileSystem.CreateDirectory(destination);

        foreach (var file in _fileSystem.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            _fileSystem.CopyFile(file, _fileSystem.PathCombine(destination, name), overwrite: true);
        }

        foreach (var dir in _fileSystem.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
            CopyTree(dir, _fileSystem.PathCombine(destination, name));
        }
    }

    private bool ValidateBootFiles(string mediaRoot)
    {
        var etfs = _fileSystem.PathCombine(mediaRoot, "boot", "etfsboot.com");
        var efisys = _fileSystem.PathCombine(mediaRoot, "efi", "microsoft", "boot", "efisys.bin");
        return _fileSystem.FileExists(etfs) && _fileSystem.FileExists(efisys);
    }
}
