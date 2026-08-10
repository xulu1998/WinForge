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
    public Dictionary<string, OfflineRegistryValueKind> ValueKinds { get; } = new();
    public Dictionary<string, List<string>> SubKeys { get; } = new();
    public int SetValueCalls { get; private set; }
    public int DeleteValueCalls { get; private set; }
    public bool ThrowOnLoad { get; set; }

    /// <summary>When true (default), SetValue actually records the value so a
    /// subsequent read-back finds it. When false, SetValue silently records
    /// nothing — used to simulate a write that does not persist.</summary>
    public bool SimulatePersist { get; set; } = true;

    /// <summary>When set, SetValue stores this data instead of the requested
    /// data — used to simulate a value that persisted with the wrong content.</summary>
    public string? ForcedData { get; set; }

    /// <summary>When set, SetValue stores this kind instead of the requested
    /// kind — used to simulate a value that persisted with the wrong type.</summary>
    public OfflineRegistryValueKind? ForcedKind { get; set; }

    /// <summary>When true (default), DeleteValue actually removes the value. When
    /// false, DeleteValue is a no-op so the value remains present — used to
    /// simulate a delete that did not take effect.</summary>
    public bool SimulateDeleteRemoves { get; set; } = true;

    /// <summary>When true, SetValue throws instead of recording anything.</summary>
    public bool ThrowOnSetValue { get; set; }

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
        if (ThrowOnSetValue)
        {
            throw new System.InvalidOperationException("Simulated SetValue failure.");
        }

        if (!SimulatePersist)
        {
            return;
        }

        var k = Key(handle.HiveName, keyPath, valueName);
        Values[k] = ForcedData ?? data;
        ValueKinds[k] = ForcedKind ?? kind;
    }

    public void DeleteValue(OfflineHiveHandle handle, string keyPath, string valueName)
    {
        DeleteValueCalls++;
        if (!SimulateDeleteRemoves)
        {
            return;
        }

        var k = Key(handle.HiveName, keyPath, valueName);
        Values.Remove(k);
        ValueKinds.Remove(k);
    }

    public string? GetValue(OfflineHiveHandle handle, string keyPath, string valueName)
        => Values.TryGetValue(Key(handle.HiveName, keyPath, valueName), out var v) ? v : null;

    public OfflineRegistryReadResult ReadValue(OfflineHiveHandle handle, string keyPath, string valueName)
    {
        var k = Key(handle.HiveName, keyPath, valueName);
        if (Values.TryGetValue(k, out var data))
        {
            return new OfflineRegistryReadResult
            {
                Exists = true,
                Kind = ValueKinds.TryGetValue(k, out var kind) ? kind : OfflineRegistryValueKind.String,
                Data = data
            };
        }

        return new OfflineRegistryReadResult { Exists = false };
    }

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

        // Mirror WindowsCustomizationExecutionService's plan lifecycle so the
        // workflow gating (which keys off the plan's post-execution status) is
        // exercised exactly as on the real desktop:
        //  - a plan that is not Validated (e.g. a guard failure) is NOT mutated;
        //  - a critical failure returns without freezing/marking the plan;
        //  - otherwise the live plan is frozen then marked Completed/Failed,
        //    which is what makes the nested (in-place) notification fire.
        if (plan.Status != CustomizationPlanStatus.Validated)
        {
            return Task.FromResult(Result);
        }

        if (Result.CriticalFailure)
        {
            return Task.FromResult(Result);
        }

        plan.FreezeForExecution();
        if (!Result.Success && Result.FailedOperations == 0)
        {
            plan.MarkFailed();
        }
        else
        {
            plan.MarkCompleted(Result.FailedOperations > 0);
        }

        return Task.FromResult(Result);
    }
}
