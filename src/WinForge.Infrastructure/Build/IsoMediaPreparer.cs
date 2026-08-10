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

        // A previous (interrupted/failed) attempt may have left a partial media tree
        // carrying ReadOnly files. Reusing it silently would re-trigger the
        // "Access to the path 'autorun.inf' is denied" failure, so start from a
        // deterministic, clean slate. The SOURCE ISO is never touched by this.
        if (_fileSystem.DirectoryExists(request.BuildMediaRoot))
        {
            _logger.Info("Build: removing any prior media tree before recopy.");
            _fileSystem.DeleteDirectory(request.BuildMediaRoot, recursive: true);
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
            _logger.Error($"Build: media preparation failed: {ex.GetType().Name}: {ex.Message}");
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
        // The build copy must be manageable by WinForge (overwrite payload, clean up),
        // so clear any protected attributes the mounted source carries (ReadOnly etc.).
        NormalizeWritable(destination);

        foreach (var file in _fileSystem.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            var dest = _fileSystem.PathCombine(destination, name);
            try
            {
                _fileSystem.CopyFile(file, dest, overwrite: true);
                // Explicitly clear ReadOnly/System/Hidden on the destination copy so
                // later payload replacement and cleanup cannot fail on a protected file.
                NormalizeWritable(dest);
            }
            catch (Exception ex)
            {
                // Surface a precise, actionable error instead of a bare
                // "Access to the path 'autorun.inf' is denied.": include source/dest,
                // their attributes, the operation, and the exception type.
                var srcAttrs = SafeGetAttributes(file);
                var dstAttrs = _fileSystem.FileExists(dest) ? SafeGetAttributes(dest) : "(does not exist)";
                _logger.Error(
                    $"Build: media copy failed | op=CopyFile | source='{file}' ({srcAttrs}) | " +
                    $"dest='{dest}' ({dstAttrs}) | {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        foreach (var dir in _fileSystem.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
            CopyTree(dir, _fileSystem.PathCombine(destination, name));
        }
    }

    // Attributes that block WinForge from overwriting/replacing/deleting its own
    // build copy. Cleared on destination copies only; the mounted source is never
    // modified.
    private const System.IO.FileAttributes BlockingAttributes =
        System.IO.FileAttributes.ReadOnly | System.IO.FileAttributes.System | System.IO.FileAttributes.Hidden;

    private void NormalizeWritable(string path)
    {
        try
        {
            var attrs = _fileSystem.GetAttributes(path);
            if ((attrs & BlockingAttributes) != 0)
            {
                _fileSystem.SetAttributes(path, attrs & ~BlockingAttributes);
            }
        }
        catch
        {
            // Best effort — WindowsFileSystem already normalizes on its own; this is
            // the explicit, testable policy layer for fake filesystems.
        }
    }

    private string SafeGetAttributes(string path)
    {
        try
        {
            return _fileSystem.GetAttributes(path).ToString();
        }
        catch
        {
            return "(unreadable)";
        }
    }

    private bool ValidateBootFiles(string mediaRoot)
    {
        var etfs = _fileSystem.PathCombine(mediaRoot, "boot", "etfsboot.com");
        var efisys = _fileSystem.PathCombine(mediaRoot, "efi", "microsoft", "boot", "efisys.bin");
        return _fileSystem.FileExists(etfs) && _fileSystem.FileExists(efisys);
    }
}
