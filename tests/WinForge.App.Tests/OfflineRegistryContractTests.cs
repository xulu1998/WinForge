using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Offline registry write SUCCESS-CONTRACT tests (ADR-031). A registry operation
/// must NOT be reported as Succeeded merely because <c>SetValue</c> /
/// <c>DeleteValue</c> did not throw. After every write/delete the execution
/// engine performs an independent read-back and confirms (a) the value exists,
/// (b) its type matches, and (c) its data matches — otherwise the operation is
/// reported Failed. The tests also prove the key path is strictly relative to the
/// loaded hive root (no duplicated <c>SOFTWARE\</c> prefix) and that the host
/// registry can never be targeted.
///
/// <para>Driven entirely by fakes — no real ISO, mounted media, or admin dialog.</para>
/// </summary>
public class OfflineRegistryContractTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wf_regcontract_" + Guid.NewGuid().ToString("N"));
    private FakeProcessRunner _runner = null!;
    private FakeOfflineRegistryService _registry = null!;
    private FakeMountIdentityValidator _validator = null!;
    private WindowsCustomizationExecutionService _service = null!;
    private ImageServicingWorkspace _workspace = null!;

    private void Build()
    {
        var mount = Path.Combine(_root, "mount");
        Directory.CreateDirectory(Path.Combine(mount, "Windows", "System32", "config"));
        File.WriteAllBytes(Path.Combine(mount, "Windows", "System32", "config", "SOFTWARE"), new byte[8]);
        File.WriteAllBytes(Path.Combine(mount, "Windows", "System32", "config", "SYSTEM"), new byte[8]);

        _workspace = new ImageServicingWorkspace
        {
            WorkingDirectory = _root,
            MountDirectory = mount,
            WorkingImagePath = Path.Combine(_root, "image", "install.wim"),
            State = ServicingWorkspaceState.Mounted
        };

        _runner = new FakeProcessRunner
        {
            Responder = req => req.Arguments.Contains("/Get-MountedImageInfo")
                ? new ProcessResult { ExitCode = 0, StandardOutput = $"Mount Dir : {mount}\n" }
                : new ProcessResult { ExitCode = 0, StandardOutput = string.Empty }
        };

        _registry = new FakeOfflineRegistryService();
        _validator = new FakeMountIdentityValidator { SessionMatches = true, WithinMount = true };

        _service = new WindowsCustomizationExecutionService(
            _runner, _registry, new InMemoryLoggerService(), _validator);
    }

    public Task InitializeAsync()
    {
        Build();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private static CustomizationPlan PlanWith(params CustomizationOperation[] ops)
    {
        var plan = new CustomizationPlan();
        foreach (var op in ops)
        {
            op.IsSelected = true;
            plan.AddOperation(op);
        }

        plan.Validate();
        return plan;
    }

    // ---- Path mapping (root-relative, no duplicated hive base) ----

    [Fact]
    public async Task SoftwareHive_Write_IsRootRelative()
    {
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "r", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "SOFTWARE",
            RegistryKeyPath = @"Microsoft\Windows\CurrentVersion\AdvertisingInfo",
            RegistryValueName = "Enabled",
            RegistryValueKind = OfflineRegistryValueKind.DWord, RegistryValueData = "0",
            Risk = RiskClass.Safe
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);

        Assert.True(result.Success);
        var stored = _registry.Values.Keys.Single();
        Assert.Equal(@"WinForge_SOFTWARE|Microsoft\Windows\CurrentVersion\AdvertisingInfo|Enabled", stored);
        Assert.DoesNotContain("SOFTWARE\\SOFTWARE", stored);
    }

    [Fact]
    public async Task SoftwareHive_AccidentalSoftwarePrefix_IsStripped()
    {
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "r", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "SOFTWARE",
            // A stray "SOFTWARE\" prefix must NOT duplicate under the loaded hive.
            RegistryKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
            RegistryValueName = "Enabled",
            RegistryValueKind = OfflineRegistryValueKind.DWord, RegistryValueData = "0",
            Risk = RiskClass.Safe
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);

        Assert.True(result.Success);
        var stored = _registry.Values.Keys.Single();
        Assert.Equal(@"WinForge_SOFTWARE|Microsoft\Windows\CurrentVersion\AdvertisingInfo|Enabled", stored);
        Assert.DoesNotContain("WinForge_SOFTWARE\\SOFTWARE", stored);
    }

    [Fact]
    public async Task SystemHive_Write_IsRootRelative()
    {
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "r", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "SYSTEM",
            RegistryKeyPath = @"ControlSet001\Services\Spooler",
            RegistryValueName = "Start",
            RegistryValueKind = OfflineRegistryValueKind.DWord, RegistryValueData = "4",
            Risk = RiskClass.Safe
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);

        Assert.True(result.Success);
        var stored = _registry.Values.Keys.Single();
        Assert.Equal(@"WinForge_SYSTEM|ControlSet001\Services\Spooler|Start", stored);
        Assert.DoesNotContain("SYSTEM\\", stored);
    }

    [Fact]
    public void NormalizeKeyPath_StripsHiveBaseAndHklm()
    {
        Assert.Equal(@"Microsoft\X", OfflineHivePaths.NormalizeKeyPath("SOFTWARE", @"Microsoft\X"));
        Assert.Equal(@"Microsoft\X", OfflineHivePaths.NormalizeKeyPath("SOFTWARE", @"SOFTWARE\Microsoft\X"));
        Assert.Equal(@"Microsoft\X", OfflineHivePaths.NormalizeKeyPath("SOFTWARE", @"HKLM\SOFTWARE\Microsoft\X"));
        Assert.Equal(@"ControlSet001\Services\Foo",
            OfflineHivePaths.NormalizeKeyPath("SYSTEM", @"SYSTEM\ControlSet001\Services\Foo"));
    }

    // ---- Value type / data verification ----

    [Fact]
    public async Task DWordWrite_VerifiedByReadBack()
    {
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "r", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K", RegistryValueName = "V",
            RegistryValueKind = OfflineRegistryValueKind.DWord, RegistryValueData = "0",
            Risk = RiskClass.Safe
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("0", _registry.Values[@"WinForge_SOFTWARE|K|V"]);
        Assert.Equal(OfflineRegistryValueKind.DWord, _registry.ValueKinds[@"WinForge_SOFTWARE|K|V"]);
    }

    [Fact]
    public async Task StringWrite_VerifiedByReadBack()
    {
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "r", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K", RegistryValueName = "V",
            RegistryValueKind = OfflineRegistryValueKind.String, RegistryValueData = "hello",
            Risk = RiskClass.Safe
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("hello", _registry.Values[@"WinForge_SOFTWARE|K|V"]);
        Assert.Equal(OfflineRegistryValueKind.String, _registry.ValueKinds[@"WinForge_SOFTWARE|K|V"]);
    }

    [Fact]
    public async Task CreateMissingSubKey_Succeeds()
    {
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "r", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = @"A\B\C\D", RegistryValueName = "V",
            RegistryValueKind = OfflineRegistryValueKind.DWord, RegistryValueData = "1",
            Risk = RiskClass.Safe
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("1", _registry.Values[@"WinForge_SOFTWARE|A\B\C\D|V"]);
    }

    // ---- Success contract: failures are surfaced, never swallowed ----

    [Fact]
    public async Task WriteFailure_OperationFailed()
    {
        _registry.ThrowOnSetValue = true;
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "r", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K", RegistryValueName = "V",
            RegistryValueKind = OfflineRegistryValueKind.DWord, RegistryValueData = "0",
            Risk = RiskClass.Safe
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedOperations);
    }

    [Fact]
    public async Task WriteSucceedsButReadBackMissing_OperationFailed()
    {
        _registry.SimulatePersist = false; // SetValue records nothing
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "r", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K", RegistryValueName = "V",
            RegistryValueKind = OfflineRegistryValueKind.DWord, RegistryValueData = "0",
            Risk = RiskClass.Safe
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedOperations);
    }

    [Fact]
    public async Task WrongValueAfterWrite_OperationFailed()
    {
        _registry.ForcedData = "1"; // persisted with wrong content
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "r", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K", RegistryValueName = "V",
            RegistryValueKind = OfflineRegistryValueKind.DWord, RegistryValueData = "0",
            Risk = RiskClass.Safe
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedOperations);
    }

    [Fact]
    public async Task WrongTypeAfterWrite_OperationFailed()
    {
        _registry.ForcedKind = OfflineRegistryValueKind.String; // persisted with wrong type
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "r", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K", RegistryValueName = "V",
            RegistryValueKind = OfflineRegistryValueKind.DWord, RegistryValueData = "0",
            Risk = RiskClass.Safe
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedOperations);
    }

    [Fact]
    public async Task DeleteThenVerifyAbsent_Succeeds()
    {
        _registry.Values[@"WinForge_SOFTWARE|K|V"] = "1";
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "r", OperationType = CustomizationOperationType.DeleteOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K", RegistryValueName = "V",
            Risk = RiskClass.Safe
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(_registry.Values.ContainsKey(@"WinForge_SOFTWARE|K|V"));
    }

    [Fact]
    public async Task DeleteDoesNotTakeEffect_OperationFailed()
    {
        _registry.Values[@"WinForge_SOFTWARE|K|V"] = "1";
        _registry.SimulateDeleteRemoves = false; // delete is a no-op
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "r", OperationType = CustomizationOperationType.DeleteOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K", RegistryValueName = "V",
            Risk = RiskClass.Safe
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedOperations);
    }

    // ---- Host isolation ----

    [Fact]
    public async Task HostStyleHiveBase_IsRejected()
    {
        // "HKLM" is not a known offline hive base; it would target the host
        // registry, so the operation must be refused (never reach a write).
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "r", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "HKLM", RegistryKeyPath = @"SOFTWARE\Microsoft\X", RegistryValueName = "V",
            RegistryValueKind = OfflineRegistryValueKind.DWord, RegistryValueData = "0",
            Risk = RiskClass.Safe
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedOperations);
        Assert.Empty(_registry.LoadedHives);
    }

    [Fact]
    public async Task PathOutsideMount_IsRejected()
    {
        _validator.WithinMount = false;
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "r", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K", RegistryValueName = "V",
            RegistryValueKind = OfflineRegistryValueKind.DWord, RegistryValueData = "0",
            Risk = RiskClass.Safe
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, result.FailedOperations);
        Assert.Empty(_registry.LoadedHives);
    }

    // ---- Real definition audit ----

    [Fact]
    public void AdvertisingIdDefinition_MapsToCorrectOfflineLocation()
    {
        var provider = new CustomizationDefinitionProvider();
        var ad = provider.GetPrivacySettings().First(s => s.SettingId == "privacy.advertising-id");

        Assert.Equal("SOFTWARE", ad.Hive);
        Assert.Equal(@"Microsoft\Windows\CurrentVersion\AdvertisingInfo", ad.KeyPath);
        Assert.Equal("Enabled", ad.ValueName);
        Assert.Equal(OfflineRegistryValueKind.DWord, ad.ValueKind);
        Assert.Equal("0", ad.RecommendedData);
    }

    // ---- Cross-cutting contract ----

    [Fact]
    public async Task Operation_NeverReportedSuccess_WhenReadBackWouldFail()
    {
        _registry.SimulatePersist = false;
        var op = new CustomizationOperation
        {
            OperationId = "r", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K", RegistryValueName = "V",
            RegistryValueKind = OfflineRegistryValueKind.DWord, RegistryValueData = "0",
            Risk = RiskClass.Safe
        };
        var plan = PlanWith(op);
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);

        // Neither the high-level result nor the per-operation status may claim success.
        Assert.False(result.Success);
        var executed = result.Operations.Single(o => o.OperationId == op.OperationId);
        Assert.Equal(CustomizationOperationStatus.FailedRecoverable, executed.ExecutionStatus);
    }
}
