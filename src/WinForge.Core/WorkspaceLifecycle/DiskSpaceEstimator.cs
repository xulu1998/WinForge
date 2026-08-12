using System;

namespace WinForge.Core.WorkspaceLifecycle;

/// <summary>
/// Conservative free-disk estimation for Prepare / Build (Part L/M). Pure logic,
/// fully testable. Estimates are intentionally conservative (safety margin
/// included) so WinForge never waits until the system drive hits zero.
/// </summary>
public static class DiskSpaceEstimator
{
    /// <summary>Safety margin added to every estimate (checkpoint + fragmentation headroom).</summary>
    public const long SafetyMarginBytes = 2L * 1024 * 1024 * 1024; // 2 GiB

    /// <summary>Rough unpacked size of a Windows install.wim while mounted (conservative: ~3x compressed WIM).</summary>
    public const long UnpackedMultiplierBytesPerWimByte = 3;

    /// <summary>Fixed ISO media-tree staging overhead when no source WIM size is known yet.</summary>
    public const long DefaultMediaTreeBytes = 5L * 1024 * 1024 * 1024; // 5 GiB

    /// <summary>
    /// Estimate for the Prepare step: isolated working WIM copy + unpacked mount
    /// overhead + safety margin.
    /// </summary>
    public static long EstimatePrepare(long workingWimBytes)
        => workingWimBytes
           + workingWimBytes * UnpackedMultiplierBytesPerWimByte
           + SafetyMarginBytes;

    /// <summary>
    /// Estimate for the Build step: committed working WIM + media staging tree +
    /// final ISO (~source size) + checkpoint margin.
    /// </summary>
    public static long EstimateBuild(long sourceIsoBytes, long workingWimBytes)
        => workingWimBytes
           + (sourceIsoBytes > 0 ? sourceIsoBytes : DefaultMediaTreeBytes)
           + Math.Max(sourceIsoBytes, workingWimBytes) // final ISO ≈ media tree size
           + SafetyMarginBytes;

    /// <summary>
    /// True when the currently free bytes are below the required estimate —
    /// block the operation before starting (Part L).
    /// </summary>
    public static bool IsInsufficient(long freeBytes, long requiredBytes)
        => freeBytes < requiredBytes;

    /// <summary>Formats bytes for the UI, e.g. "23.4 GB".</summary>
    public static string FormatBytes(long bytes)
    {
        const double gb = 1024.0 * 1024 * 1024;
        const double mb = 1024.0 * 1024;
        if (bytes >= gb)
        {
            return (bytes / gb).ToString("0.#") + " GB";
        }

        if (bytes >= mb)
        {
            return (bytes / mb).ToString("0.#") + " MB";
        }

        return bytes.ToString("N0") + " B";
    }
}
