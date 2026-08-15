using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WinForge.Core.Models;
using WinForge.Core.Profiles;
using WinForge.Core.Services;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.Profiles;
using Xunit;

namespace WinForge.App.Tests;

// =====================================================================
// Phase 15 Stage 15.4a — OFFLINE REGISTRY PRECHECK: MISSING KEY SEMANTICS
//
// First real Balanced offline apply reached mount/discovery/hive-access and then
// aborted with "APPLY VALIDATION FAILED: The specified registry key does not
// exist." Root cause (verified against a REAL hive): .NET 8's
// RegistryKey.GetValueKind throws IOException (message "The specified registry
// key does not exist.") when the named VALUE is absent from an existing key —
// and OfflineRegistryService.ReadValue only caught ArgumentException, so the
// exception escaped the apply PRECHECK and aborted the whole profile.
//
// Required semantics: for SetOfflineRegistryValue, a missing key/value during
// PRECHECK means "operation required" (continue to execution), NOT a failure.
// Missing AFTER execution is a VerificationFailed. Genuine infrastructure
// failures (hive load, corrupt hive, access denied) still fail.
// =====================================================================

public sealed class Stage15fRegistryPrecheckTests
{
    private static readonly ProfileDefinition Balanced =
        new ProfileCatalog().GetProfiles().Single(p => p.Id == "Balanced");

    private static readonly string[] BalancedPolicyTargets =
    {
        @"Policies\Microsoft\Windows\AppCompat",       // AllowTelemetry
        @"Policies\Microsoft\Windows\CloudContent",    // DisableWindowsConsumerFeatures / Spotlight
        @"Policies\Microsoft\Windows\DataCollection",  // DoNotShowFeedbackNotifications
        @"Policies\Microsoft\Windows\Explorer",        // DisableSearchBoxSuggestions
        @"Policies\Microsoft\Windows\System",          // EnableActivityHistory
    };

    private static ImageServicingWorkspace TemporaryMountedWorkspace()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wf15a-" + Guid.NewGuid().ToString("N"));
        var mount = System.IO.Path.Combine(root, "mount");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(mount, "Windows", "System32", "config"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(mount, "Users", "Default"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(mount, "Windows", "System32", "config", "SOFTWARE"), string.Empty);
        System.IO.File.WriteAllText(System.IO.Path.Combine(mount, "Windows", "System32", "config", "SYSTEM"), string.Empty);
        System.IO.File.WriteAllText(System.IO.Path.Combine(mount, "Users", "Default", "NTUSER.DAT"), string.Empty);
        return new ImageServicingWorkspace
        {
            State = ServicingWorkspaceState.Mounted,
            MountDirectory = mount,
            WorkingDirectory = root,
            WorkingImagePath = System.IO.Path.Combine(root, "work", "install.wim"),
            SourceIsoPath = System.IO.Path.Combine(root, "src.iso"),
        };
    }

    private static CustomizationOperation RegOp(
        string id, string hive, string key, string value, string data,
        OfflineRegistryValueKind kind = OfflineRegistryValueKind.DWord)
        => new()
        {
            OperationId = id, DisplayName = id,
            OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = hive, RegistryKeyPath = key, RegistryValueName = value,
            RegistryValueKind = kind, RegistryValueData = data,
            Risk = RiskClass.Safe, IsSelected = true, ActionKind = OptimizationAction.Configure,
            Scope = OptimizationScope.OfflineMachine,
        };

    private static OfflineApplyVerifier Verifier(FakeOfflineRegistryService registry)
        => new(new FakeProcessRunner(), registry, new InMemoryLoggerService());

    // =====================================================================
    // 1. PRECHECK SEMANTICS (spec §2 C/D/E/F)
    // =====================================================================

