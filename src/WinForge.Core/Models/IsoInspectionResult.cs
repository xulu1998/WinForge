namespace WinForge.Core.Models;

/// <summary>
/// Structured, read-only result of inspecting a Windows ISO. Step 2.1 performs
/// safe, non-destructive inspection only: it validates the file, optionally
/// mounts it read-only, and inspects the on-disk directory layout. It does NOT
/// parse WIM/ESD contents, editions, or Windows versions.
/// </summary>
public sealed class IsoInspectionResult
{
    /// <summary>Path that was inspected (or attempted).</summary>
    public string? IsoPath { get; set; }

    /// <summary>File name portion of <see cref="IsoPath"/>.</summary>
    public string? FileName { get; set; }

    /// <summary>Size of the ISO file in bytes.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>Whether the file exists on disk.</summary>
    public bool Exists { get; set; }

    /// <summary>Whether the file has a <c>.iso</c> extension.</summary>
    public bool ExtensionValid { get; set; }

    /// <summary>Whether the file could be opened for reading.</summary>
    public bool IsReadable { get; set; }

    /// <summary>Coarse detection outcome.</summary>
    public IsoDetectedType DetectedType { get; set; } = IsoDetectedType.Unknown;

    /// <summary>Outcome of the inspection run.</summary>
    public IsoInspectionStatus Status { get; set; } = IsoInspectionStatus.NotInspected;

    /// <summary>User-facing error description when <see cref="Status"/> is Failed.</summary>
    public string? ErrorMessage { get; set; }

    // Structure details — populated only after a successful read-only mount.

    /// <summary>Found a <c>\boot</c> directory at the ISO root.</summary>
    public bool HasBootDirectory { get; set; }

    /// <summary>Found a <c>\sources</c> directory at the ISO root.</summary>
    public bool HasSourcesDirectory { get; set; }

    /// <summary>Found <c>\sources\boot.wim</c>.</summary>
    public bool HasBootWim { get; set; }

    /// <summary>Found <c>\sources\install.wim</c>.</summary>
    public bool HasInstallWim { get; set; }

    /// <summary>Found <c>\sources\install.esd</c>.</summary>
    public bool HasInstallEsd { get; set; }

    /// <summary>Type of install image detected (WIM / ESD / Unknown).</summary>
    public InstallImageType InstallImageType { get; set; } = InstallImageType.Unknown;

    /// <summary>
    /// Read-only metadata read from the install image (Step 2.2). Populated only
    /// when the layout inspection found an install.wim/install.esd and the
    /// metadata query succeeded or failed. Null when no install image exists.
    /// </summary>
    public WindowsImageMetadataResult? ImageMetadata { get; set; }

    public static IsoInspectionResult NotInspected(string? isoPath) => new()
    {
        IsoPath = isoPath,
        Status = IsoInspectionStatus.NotInspected
    };

    public static IsoInspectionResult Failed(string? isoPath, string message) => new()
    {
        IsoPath = isoPath,
        Status = IsoInspectionStatus.Failed,
        ErrorMessage = message
    };
}
