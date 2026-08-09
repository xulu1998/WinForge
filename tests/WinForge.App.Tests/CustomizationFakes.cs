using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.App.Tests;

/// <summary>
/// In-memory fakes for the Step 3.3 customization contracts, used by discovery,
/// execution, and view-model tests so no real ISO, mounted media, or admin
/// dialog is ever required (CI-safe).
/// </summary>
internal sealed class FakeOfflineRegistryService : IOfflineRegistryService
{
    public List<string> LoadedHives { get; } = new();
    public List<string> UnloadedHives { get; } = new();
    public Dictionary<string, string> Values { get; } = new();
    public Dictionary<string, List<string>> SubKeys { get; } = new();
    public int SetValueCalls { get; private set; }
    public int DeleteValueCalls { get; private set; }
    public bool ThrowOnLoad { get; set; }

    private static string Key(string hive, string path, string name) => $"{hive}|{path}|{name}";

    public OfflineHiveHandle LoadHive(string hiveFilePath, string hiveName)
    {
        if (ThrowOnLoad)
        {
            throw new System.InvalidOperationException("Simulated hive load failure.");
        }

        LoadedHives.Add(hiveName);
        return new OfflineHiveHandle(hiveFilePath, hiveName);
    }

    public void UnloadHive(OfflineHiveHandle handle) => UnloadedHives.Add(handle.HiveName);

    public void SetValue(OfflineHiveHandle handle, string keyPath, string valueName, OfflineRegistryValueKind kind, string data)
    {
        SetValueCalls++;
        Values[Key(handle.HiveName, keyPath, valueName)] = data;
    }

    public void DeleteValue(OfflineHiveHandle handle, string keyPath, string valueName)
    {
        DeleteValueCalls++;
        Values.Remove(Key(handle.HiveName, keyPath, valueName));
    }

    public string? GetValue(OfflineHiveHandle handle, string keyPath, string valueName)
        => Values.TryGetValue(Key(handle.HiveName, keyPath, valueName), out var v) ? v : null;

    public IReadOnlyList<string> EnumSubKeys(OfflineHiveHandle handle, string keyPath)
        => SubKeys.TryGetValue($"{handle.HiveName}|{keyPath}", out var s) ? s : System.Array.Empty<string>();
}

internal sealed class FakeCustomizationDefinitionProvider : ICustomizationDefinitionProvider
{
    public List<DiscoveredRegistrySetting> Privacy { get; set; } = new();
    public List<DiscoveredRegistrySetting> System { get; set; } = new();
    public List<DiscoveredOfflineService> Services { get; set; } = new();

    public IReadOnlyList<DiscoveredRegistrySetting> GetPrivacySettings() => Privacy;
    public IReadOnlyList<DiscoveredRegistrySetting> GetSystemSettings() => System;
    public IReadOnlyList<DiscoveredOfflineService> GetRecommendedServiceChanges() => Services;
}

internal sealed class FakeMountIdentityValidator : IMountIdentityValidator
{
    public bool WithinMount { get; set; } = true;
    public bool SessionMatches { get; set; } = true;

    public bool IsWithinMount(string path, ImageServicingWorkspace workspace) => WithinMount;
    public bool MatchesSession(ImageServicingWorkspace workspace) => SessionMatches;
}

internal sealed class FakeCustomizationDiscoveryService : ICustomizationDiscoveryService
{
    public DiscoveryInventory Inventory { get; set; } = new DiscoveryInventory { Discovered = true };
    public int DiscoverCalls { get; private set; }

    public Task<DiscoveryInventory> DiscoverAsync(ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
    {
        DiscoverCalls++;
        return Task.FromResult(Inventory);
    }
}

internal sealed class FakeCustomizationExecutionService : ICustomizationExecutionService
{
    public CustomizationResult Result { get; set; } = new CustomizationResult();
    public CustomizationPlan? LastPlan { get; private set; }
    public ImageServicingWorkspace? LastWorkspace { get; private set; }
    public int ExecuteCalls { get; private set; }

    public Task<CustomizationResult> ExecuteAsync(
        CustomizationPlan plan, ImageServicingWorkspace workspace,
        IProgress<ExecutionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ExecuteCalls++;
        LastPlan = plan;
        LastWorkspace = workspace;
        return Task.FromResult(Result);
    }
}
