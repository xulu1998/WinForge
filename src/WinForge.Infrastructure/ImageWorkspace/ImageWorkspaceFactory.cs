using System.Collections.Generic;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.WimEngine;

/// <summary>
/// Pure, read-only builder of a durable <see cref="ImageWorkspace"/> from a
/// Phase 2 inspection result plus the user's selected edition. It performs no
/// I/O, no DISM, no mount, and never captures a temporary mounted drive letter:
/// the relative install-image path is derived from the detected image type, and
/// the source is the original ISO path supplied to inspection.
/// </summary>
public sealed class ImageWorkspaceFactory : IImageWorkspaceFactory
{
    public ImageWorkspaceBuildResult BuildWorkspace(IsoInspectionResult inspection, WindowsEditionInfo? selectedEdition)
    {
        if (inspection is null || string.IsNullOrWhiteSpace(inspection.IsoPath))
        {
            return NotReady("Source ISO path is missing.");
        }

        if (inspection.Status != IsoInspectionStatus.Completed)
        {
            return NotReady("ISO inspection did not complete successfully.");
        }

        if (inspection.InstallImageType == InstallImageType.Unknown)
        {
            return NotReady("Install image type is unknown.");
        }

        var metadata = inspection.ImageMetadata;
        if (metadata is null || metadata.Status != WindowsImageMetadataStatus.Completed || metadata.Editions.Count == 0)
        {
            return NotReady("Windows image metadata is missing or failed.");
        }

        if (selectedEdition is null)
        {
            return NotReady("No edition has been selected.");
        }

        var match = FindByIndex(metadata.Editions, selectedEdition.Index);
        if (match is null)
        {
            return Invalid(
                $"Selected index {selectedEdition.Index} is not present in the inspected editions.");
        }

        var imageType = inspection.InstallImageType == InstallImageType.Esd
            ? WindowsImageType.Esd
            : WindowsImageType.Wim;

        var relativePath = ImageWorkspace.NormalizeRelativePath(
            imageType == WindowsImageType.Esd ? "sources/install.esd" : "sources/install.wim");

        var languages = match.Languages.Count > 0
            ? new List<string>(match.Languages)
            : metadata.Languages is { Count: > 0 } ml
                ? new List<string>(ml)
                : new List<string>();

        var workspace = new ImageWorkspace
        {
            SourceIsoPath = inspection.IsoPath,
            ImageRelativePath = relativePath,
            ImageType = imageType,
            SelectedIndex = match.Index,
            SelectedEditionName = match.Name,
            Architecture = match.Architecture ?? metadata.Architecture,
            Version = match.Version ?? metadata.Version,
            Build = match.Build ?? metadata.Build,
            Languages = languages
        };

        return new ImageWorkspaceBuildResult(workspace, ImageWorkspaceStatus.Ready, System.Array.Empty<string>());
    }

    private static WindowsEditionInfo? FindByIndex(IReadOnlyList<WindowsEditionInfo> editions, int index)
    {
        foreach (var edition in editions)
        {
            if (edition.Index == index)
            {
                return edition;
            }
        }

        return null;
    }

    private static ImageWorkspaceBuildResult NotReady(string issue)
        => new(null, ImageWorkspaceStatus.NotReady, new[] { issue });

    private static ImageWorkspaceBuildResult Invalid(string issue)
        => new(null, ImageWorkspaceStatus.Invalid, new[] { issue });
}
