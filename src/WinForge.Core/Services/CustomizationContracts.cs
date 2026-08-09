using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;

namespace WinForge.Core.Services;

/// <summary>
/// Result of executing a customization plan against the mounted working image.
/// Carries the overall outcome plus per-operation results so the UI can show a
/// summary and any failures.
/// </summary>
public sealed class CustomizationResult
{
    public bool Success => FailedOperations == 0;
    public int TotalOperations { get; init; }
    public int Succeeded { get; init; }
    public int FailedOperations { get; init; }
    public bool CriticalFailure { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<CustomizationOperation> Operations { get; init; } = new List<CustomizationOperation>();
}

/// <summary>
/// Progress emitted during plan execution so the UI stays responsive and can
/// show per-operation status (e.g. "Applying 3 of 12").
/// </summary>
public sealed class ExecutionProgress
{
    public int Completed { get; init; }
    public int Total { get; init; }
    public string? CurrentOperation { get; init; }
    public string? Detail { get; init; }
}

/// <summary>
/// Inspects the mounted offline working image and returns structured candidate
/// customization items (apps, packages, services, registry settings). Must not
/// return raw DISM text to the UI, must tolerate missing/renamed/edition/build
/// differences, and must operate ONLY against the mounted workspace.
/// </summary>
public interface ICustomizationDiscoveryService
{
    Task<DiscoveryInventory> DiscoverAsync(
        ImageServicingWorkspace workspace, CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes a validated <see cref="CustomizationPlan"/> against the mounted
/// isolated working image. Verifies the workspace is Mounted and the mount is
/// still registered, freezes the plan, runs operations in defined order, records
/// per-operation results, applies the failure policy, and leaves the image
/// mounted afterward (no auto-commit/unmount).
/// </summary>
public interface ICustomizationExecutionService
{
    Task<CustomizationResult> ExecuteAsync(
        CustomizationPlan plan,
        ImageServicingWorkspace workspace,
        IProgress<ExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A platform-agnostic offline registry editing abstraction. Implementations
/// load an offline hive from the mounted image, set/delete typed values, and
/// guarantee the hive is unloaded (including on exception). Core never shells out
/// to <c>reg.exe</c>; the Windows implementation uses a safer API/registry
/// approach and a WinForge-owned temporary hive name.
/// </summary>
public interface IOfflineRegistryService
{
    /// <summary>Loads an offline hive file under a WinForge-owned name.</summary>
    OfflineHiveHandle LoadHive(string hiveFilePath, string hiveName);

    /// <summary>Unloads a previously loaded hive; safe to call multiple times.</summary>
    void UnloadHive(OfflineHiveHandle handle);

    void SetValue(OfflineHiveHandle handle, string keyPath, string valueName, OfflineRegistryValueKind kind, string data);
    void DeleteValue(OfflineHiveHandle handle, string keyPath, string valueName);
    string? GetValue(OfflineHiveHandle handle, string keyPath, string valueName);

    /// <summary>
    /// Reads back a value from the loaded offline hive for independent
    /// verification. Returns <see cref="OfflineRegistryReadResult.Exists"/> = false
    /// when the key or value is absent, so a caller can confirm a write actually
    /// landed (existence + kind + data) before reporting success.
    /// </summary>
    OfflineRegistryReadResult ReadValue(OfflineHiveHandle handle, string keyPath, string valueName);

    /// <summary>Enumerates the immediate sub-key names of <paramref name="keyPath"/>.</summary>
    IReadOnlyList<string> EnumSubKeys(OfflineHiveHandle handle, string keyPath);
}

/// <summary>
/// Independent read-back of a single offline registry value. Used to verify a
/// write actually persisted (existence, registry type, and data) rather than
/// trusting that <c>SetValue</c> did not throw.
/// </summary>
public sealed class OfflineRegistryReadResult
{
    /// <summary>True when the key and value both exist in the offline hive.</summary>
    public bool Exists { get; init; }

    /// <summary>The registry value kind actually stored (only meaningful when <see cref="Exists"/>).</summary>
    public OfflineRegistryValueKind Kind { get; init; }

    /// <summary>The value data rendered as a string (only meaningful when <see cref="Exists"/>).</summary>
    public string? Data { get; init; }
}

/// <summary>
/// A loaded offline hive handle. The implementation owns the lifecycle; callers
/// must pass it to <see cref="IOfflineRegistryService.UnloadHive"/> (typically in
/// a finally block). Never references a host OS hive.
/// </summary>
public sealed class OfflineHiveHandle
{
    public string HiveFile { get; }
    public string HiveName { get; }
    public bool IsLoaded { get; set; } = true;

    public OfflineHiveHandle(string hiveFile, string hiveName)
    {
        HiveFile = hiveFile;
        HiveName = hiveName;
    }
}

/// <summary>
/// Supplies the curated, trusted set of offline registry / service definitions
/// for the Privacy and System pages. Definitions are generated only by WinForge
/// — never from arbitrary UI input — so every generated operation has a known,
/// documented, offline-safe target.
/// </summary>
public interface ICustomizationDefinitionProvider
{
    IReadOnlyList<DiscoveredRegistrySetting> GetPrivacySettings();
    IReadOnlyList<DiscoveredRegistrySetting> GetSystemSettings();
    IReadOnlyList<DiscoveredOfflineService> GetRecommendedServiceChanges();
}

/// <summary>
/// Ensures every modification targets ONLY the active mounted WinForge working
/// image and never the host OS, the original ISO mount root, or any path outside
/// the workspace. Used by the execution engine before each destructive group.
/// </summary>
public interface IMountIdentityValidator
{
    /// <summary>True when <paramref name="path"/> is strictly inside the mounted workspace.</summary>
    bool IsWithinMount(string path, ImageServicingWorkspace workspace);

    /// <summary>
    /// True when the session/workspace identity still matches reality (the mount
    /// directory belongs to this workspace and the working image path is under
    /// the workspace directory).
    /// </summary>
    bool MatchesSession(ImageServicingWorkspace workspace);
}
