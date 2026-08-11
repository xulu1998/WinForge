using System;
using System.IO;
using WinForge.Core.Models;

namespace WinForge.Infrastructure.Customization;

/// <summary>
/// Helpers that map a logical offline hive base name (<c>SOFTWARE</c>,
/// <c>SYSTEM</c>, <c>DEFAULT</c>, <c>DEFAULT_USER</c>) to its on-disk file inside
/// the mounted working image and to the WinForge-owned temporary load name used
/// by <see cref="OfflineRegistryService"/>.
///
/// <para>The file always lives inside the mounted image — it can never be a host
/// hive or the original ISO mount root. <c>DEFAULT_USER</c> maps to the Default
/// User profile (<c>Users\Default\NTUSER.DAT</c>), the template for NEW user
/// accounts, which is how user-level personalization/privacy settings are applied
/// to the offline image (Stage 11.3 ADR-052). The host user's HKCU is never
/// touched.</para>
/// </summary>
public static class OfflineHivePaths
{
    private static readonly string[] KnownBases = { "SOFTWARE", "SYSTEM", "DEFAULT", "DEFAULT_USER" };

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

        // DEFAULT_USER is the Default User profile template (new users), NOT the
        // system hive in Windows\System32\config.
        if (string.Equals(hiveBase, "DEFAULT_USER", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(workspace.MountDirectory!, "Users", "Default", "NTUSER.DAT");
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

    /// <summary>
    /// Normalizes a logical registry key path so it is strictly <b>relative to the
    /// loaded hive root</b> — i.e. it can never accidentally duplicate the hive
    /// base after the hive has already been loaded.
    ///
    /// <para>
    /// For example, after the SOFTWARE hive is loaded under
    /// <c>HKLM\WinForge_SOFTWARE</c>, a path of
    /// <c>SOFTWARE\Microsoft\Windows\...</c> (or <c>HKLM\SOFTWARE\Microsoft\...</c>)
    /// would otherwise be written to
    /// <c>HKLM\WinForge_SOFTWARE\SOFTWARE\Microsoft\...</c> — a location that does
    /// not correspond to the live <c>HKLM\SOFTWARE\Microsoft\...</c> the operator
    /// intended. This method strips any leading <c>HKLM\</c> designator and any
    /// leading occurrence of the hive base segment so the result is always
    /// <c>Microsoft\Windows\...</c>. The fix is idempotent (a correctly relative
    /// path is returned unchanged).
    /// </para>
    /// </summary>
    public static string NormalizeKeyPath(string hiveBase, string keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            return keyPath ?? string.Empty;
        }

        // Normalize separators and reject escape markers up front.
        var working = keyPath.Replace('/', '\\');
        if (working.Contains(".."))
        {
            throw new ArgumentException("Key path must not contain '..' (relative escape).", nameof(keyPath));
        }

        // Drop a leading "HKLM\" designator if present (a host-style absolute path
        // that slipped through). The remaining path is still relative to the root.
        const string hklm = "HKLM\\";
        while (working.StartsWith(hklm, StringComparison.OrdinalIgnoreCase))
        {
            working = working.Substring(hklm.Length);
        }

        // Drop any leading occurrence of the hive base (e.g. "SOFTWARE\") so the
        // path is never written under a duplicated hive-base segment.
        var basePrefix = (hiveBase ?? string.Empty) + "\\";
        while (basePrefix.Length > 1 &&
               working.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase) &&
               working.Length > basePrefix.Length)
        {
            working = working.Substring(basePrefix.Length);
        }

        return working;
    }
}
