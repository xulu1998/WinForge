using System;
using System.IO;
using WinForge.Core.Models;

namespace WinForge.Infrastructure.Customization;

/// <summary>
/// Helpers that map a logical offline hive base name (<c>SOFTWARE</c>,
/// <c>SYSTEM</c>, <c>DEFAULT</c>) to its on-disk file inside the mounted working
/// image and to the WinForge-owned temporary load name used by
/// <see cref="OfflineRegistryService"/>.
///
/// <para>The file always lives under the mounted image's
/// <c>Windows\System32\config</c> directory, i.e. strictly inside the mount — it
/// can never be a host hive or the original ISO mount root.</para>
/// </summary>
public static class OfflineHivePaths
{
    private static readonly string[] KnownBases = { "SOFTWARE", "SYSTEM", "DEFAULT" };

    /// <summary>
    /// Returns the on-disk hive file path for the given logical base inside the
    /// mounted working image. Returns null when the base is unknown or the
    /// workspace has no mount directory.
    /// </summary>
    public static string? GetHiveFilePath(ImageServicingWorkspace workspace, string hiveBase)
    {
        if (workspace?.MountDirectory is null)
        {
            return null;
        }

        if (!IsKnownBase(hiveBase))
        {
            return null;
        }

        return Path.Combine(
            workspace.MountDirectory!,
            "Windows",
            "System32",
            "config",
            hiveBase);
    }

    /// <summary>
    /// Returns the WinForge-owned temporary load name for a logical base, e.g.
    /// <c>WinForge_SOFTWARE</c>. This is the only name under which a hive may be
    /// loaded by <see cref="OfflineRegistryService"/>.
    /// </summary>
    public static string GetWinForgeHiveName(string hiveBase)
        => "WinForge_" + hiveBase;

    public static bool IsKnownBase(string hiveBase)
        => Array.Exists(KnownBases, b => string.Equals(b, hiveBase, StringComparison.OrdinalIgnoreCase));
}
