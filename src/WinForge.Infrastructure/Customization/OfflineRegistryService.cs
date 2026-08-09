using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Customization;

/// <summary>
/// Windows DISM/Win32-backed implementation of <see cref="IOfflineRegistryService"/>
/// (Step 3.3 section F). It edits an OFFLINE registry hive loaded from the mounted
/// working image — never the host OS registry.
///
/// <para>
/// The hive is loaded under a <b>WinForge-owned temporary name</b>
/// (<c>WinForge_&lt;BASE&gt;</c>) via <c>RegLoadKey</c>, edited through the
/// standard <see cref="Registry"/> API under that name, then <b>always</b>
/// unloaded via <c>RegUnLoadKey</c> (including on exception). The temporary name
/// is validated to be a single safe segment so it can never collide with or
/// redirect into a real host hive. Core never shells out to <c>reg.exe</c>.
/// </para>
///
/// <para>All operations are confined to the loaded hive; there is no path that can
/// reach the host's live registry.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OfflineRegistryService : IOfflineRegistryService
{
    // A WinForge-owned hive name is exactly "WinForge_" + an alphanumeric/underscore
    // base. This guarantees it cannot be a host hive (HKLM\SOFTWARE, etc.) and
    // cannot contain a separator that would escape the loaded root.
    private static readonly System.Text.RegularExpressions.Regex SafeHiveNameRegex =
        new(@"^WinForge_[A-Za-z0-9_]{1,80}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public OfflineHiveHandle LoadHive(string hiveFilePath, string hiveName)
    {
        if (string.IsNullOrWhiteSpace(hiveFilePath))
        {
            throw new ArgumentException("Hive file path is required.", nameof(hiveFilePath));
        }

        if (!SafeHiveNameRegex.IsMatch(hiveName))
        {
            throw new ArgumentException(
                "Hive name must be a WinForge-owned safe segment (WinForge_<base>).", nameof(hiveName));
        }

        if (!System.IO.File.Exists(hiveFilePath))
        {
            throw new System.IO.FileNotFoundException("Offline hive file not found.", hiveFilePath);
        }

        // Load under HKLM\<hiveName>. RegLoadKey requires the target not already be
        // loaded; if it is (a leaked prior load), refuse rather than clobber it.
        var rc = RegLoadKey(
            (int)RegistryHive.LocalMachine,
            hiveName,
            hiveFilePath);
        if (rc != 0)
        {
            throw new Win32Exception(rc, $"Failed to load offline hive '{hiveName}' (Win32 {rc}).");
        }

        return new OfflineHiveHandle(hiveFilePath, hiveName);
    }

    public void UnloadHive(OfflineHiveHandle handle)
    {
        if (handle is null || !handle.IsLoaded)
        {
            return;
        }

        // Best effort: unloading may transiently fail if a handle is still open;
        // we still mark the handle as unloaded so callers cannot double-unload.
        try
        {
            RegUnLoadKey((int)RegistryHive.LocalMachine, handle.HiveName);
        }
        finally
        {
            handle.IsLoaded = false;
        }
    }

    public void SetValue(OfflineHiveHandle handle, string keyPath, string valueName, OfflineRegistryValueKind kind, string data)
    {
        Validate(handle, keyPath, valueName);

        using var root = OpenLoadedRoot(handle, writable: true);
        using var key = EnsureKeyPath(root, keyPath);
        var (nativeKind, value) = ConvertToNative(kind, data);
        key.SetValue(valueName, value, nativeKind);
    }

    public void DeleteValue(OfflineHiveHandle handle, string keyPath, string valueName)
    {
        Validate(handle, keyPath, valueName);

        using var root = OpenLoadedRoot(handle, writable: true);
        using var key = root.OpenSubKey(keyPath, writable: true);
        if (key is null)
        {
            // Key not present: nothing to delete. Treated as success (idempotent).
            return;
        }

        if (key.GetValue(valueName) is null)
        {
            return;
        }

        key.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public string? GetValue(OfflineHiveHandle handle, string keyPath, string valueName)
    {
        Validate(handle, keyPath, valueName);

        using var root = OpenLoadedRoot(handle, writable: false);
        using var key = root.OpenSubKey(keyPath, writable: false);
        if (key is null)
        {
            return null;
        }

        var obj = key.GetValue(valueName);
        return obj switch
        {
            null => null,
            int i => i.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string s => s,
            string[] multi => string.Join("\n", multi),
            byte[] bytes => Convert.ToHexString(bytes),
            _ => obj.ToString()
        };
    }

    public IReadOnlyList<string> EnumSubKeys(OfflineHiveHandle handle, string keyPath)
    {
        if (handle is null || !handle.IsLoaded)
        {
            throw new ArgumentException("Hive is not loaded.", nameof(handle));
        }

        if (string.IsNullOrWhiteSpace(keyPath))
        {
            throw new ArgumentException("Key path is required.", nameof(keyPath));
        }

        using var root = OpenLoadedRoot(handle, writable: false);
        using var key = root.OpenSubKey(keyPath, writable: false);
        if (key is null)
        {
            return Array.Empty<string>();
        }

        return key.GetSubKeyNames();
    }

    // ---- internals ----

    private static void Validate(OfflineHiveHandle handle, string keyPath, string valueName)
    {
        if (handle is null || !handle.IsLoaded)
        {
            throw new ArgumentException("Hive is not loaded.", nameof(handle));
        }

        if (string.IsNullOrWhiteSpace(keyPath))
        {
            throw new ArgumentException("Key path is required.", nameof(keyPath));
        }

        if (string.IsNullOrWhiteSpace(valueName))
        {
            throw new ArgumentException("Value name is required.", nameof(valueName));
        }

        // The key path must be relative to the loaded hive root and must not try
        // to escape via ".." or absolute markers.
        if (keyPath.Contains("..") || keyPath.StartsWith("\\") || keyPath.StartsWith("/"))
        {
            throw new ArgumentException("Key path must be relative to the loaded hive.", nameof(keyPath));
        }
    }

    private static RegistryKey OpenLoadedRoot(OfflineHiveHandle handle, bool writable)
    {
        var root = Registry.LocalMachine.OpenSubKey(handle.HiveName, writable: writable);
        if (root is null)
        {
            throw new InvalidOperationException(
                $"Loaded hive '{handle.HiveName}' is not accessible; it may have been unloaded.");
        }

        return root;
    }

    private static RegistryKey EnsureKeyPath(RegistryKey root, string keyPath)
    {
        // Recreate the relative path under the loaded root, creating sub-keys as
        // needed (so a registry setting can be written even if the key is absent).
        RegistryKey current = root;
        var segments = keyPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var next = current.OpenSubKey(segment, writable: true) ?? current.CreateSubKey(segment);
            current.Dispose();
            current = next;
        }

        return current;
    }

    private static (RegistryValueKind, object) ConvertToNative(OfflineRegistryValueKind kind, string data)
    {
        switch (kind)
        {
            case OfflineRegistryValueKind.DWord:
                if (!int.TryParse(data, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var dword))
                {
                    throw new FormatException($"Cannot parse DWORD value '{data}'.");
                }

                return (RegistryValueKind.DWord, dword);

            case OfflineRegistryValueKind.QWord:
                if (!long.TryParse(data, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var qword))
                {
                    throw new FormatException($"Cannot parse QWORD value '{data}'.");
                }

                return (RegistryValueKind.QWord, qword);

            case OfflineRegistryValueKind.String:
                return (RegistryValueKind.String, data ?? string.Empty);

            case OfflineRegistryValueKind.ExpandString:
                return (RegistryValueKind.ExpandString, data ?? string.Empty);

            case OfflineRegistryValueKind.MultiString:
                var parts = (data ?? string.Empty)
                    .Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries);
                return (RegistryValueKind.MultiString, parts);

            case OfflineRegistryValueKind.Binary:
                var bytes = Convert.FromHexString(data ?? string.Empty);
                return (RegistryValueKind.Binary, bytes);

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    // ---- Win32 P/Invoke ----

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern int RegLoadKey(int hKey, string lpSubKey, string lpFile);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern int RegUnLoadKey(int hKey, string lpSubKey);
}
