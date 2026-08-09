using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
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

    private readonly ILoggerService _logger;

    public OfflineRegistryService(ILoggerService logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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

        // Diagnostics: log the requested hive file (mount path redacted so no
        // local filesystem layout is leaked) and the WinForge-owned temporary
        // HKLM name actually used. None of this is host-registry data — it is the
        // OFFLINE image's hive, loaded strictly under HKLM\<WinForge_*>.
        _logger.Info($"OfflineRegistry: loading offline hive '{hiveName}' from '{RedactHivePath(hiveFilePath)}'.");

        // RegLoadKey requires SeRestorePrivilege (and RegUnLoadKey SeBackupPrivilege),
        // which are present in an elevated token but usually DISABLED by default.
        // Enable them before the call — without this, hive load fails with
        // ERROR_PRIVILEGE_NOT_HELD on a real elevated Windows session, which is
        // exactly why offline service discovery could silently return zero. The
        // enablement result is logged so a privilege failure is observable.
        EnableRequiredPrivileges();

        // Load under HKLM\<hiveName>. RegLoadKey requires the target not already be
        // loaded; if it is (a leaked prior load), refuse rather than clobber it.
        var rc = RegLoadKey(
            (int)RegistryHive.LocalMachine,
            hiveName,
            hiveFilePath);
        if (rc != 0)
        {
            var msg = new Win32Exception(rc).Message;
            _logger.Error($"OfflineRegistry: RegLoadKey FAILED (return code {rc}: {msg}) for hive '{hiveName}'. The offline hive was NOT loaded.");
            throw new Win32Exception(rc, $"Failed to load offline hive '{hiveName}' (Win32 {rc}: {msg}).");
        }

        _logger.Info($"OfflineRegistry: RegLoadKey OK (return code 0) — hive '{hiveName}' loaded under HKLM.");
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
        // The RegUnLoadKey return code is logged so a leaked/unloadable hive is
        // observable rather than silently ignored.
        try
        {
            EnableRequiredPrivileges();
            var rc = RegUnLoadKey((int)RegistryHive.LocalMachine, handle.HiveName);
            if (rc != 0)
            {
                var msg = new Win32Exception(rc).Message;
                _logger.Warning($"OfflineRegistry: RegUnLoadKey FAILED (return code {rc}: {msg}) for hive '{handle.HiveName}'. The hive may remain loaded in the host registry; ensure no open handles.");
            }
            else
            {
                _logger.Info($"OfflineRegistry: RegUnLoadKey OK (return code 0) — hive '{handle.HiveName}' unloaded.");
            }
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

    private const int SE_PRIVILEGE_ENABLED = 0x00000002;
    private const int TOKEN_ADJUST_PRIVILEGES = 0x00000020;
    private const int TOKEN_QUERY = 0x00000008;
    private const uint ERROR_NOT_ALL_ASSIGNED = 1300;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivilege
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegLoadKey(int hKey, string lpSubKey, string lpFile);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegUnLoadKey(int hKey, string lpSubKey);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, int desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? systemName, string privilegeName, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle, bool disableAllPrivileges, ref TokenPrivilege newState,
        int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Enables <c>SeRestorePrivilege</c> and <c>SeBackupPrivilege</c> on the
    /// current process token. These are required by <see cref="RegLoadKey"/> /
    /// <see cref="RegUnLoadKey"/> and are present in an elevated token but
    /// disabled by default — without this, hive load fails with
    /// ERROR_PRIVILEGE_NOT_HELD on a real elevated Windows session, which is
    /// exactly why offline service discovery could silently return zero. The
    /// outcome is logged so a privilege failure is observable rather than silent.
    /// </summary>
    private void EnableRequiredPrivileges()
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.Debug("OfflineRegistry: privilege enable skipped (not Windows).");
            return;
        }

        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
        {
            var openErr = Marshal.GetLastWin32Error();
            _logger.Warning($"OfflineRegistry: OpenProcessToken failed (Win32 {openErr}); RegLoadKey/RegUnLoadKey may fail with ERROR_PRIVILEGE_NOT_HELD.");
            return;
        }

        try
        {
            var failures = new List<string>();
            foreach (var privilege in new[] { "SeRestorePrivilege", "SeBackupPrivilege" })
            {
                if (!LookupPrivilegeValue(null, privilege, out var luid))
                {
                    failures.Add($"{privilege} (LookupPrivilegeValue Win32 {Marshal.GetLastWin32Error()})");
                    continue;
                }

                var state = new TokenPrivilege
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED
                };

                if (!AdjustTokenPrivileges(token, disableAllPrivileges: false, ref state, 0, IntPtr.Zero, IntPtr.Zero))
                {
                    failures.Add($"{privilege} (AdjustTokenPrivileges Win32 {Marshal.GetLastWin32Error()})");
                    continue;
                }

                // AdjustTokenPrivileges returns TRUE even when a privilege could not
                // actually be enabled; the real status is in GetLastError.
                if (Marshal.GetLastWin32Error() == ERROR_NOT_ALL_ASSIGNED)
                {
                    failures.Add($"{privilege} (not held/assigned on this token)");
                }
            }

            if (failures.Count == 0)
            {
                _logger.Info("OfflineRegistry: enabled SeRestorePrivilege and SeBackupPrivilege on the process token.");
            }
            else
            {
                _logger.Warning($"OfflineRegistry: some required privileges could not be enabled: {string.Join("; ", failures)}. Hive load/unload may fail.");
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// Produces a diagnostic-safe representation of an offline hive file path: the
    /// mount-root prefix (which can leak a user's local filesystem layout such as
    /// <c>C:\Users\&lt;user&gt;\...</c>) is replaced with <c>&lt;mount&gt;</c>, leaving only
    /// the in-image portion (e.g. <c>&lt;mount&gt;\Windows\System32\config\SYSTEM</c>).
    /// If no mount marker is present, only the final two segments are revealed.
    /// This never exposes host-registry data — only the OFFLINE image's hive path.
    /// </summary>
    private static string RedactHivePath(string hiveFilePath)
    {
        const string marker = "\\mount\\";
        var idx = hiveFilePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            return "<mount>\\" + hiveFilePath.Substring(idx + marker.Length);
        }

        var name = System.IO.Path.GetFileName(hiveFilePath);
        var parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(hiveFilePath) ?? string.Empty);
        return string.IsNullOrEmpty(parent) ? name : parent + "\\" + name;
    }
}