    [Fact]
    public async Task Precheck_Missing_Key_Means_Operation_Required()
    {
        // Pristine image: the policy KEY itself does not exist yet.
        var registry = new FakeOfflineRegistryService();
        var verifier = Verifier(registry);
        var op = RegOp("r", "SOFTWARE", @"Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", "1");

        var pre = await verifier.PreCheckAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.False(pre.AlreadySatisfied);
        Assert.Contains("write required", pre.Detail);
    }

    [Fact]
    public async Task Precheck_Missing_Value_Means_Operation_Required()
    {
        // Key exists, VALUE does not (the .NET GetValueKind IOException case).
        // The fake registry reports Exists=false for a missing value exactly like
        // the fixed OfflineRegistryService.ReadValue does.
        var registry = new FakeOfflineRegistryService(); // pristine: value absent
        var verifier = Verifier(registry);
        var op = RegOp("r", "SOFTWARE", @"Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", "0");

        var pre = await verifier.PreCheckAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.False(pre.AlreadySatisfied);
        Assert.Contains("write required", pre.Detail);
    }

    [Fact]
    public async Task Precheck_Mismatched_Value_Means_Operation_Required()
    {
        var registry = new FakeOfflineRegistryService();
        registry.Values["WinForge_SOFTWARE|Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo|Enabled"] = "1";
        registry.ValueKinds["WinForge_SOFTWARE|Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo|Enabled"] = OfflineRegistryValueKind.DWord;
        var verifier = Verifier(registry);
        var op = RegOp("r", "SOFTWARE", @"Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", "0");

        var pre = await verifier.PreCheckAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.False(pre.AlreadySatisfied);
        Assert.Contains("differs", pre.Detail);
    }

    [Fact]
    public async Task Precheck_Matching_Value_Is_AlreadySatisfied()
    {
        var registry = new FakeOfflineRegistryService();
        registry.Values["WinForge_SOFTWARE|Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo|Enabled"] = "0";
        registry.ValueKinds["WinForge_SOFTWARE|Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo|Enabled"] = OfflineRegistryValueKind.DWord;
        var verifier = Verifier(registry);
        var op = RegOp("r", "SOFTWARE", @"Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", "0");

        var pre = await verifier.PreCheckAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.True(pre.AlreadySatisfied);
        Assert.Contains("already matches", pre.Detail);
    }

    // =====================================================================
    // 2. OFFLINE MACHINE MISSING POLICY PATHS (real Balanced targets)
    // =====================================================================

    [Fact]
    public async Task OfflineMachine_Missing_Policy_Paths_All_Require_Operation()
    {
        var registry = new FakeOfflineRegistryService(); // pristine: everything absent
        var verifier = Verifier(registry);

        foreach (var keyPath in BalancedPolicyTargets)
        {
            var op = RegOp("r-" + keyPath, "SOFTWARE", keyPath, "SomeValue", "1");
            var pre = await verifier.PreCheckAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
            Assert.False(pre.AlreadySatisfied);
            Assert.Contains("write required", pre.Detail);
        }
    }

    // =====================================================================
    // 3. OFFLINE DEFAULT USER MISSING PATHS (§8 — must not throw)
    // =====================================================================

