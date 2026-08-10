using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Build;

/// <summary>
/// Pure builder for the oscdimg command line. Kept static and side-effect free so
/// the dual BIOS/UEFI boot command can be unit-tested without invoking a process.
/// The canonical Windows ISO command embeds two boot entries in
/// <c>-bootdata:2</c>: a BIOS entry (<c>p0</c>) using <c>etfsboot.com</c> and a
/// UEFI entry (<c>pEF</c>) using <c>efisys.bin</c>.
/// </summary>
public static class OscdimgArgumentBuilder
{
    /// <summary>
    /// Builds the oscdimg argument string for a dual-boot (BIOS + UEFI) Windows ISO.
    /// </summary>
    public static string Build(string mediaRoot, string etfsBoot, string efisysBin, string outputIso)
    {
        var bootData = $"-bootdata:2#p0,e,b\"{etfsBoot}\"#pEF,e,b\"{efisysBin}\"";
        return $"-m -o -u2 -udfver102 {bootData} \"{mediaRoot}\" \"{outputIso}\"";
    }
}

/// <summary>
/// Windows ADK <c>oscdimg.exe</c> implementation of <see cref="IBootableIsoBuilder"/>.
/// If the tool cannot be located it returns <see cref="IsoBuildResult.ToolMissing"/>
/// so the pipeline can surface the required, friendly message instead of faking
/// ISO creation. The boot files are re-validated before invocation so a missing
/// file fails with a clear error rather than a cryptic tool exit.
/// </summary>
public sealed class OscdimgIsoBuilder : IBootableIsoBuilder
{
    private readonly IAdkToolLocator _locator;
    private readonly IProcessRunner _processRunner;
    private readonly IFileSystem _fileSystem;
    private readonly ILoggerService _logger;

    public OscdimgIsoBuilder(
        IAdkToolLocator locator,
        IProcessRunner processRunner,
        IFileSystem fileSystem,
        ILoggerService logger)
    {
        _locator = locator ?? throw new System.ArgumentNullException(nameof(locator));
        _processRunner = processRunner ?? throw new System.ArgumentNullException(nameof(processRunner));
        _fileSystem = fileSystem ?? throw new System.ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new System.ArgumentNullException(nameof(logger));
    }

    public async Task<IsoBuildResult> BuildAsync(IsoBuildRequest request, CancellationToken cancellationToken = default)
    {
        var oscdimg = _locator.FindOscdimg();
        if (string.IsNullOrWhiteSpace(oscdimg))
        {
            _logger.Warning("Build: oscdimg.exe not found (Windows ADK Deployment Tools required).");
            return IsoBuildResult.ToolNotFound();
        }

        if (!_fileSystem.FileExists(request.BootFileEtfs))
        {
            return IsoBuildResult.Fail("Required BIOS boot file (boot\\etfsboot.com) is missing.", -1);
        }

        if (!_fileSystem.FileExists(request.BootFileEfisys))
        {
            return IsoBuildResult.Fail("Required UEFI boot file (efi\\microsoft\\boot\\efisys.bin) is missing.", -1);
        }

        var arguments = OscdimgArgumentBuilder.Build(request.MediaRoot, request.BootFileEtfs, request.BootFileEfisys, request.OutputIsoPath);

        _logger.Info("Build: invoking oscdimg to create the bootable ISO.");
        var run = await _processRunner.RunAsync(new ProcessRequest
        {
            FileName = oscdimg,
            Arguments = arguments
        }, cancellationToken);

        if (run.ExitCode != 0)
        {
            _logger.Warning($"Build: oscdimg exited with code {run.ExitCode}.");
            return IsoBuildResult.Fail(
                $"ISO creation failed (oscdimg exit {run.ExitCode}).",
                run.ExitCode,
                Normalize(run.StandardOutput),
                Normalize(run.StandardError));
        }

        if (!_fileSystem.FileExists(request.OutputIsoPath))
        {
            return IsoBuildResult.Fail("ISO creation reported success but the output file is missing.", run.ExitCode);
        }

        _logger.Info("Build: bootable ISO created.");
        return IsoBuildResult.Ok(request.OutputIsoPath);
    }

    /// <summary>
    /// Strips the noisy CLIXML decoration some Windows tools emit so the UI never
    /// shows raw CLIXML as the only diagnostic. DISM/oscdimg plain text is passed
    /// through; wrapped CLIXML is reduced to its text content.
    /// </summary>
    private static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        // CLIXML is wrapped in a single #<CLIXML> line; extract only the readable
        // text segments after the marker. Best-effort, non-destructive.
        const string marker = "#<CLIXML>";
        var idx = text.IndexOf(marker, System.StringComparison.Ordinal);
        return idx < 0 ? text : text.Substring(idx + marker.Length).Trim();
    }
}
