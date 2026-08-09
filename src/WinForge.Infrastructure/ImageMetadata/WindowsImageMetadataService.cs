using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.ImageMetadata;

namespace WinForge.Infrastructure.ImageMetadata;

/// <summary>
/// Read-only Windows image metadata inspector. It queries an install.wim /
/// install.esd through <c>dism.exe /Get-ImageInfo</c> in TWO read-only stages and
/// parses the results with <see cref="DismImageInfoParser"/>:
///
/// 1. Enumeration query (no <c>/Index</c>) — lists the image indexes and their
///    reliable list-level fields (Index, Name, Description).
/// 2. One per-index detail query (<c>/Index:&lt;n&gt;</c>) for EACH returned
///    index — supplies Architecture, Version/Build, Edition Id, Installation
///    type, and Languages, which the enumeration query does not report.
///
/// It never mounts, modifies, or services the image. DISM is always invoked with
/// <c>/English</c> so the parsed fields are stable regardless of the host's UI
/// language. Environmental failures (missing tooling, corrupt image, non-zero
/// exit) surface as a <see cref="WindowsImageMetadataResult"/> with
/// <see cref="WindowsImageMetadataStatus.Failed"/>; only cancellation propagates
/// as <see cref="OperationCanceledException"/>.
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

        // Stage A — enumeration query (no /Index). Reliably returns the index
        // list with Name/Description only.
        _logger.Info("Querying image indexes (enumeration)");

        ProcessResult enumRun;
        try
        {
            enumRun = await _processRunner.RunAsync(
                new ProcessRequest
                {
                    FileName = "dism.exe",
                    Arguments = $"/Get-ImageInfo /ImageFile:\"{imagePath}\" /English"
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
            return Failed(imagePath, "The Windows image could not be queried.", imageType);
        }

        if (enumRun.ExitCode != 0)
        {
            _logger.Warning($"DISM exited with code {enumRun.ExitCode} while enumerating image indexes.");
            return Failed(imagePath, "The Windows image could not be read.", imageType);
        }

        var enumerated = DismImageInfoParser.ParseImageList(enumRun.StandardOutput);
        if (enumerated.Count == 0)
        {
            _logger.Warning("DISM enumeration returned no image indexes.");
            return Failed(imagePath, "No image indexes were found in the source.", imageType);
        }

        _logger.Info($"Enumerated {enumerated.Count} image index(es)");

        // Stage B — one per-index detail query for EVERY enumerated index. Index
        // numbers are not assumed sequential and are not assumed to map to a
        // specific edition (Home/Pro).
        var result = new WindowsImageMetadataResult
        {
            ImagePath = imagePath,
            ImageType = imageType,
            Editions = enumerated.ToList()
        };

        foreach (var edition in result.Editions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _logger.Debug($"Querying detailed metadata for index {edition.Index}");
                var detailRun = await _processRunner.RunAsync(
                    new ProcessRequest
                    {
                        FileName = "dism.exe",
                        Arguments = $"/Get-ImageInfo /ImageFile:\"{imagePath}\" /Index:{edition.Index} /English"
                    },
                    cancellationToken);

                if (detailRun.ExitCode != 0)
                {
                    _logger.Warning($"DISM exited with code {detailRun.ExitCode} querying index {edition.Index}.");
                    MarkDetailFailed(edition, "Detailed metadata for this edition could not be read.");
                    continue;
                }

                var detail = DismImageInfoParser.ParseImageDetails(detailRun.StandardOutput);
                if (detail is null)
                {
                    _logger.Warning($"DISM returned no detail for index {edition.Index}.");
                    MarkDetailFailed(edition, "Detailed metadata for this edition could not be read.");
                    continue;
                }

                MergeDetail(edition, detail);
                edition.DetailStatus = WindowsEditionDetailStatus.Queried;
                _logger.Info($"Detected: {edition.Name} / {edition.Architecture} / build {edition.Build}");
            }
            catch (OperationCanceledException)
            {
                // Cancellation propagates; the orchestrator still dismounts the ISO.
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"DISM detail query failed for index {edition.Index}: {ex}");
                MarkDetailFailed(edition, "Detailed metadata for this edition could not be read.");
            }
        }

        ComputeTopLevel(result);
        result.Status = WindowsImageMetadataStatus.Completed;
        _logger.Info($"Metadata inspection completed ({result.Editions.Count} index(es)).");
        return result;
    }

    private static void MarkDetailFailed(WindowsEditionInfo edition, string message)
    {
        // Preserve the enumerated Index/Name/Description; only the detailed
        // fields remain null, and the failure is recorded (logged, not shown raw
        // to the UI) so the UI never silently pretends full metadata arrived.
        edition.DetailStatus = WindowsEditionDetailStatus.Failed;
        edition.DetailErrorMessage = message;
    }

    private static void MergeDetail(WindowsEditionInfo target, WindowsEditionInfo detail)
    {
        target.Architecture = detail.Architecture ?? target.Architecture;
        target.EditionId = detail.EditionId ?? target.EditionId;
        target.Version = detail.Version ?? target.Version;
        target.Build = detail.Build ?? target.Build;
        target.InstallationType = detail.InstallationType ?? target.InstallationType;
        target.DefaultLanguage = detail.DefaultLanguage ?? target.DefaultLanguage;
        target.Name = detail.Name ?? target.Name;
        target.Description = detail.Description ?? target.Description;
        if (detail.Languages.Count > 0)
        {
            target.Languages = detail.Languages;
        }
    }

    private static void ComputeTopLevel(WindowsImageMetadataResult result)
    {
        result.Architecture = Consistent(result.Editions, e => e.Architecture);
        result.Version = Consistent(result.Editions, e => e.Version);
        result.Build = Consistent(result.Editions, e => e.Build);
        result.Languages = ConsistentLanguages(result.Editions);
    }

    private static string? Consistent(List<WindowsEditionInfo> editions, Func<WindowsEditionInfo, string?> selector)
    {
        var distinct = editions
            .Select(selector)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .Distinct()
            .ToList();

        return distinct.Count == 1 ? distinct[0] : null;
    }

    private static List<string>? ConsistentLanguages(List<WindowsEditionInfo> editions)
    {
        var lists = editions
            .Select(e => e.Languages)
            .Where(l => l.Count > 0)
            .ToList();

        // If any edition reports no languages (e.g. its detail query failed),
        // we cannot assert a consistent set.
        if (lists.Count != editions.Count)
        {
            return null;
        }

        var first = lists[0];
        for (var i = 1; i < lists.Count; i++)
        {
            if (!first.SequenceEqual(lists[i]))
            {
                return null;
            }
        }

        return first.ToList();
    }

    private static bool IsEsd(string path)
        => string.Equals(Path.GetExtension(path), ".esd", StringComparison.OrdinalIgnoreCase);

    private static WindowsImageMetadataResult Failed(string? imagePath, string message, WindowsImageType? type = null)
    {
        var imageType = type
            ?? (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)
                ? WindowsImageType.Unknown
                : (string.Equals(Path.GetExtension(imagePath), ".esd", StringComparison.OrdinalIgnoreCase)
                    ? WindowsImageType.Esd
                    : WindowsImageType.Wim));

        return new WindowsImageMetadataResult
        {
            ImagePath = imagePath,
            ImageType = imageType,
            Status = WindowsImageMetadataStatus.Failed,
            ErrorMessage = message
        };
    }
}
