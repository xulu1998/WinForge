using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// <see cref="WindowsCustomizationExecutionService"/> behaviour driven by fakes
/// for DISM, the offline registry, and mount identity. Covers the pre-execution
/// safety guard (critical stop), per-operation success / recoverable failure,
/// package allowlist gating, service present/absent handling, and cooperative
/// cancellation. No real ISO or mounted media is required.
/// </summary>
public class WindowsCustomizationExecutionServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wf_exec_" + System.Guid.NewGuid().ToString("N"));
    private FakeProcessRunner _runner = null!;
    private FakeOfflineRegistryService _registry = null!;
    private FakeMountIdentityValidator _validator = null!;
    private WindowsCustomizationExecutionService _service = null!;
    private ImageServicingWorkspace _workspace = null!;

    public int AppxExitCode { get; set; } = 0;
    public int PackageExitCode { get; set; } = 0;
    public bool MountRegistered { get; set; } = true;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private void Build(bool sessionMatches = true)
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
            Responder = req =>
            {
                if (req.Arguments.Contains("/Get-MountedImageInfo"))
                {
                    return MountRegistered
                        ? new ProcessResult { ExitCode = 0, StandardOutput = $"Mount Dir : {mount}\n" }
                        : new ProcessResult { ExitCode = 0, StandardOutput = "Mount Dir : X:\\other\n" };
                }

                if (req.Arguments.Contains("/Remove-ProvisionedAppxPackage"))
                {
                    return new ProcessResult { ExitCode = AppxExitCode, StandardOutput = string.Empty };
                }

                if (req.Arguments.Contains("/Remove-Package"))
                {
                    return new ProcessResult { ExitCode = PackageExitCode, StandardOutput = string.Empty };
                }

                return new ProcessResult { ExitCode = 0, StandardOutput = string.Empty };
            }
        };

        _registry = new FakeOfflineRegistryService();
        _registry.Values["WinForge_SYSTEM|Select|Current"] = "1";
        _registry.Values["WinForge_SYSTEM|ControlSet001\\Services\\DiagTrack|Start"] = "2";

        _validator = new FakeMountIdentityValidator { SessionMatches = sessionMatches, WithinMount = true };

        _service = new WindowsCustomizationExecutionService(
            _runner, _registry, new InMemoryLoggerService(), _validator);
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

    // ---- Pre-execution guard ----

    [Fact]
    public async Task Execute_Refuses_WhenSessionInvalid()
    {
        Build(sessionMatches: false);
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "a", OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            TargetIdentifier = "x", Risk = RiskClass.Removable
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.True(result.CriticalFailure);
        Assert.Empty(_registry.LoadedHives); // no hive touched
    }

    [Fact]
    public async Task Execute_Refuses_WhenNotMounted()
    {
        Build();
        _workspace.State = ServicingWorkspaceState.Prepared;
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "a", OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            TargetIdentifier = "x", Risk = RiskClass.Removable
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);
        Assert.True(result.CriticalFailure);
    }

    [Fact]
    public async Task Execute_Refuses_WhenMountNotRegistered()
    {
        Build();
        MountRegistered = false;
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "a", OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            TargetIdentifier = "x", Risk = RiskClass.Removable
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);
        Assert.True(result.CriticalFailure);
    }

    // ---- Registry operations ----

    [Fact]
    public async Task Execute_SetRegistryValue_Succeeds()
    {
        Build();
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "r", OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K", RegistryValueName = "V",
            RegistryValueKind = OfflineRegistryValueKind.DWord, RegistryValueData = "0",
            Risk = RiskClass.Safe
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(1, _registry.SetValueCalls);
        Assert.Single(_registry.LoadedHives);
        Assert.Contains("WinForge_SOFTWARE", _registry.LoadedHives);
        Assert.Contains("WinForge_SOFTWARE", _registry.UnloadedHives);
    }

    [Fact]
    public async Task Execute_DeleteRegistryValue_Succeeds()
    {
        Build();
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "r", OperationType = CustomizationOperationType.DeleteOfflineRegistryValue,
            RegistryHive = "SOFTWARE", RegistryKeyPath = "K", RegistryValueName = "V",
            Risk = RiskClass.Safe
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(1, _registry.DeleteValueCalls);
    }

    // ---- Service operations ----

    [Fact]
    public async Task Execute_ConfigureService_Present_Succeeds()
    {
        Build();
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "s", OperationType = CustomizationOperationType.ConfigureOfflineService,
            ServiceName = "DiagTrack", ServiceStartType = ServiceStartType.Disabled,
            Risk = RiskClass.Removable
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("4", _registry.Values["WinForge_SYSTEM|ControlSet001\\Services\\DiagTrack|Start"]);
    }

    [Fact]
    public async Task Execute_ConfigureService_Absent_IsSkipped()
    {
        Build();
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "s", OperationType = CustomizationOperationType.ConfigureOfflineService,
            ServiceName = "NoSuchService", ServiceStartType = ServiceStartType.Disabled,
            Risk = RiskClass.Removable
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);
        // Skipped is not a failure.
        Assert.True(result.Success);
        Assert.Equal(0, result.FailedOperations);
    }

    // ---- Appx / package removal ----

    [Fact]
    public async Task Execute_RemoveAppx_Success()
    {
        Build();
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "a", OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            TargetIdentifier = "Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe",
            Risk = RiskClass.Removable
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Execute_RemoveAppx_DismFailure_IsRecoverable()
    {
        Build();
        AppxExitCode = 11;
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "a", OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            TargetIdentifier = "x", Risk = RiskClass.Removable
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(1, result.FailedOperations);
        Assert.False(result.CriticalFailure);
        Assert.Equal(CustomizationPlanStatus.CompletedWithErrors, plan.Status);
    }

    [Fact]
    public async Task Execute_RemovePackage_NotAllowlisted_IsSkipped()
    {
        Build();
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "p", OperationType = CustomizationOperationType.RemovePackage,
            TargetIdentifier = "Microsoft-Windows-Client-ProfessionalEdition-Package~x",
            Risk = RiskClass.Removable
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);
        // Skipped (not on allowlist) is not a failure.
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Execute_RemovePackage_Allowlisted_AttemptsDism()
    {
        Build();
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "p", OperationType = CustomizationOperationType.RemovePackage,
            TargetIdentifier = "Microsoft-Windows-InternetExplorer-Optional-Package~x",
            Risk = RiskClass.Removable
        });
        var result = await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Contains(_runner.Requests, r => r.Arguments.Contains("/Remove-Package"));
    }

    // ---- Cancellation ----

    [Fact]
    public async Task Execute_Cancellation_BeforeStart_MarksCancelled()
    {
        Build();
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "a", OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            TargetIdentifier = "x", Risk = RiskClass.Removable
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await _service.ExecuteAsync(plan, _workspace, null, cts.Token);
        Assert.Equal(CustomizationPlanStatus.Cancelled, plan.Status);
    }

    [Fact]
    public async Task Execute_LeavesImageMounted_AfterApply()
    {
        Build();
        var plan = PlanWith(new CustomizationOperation
        {
            OperationId = "a", OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            TargetIdentifier = "x", Risk = RiskClass.Removable
        });
        await _service.ExecuteAsync(plan, _workspace, null, CancellationToken.None);
        // No unmount command is issued by the execution engine.
        Assert.DoesNotContain(_runner.Requests, r => r.Arguments.Contains("/Unmount-Image"));
        Assert.Equal(ServicingWorkspaceState.Mounted, _workspace.State);
    }
}