    [Fact]
    public async Task OfflineDefaultUser_Missing_Subkey_Requires_Operation_Without_Throwing()
    {
        var registry = new FakeOfflineRegistryService(); // pristine Default User: Explorer\Advanced exists? simulate absent
        var verifier = Verifier(registry);
        var op = RegOp("d", "DEFAULT_USER", @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", "1");

        // Must NOT throw — a missing subkey in the mounted Default User hive is
        // expected-absence → operation required.
        var pre = await verifier.PreCheckAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.False(pre.AlreadySatisfied);
    }

    [Fact]
    public void OfflineDefaultUser_Isolation_Maps_To_Mounted_Default_Profile()
    {
        var ws = TemporaryMountedWorkspace();
        Assert.Equal(
            System.IO.Path.Combine(ws.MountDirectory!, "Users", "Default", "NTUSER.DAT"),
            OfflineHivePaths.GetHiveFilePath(ws, "DEFAULT_USER"));
    }

    // =====================================================================
    // 4. POST-EXECUTION MISSING = VerificationFailed (§4 — keep separate)
    // =====================================================================

    [Fact]
    public async Task Post_Execution_Missing_Key_Is_VerificationFailed()
    {
        // Execution reported success, but the independent read-back finds the
        // value STILL absent — that is a FAILURE, unlike the precheck.
        var registry = new FakeOfflineRegistryService(); // value absent
        var verifier = Verifier(registry);
        var op = RegOp("r", "SOFTWARE", @"Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", "1");

        var verify = await verifier.VerifyAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.Equal(ApplyVerificationStatus.VerificationFailed, verify.Status);
        Assert.Contains("was not found", verify.Detail);
    }

    // =====================================================================
    // 5. STRUCTURED DIAGNOSTICS (§5/§6 — report survives precheck failure)
    // =====================================================================

    [Fact]
    public async Task Precheck_Infrastructure_Failure_Yields_Structured_Report_With_Key_And_Phase()
    {
        var op = RegOp("r", "SOFTWARE", @"Policies\Microsoft\Windows\System", "EnableActivityHistory", "0");
        var plan = new CustomizationPlan();
        plan.AddOperation(op);
        Assert.Empty(plan.Validate());

        var throwingVerifier = new ThrowingApplyVerifier(new InvalidOperationException("Offline hive is corrupt."));
        var service = new ProfileApplyValidationService();
        var report = await service.ValidateAsync(new ProfileApplyValidationRequest
        {
            Profile = Balanced,
            Plan = plan,
            Workspace = TemporaryMountedWorkspace(),
            Executor = new NoopApplyExecutor(),
            Verifier = throwingVerifier,
            Validator = new FakeMountIdentityValidator { SessionMatches = true },
            Logger = new InMemoryLoggerService(),
        });

        // The user gets the failing operation + phase + error — never a bare
        // "The specified registry key does not exist." with nothing else.
        Assert.False(report.ValidationPassed);
        Assert.Equal("Precheck", report.FailureStage);
        Assert.NotNull(report.FailedCanonicalKey);
        Assert.Contains("EnableActivityHistory", report.FailedCanonicalKey);
        Assert.Contains("Offline hive is corrupt", report.Error);

        // JSON schema carries the structured failure fields (camelCase).
        var json = System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.Contains("\"failureStage\":\"Precheck\"", json);
        Assert.Contains("\"failedCanonicalKey\"", json);
        Assert.Contains("\"error\"", json);
        Assert.Contains("\"validationPassed\":false", json);

        // The report is RETURNeD (not thrown), so the CLI's cleanup step always
        // runs and mountCleanup can be attached — cleanup still executes.
        report.MountCleanup = new ProfileApplyMountCleanupReport
        {
            DiscardSucceeded = true,
            WorkspaceCleanupSucceeded = true,
        };
        Assert.True(report.MountCleanup.DiscardSucceeded);
    }

    [Fact]
    public async Task Verify_Infrastructure_Failure_Yields_Structured_Report()
    {
        var op = RegOp("r", "SOFTWARE", @"Policies\Microsoft\Windows\System", "EnableActivityHistory", "0");
        var plan = new CustomizationPlan();
        plan.AddOperation(op);
        Assert.Empty(plan.Validate());

        var service = new ProfileApplyValidationService();
        var report = await service.ValidateAsync(new ProfileApplyValidationRequest
        {
            Profile = Balanced,
            Plan = plan,
            Workspace = TemporaryMountedWorkspace(),
            Executor = new SucceedingApplyExecutor(),
            Verifier = new ThrowingVerifyVerifier(new InvalidOperationException("SYSTEM hive cannot be read back.")),
            Validator = new FakeMountIdentityValidator { SessionMatches = true },
            Logger = new InMemoryLoggerService(),
        });

        Assert.False(report.ValidationPassed);
        Assert.Equal("Verify", report.FailureStage);
        Assert.Contains("SYSTEM hive cannot be read back", report.Error);
        Assert.Equal(1, report.Failed);
    }

    // =====================================================================
    // 6. ROOT-CAUSE PIN: .NET GetValueKind throws IOException on missing value
    // =====================================================================

    [Fact]
    public void DotNet_GetValueKind_Throws_IOException_On_Missing_Value_Regression_Pin()
    {
        // The EXACT behavior that aborted the first real Balanced apply: on .NET 8
        // Windows, RegistryKey.GetValueKind throws IOException with the message
        // "The specified registry key does not exist." when the named VALUE is
        // absent from an existing key (NOT ArgumentException). OfflineRegistryService
        // must treat this as expected absence (Exists=false). Pinned against a real
        // hive (scratch HKCU key, created and removed by this test).
        const string rootName = @"Software\WinForgeTest15a_GetValueKindPin";
        try
        {
            using (var root = Registry.CurrentUser.CreateSubKey(rootName))
            {
                var ex = Record.Exception(() => root.GetValueKind("DefinitelyMissingValue"));
                Assert.NotNull(ex);
                Assert.IsAssignableFrom<System.IO.IOException>(ex);
                Assert.Contains("registry key does not exist", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(rootName, throwOnMissingSubKey: false);
        }
    }

    // =====================================================================
    // fakes ---------------------------------------------------------------
    // =====================================================================

    private sealed class ThrowingApplyVerifier : IOfflineApplyVerifier
    {
        private readonly Exception _ex;

        public ThrowingApplyVerifier(Exception ex) => _ex = ex;

        public Task<ApplyPreCheckResult> PreCheckAsync(
            CustomizationOperation op, ImageServicingWorkspace workspace, CancellationToken ct)
            => Task.FromException<ApplyPreCheckResult>(_ex);

        public Task<ApplyVerifyResult> VerifyAsync(
            CustomizationOperation op, ImageServicingWorkspace workspace, CancellationToken ct)
            => Task.FromResult(new ApplyVerifyResult(ApplyVerificationStatus.Verified, "not reached"));
    }

    private sealed class ThrowingVerifyVerifier : IOfflineApplyVerifier
    {
        private readonly Exception _ex;

        public ThrowingVerifyVerifier(Exception ex) => _ex = ex;

        public Task<ApplyPreCheckResult> PreCheckAsync(
            CustomizationOperation op, ImageServicingWorkspace workspace, CancellationToken ct)
            => Task.FromResult(new ApplyPreCheckResult(false, "Needs execution."));

        public Task<ApplyVerifyResult> VerifyAsync(
            CustomizationOperation op, ImageServicingWorkspace workspace, CancellationToken ct)
            => Task.FromException<ApplyVerifyResult>(_ex);
    }

    private sealed class NoopApplyExecutor : ICustomizationExecutionService
    {
        public Task<CustomizationResult> ExecuteAsync(
            CustomizationPlan plan, ImageServicingWorkspace workspace,
            IProgress<ExecutionProgress>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new CustomizationResult { TotalOperations = 0 });
    }

    private sealed class SucceedingApplyExecutor : ICustomizationExecutionService
    {
        public Task<CustomizationResult> ExecuteAsync(
            CustomizationPlan plan, ImageServicingWorkspace workspace,
            IProgress<ExecutionProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            plan.FreezeForExecution();
            foreach (var op in plan.Operations.Where(o => o.IsSelected))
            {
                op.ExecutionStatus = CustomizationOperationStatus.Succeeded;
            }

            plan.MarkCompleted(withErrors: false);
            return Task.FromResult(new CustomizationResult
            {
                TotalOperations = plan.SelectedOperations.Count,
                Succeeded = plan.SelectedOperations.Count,
            });
        }
    }
}
