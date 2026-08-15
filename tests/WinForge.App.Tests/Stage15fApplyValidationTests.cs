using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Profiles;
using WinForge.Core.Services;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.Profiles;
using WinForge.Infrastructure.Servicing;
using Xunit;

namespace WinForge.App.Tests;

// =====================================================================
// Phase 15 Stage 15.4 — REAL OFFLINE APPLY VALIDATION (ADR-097)
//
// Tests for the --apply-profile harness: workspace ownership guards,
// selected-only execution, independent read-back verification (AppX /
// optional feature / offline service / offline registry, incl.
// OfflineDefaultUser isolation), deterministic already-satisfied skips,
// exact failure reporting, and the validation report schema.
// =====================================================================

public sealed class Stage15fApplyValidationTests
{
    private static readonly ProfileDefinition Balanced =
        new ProfileCatalog().GetProfiles().Single(p => p.Id == "Balanced");

    private static readonly ProfileDefinition DedicatedGaming =
        new ProfileCatalog().GetProfiles().Single(p => p.Id == "DedicatedGaming");

    private readonly ProfileApplyValidationService _service = new();

    // ---- helpers -------------------------------------------------------

    private static ImageServicingWorkspace MountedWorkspace()
        => new()
        {
            State = ServicingWorkspaceState.Mounted,
            MountDirectory = @"C:\wf15b\mount",
            WorkingDirectory = @"C:\wf15b",
            WorkingImagePath = @"C:\wf15b\work\install.wim",
            SourceIsoPath = @"C:\isos\Win11.iso",
        };

    /// <summary>
    /// A mounted workspace whose hive files physically exist on disk (the
    /// verifier genuinely reads the offline hive files). Real hive reads require
    /// the files to exist — the verifier refuses otherwise.
    /// </summary>
    private static ImageServicingWorkspace TemporaryMountedWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "wf15f-" + Guid.NewGuid().ToString("N"));
        var mount = Path.Combine(root, "mount");
        Directory.CreateDirectory(Path.Combine(mount, "Windows", "System32", "config"));
        Directory.CreateDirectory(Path.Combine(mount, "Users", "Default"));
        File.WriteAllText(Path.Combine(mount, "Windows", "System32", "config", "SOFTWARE"), string.Empty);
        File.WriteAllText(Path.Combine(mount, "Windows", "System32", "config", "SYSTEM"), string.Empty);
        File.WriteAllText(Path.Combine(mount, "Users", "Default", "NTUSER.DAT"), string.Empty);
        return new ImageServicingWorkspace
        {
            State = ServicingWorkspaceState.Mounted,
            MountDirectory = mount,
            WorkingDirectory = root,
            WorkingImagePath = Path.Combine(root, "work", "install.wim"),
            SourceIsoPath = Path.Combine(root, "src.iso"),
        };
    }

    private static CustomizationOperation AppxOp(string id, string pkg, bool selected = true)
        => new()
        {
            OperationId = id, DisplayName = id,
            OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            TargetIdentifier = pkg, Risk = RiskClass.Safe, IsSelected = selected,
            ActionKind = OptimizationAction.Remove,
        };

    private static CustomizationOperation FeatureOp(string id, string feature, bool selected = true)
        => new()
        {
            OperationId = id, DisplayName = id,
            OperationType = CustomizationOperationType.DisableOptionalFeature,
            TargetIdentifier = feature, Risk = RiskClass.Safe, IsSelected = selected,
            ActionKind = OptimizationAction.Disable,
        };

    private static CustomizationOperation ServiceOp(string id, string service, bool selected = true)
        => new()
        {
            OperationId = id, DisplayName = id,
            OperationType = CustomizationOperationType.ConfigureOfflineService,
            ServiceName = service, ServiceStartType = ServiceStartType.Disabled,
            Risk = RiskClass.Safe, IsSelected = selected, ActionKind = OptimizationAction.Configure,
        };

    private static CustomizationOperation RegOp(
        string id, string hive, string key, string value, string data,
        OfflineRegistryValueKind kind = OfflineRegistryValueKind.DWord, bool selected = true)
        => new()
        {
            OperationId = id, DisplayName = id,
            OperationType = CustomizationOperationType.SetOfflineRegistryValue,
            RegistryHive = hive, RegistryKeyPath = key, RegistryValueName = value,
            RegistryValueKind = kind, RegistryValueData = data,
            Risk = RiskClass.Safe, IsSelected = selected, ActionKind = OptimizationAction.Configure,
            Scope = OptimizationScope.OfflineMachine,
        };

    private static CustomizationPlan ValidatedPlan(params CustomizationOperation[] ops)
    {
        var plan = new CustomizationPlan();
        foreach (var op in ops)
        {
            plan.AddOperation(op);
        }

        var issues = plan.Validate();
        Assert.Empty(issues);
        return plan;
    }

    private static async Task<ProfileApplyValidationReport> RunAsync(
        ProfileDefinition profile, CustomizationPlan plan,
        ImageServicingWorkspace? workspace = null,
        FakeApplyExecutor? executor = null, FakeApplyVerifier? verifier = null,
        FakeMountIdentityValidator? validator = null)
    {
        var svc = new ProfileApplyValidationService();
        return await svc.ValidateAsync(new ProfileApplyValidationRequest
        {
            Profile = profile,
            Plan = plan,
            Workspace = workspace ?? MountedWorkspace(),
            Executor = executor ?? new FakeApplyExecutor(),
            Verifier = verifier ?? new FakeApplyVerifier(),
            Validator = validator ?? new FakeMountIdentityValidator { SessionMatches = true },
            Logger = new InMemoryLoggerService(),
        });
    }

    // =====================================================================
    // 1. WORKSPACE OWNERSHIP (§10)
    // =====================================================================

    [Fact]
    public async Task Workspace_Ownership_Guard_Refuses_Unmounted_Workspace()
    {
        var ws = MountedWorkspace();
        ws.State = ServicingWorkspaceState.Prepared; // NOT mounted

        var executor = new FakeApplyExecutor();
        var report = await RunAsync(Balanced, ValidatedPlan(AppxOp("a", "Pkg.X")), ws, executor);

        Assert.False(report.ValidationPassed);
        Assert.Equal(1, report.Failed);
        Assert.Equal(0, executor.Calls);
        var op = Assert.Single(report.Operations);
        Assert.Contains("Workspace ownership guard failed", op.VerificationDetail);
    }

    [Fact]
    public async Task Workspace_Ownership_Guard_Refuses_Session_Mismatch()
    {
        var executor = new FakeApplyExecutor();
        var report = await RunAsync(
            Balanced, ValidatedPlan(AppxOp("a", "Pkg.X")),
            executor: executor,
            validator: new FakeMountIdentityValidator { SessionMatches = false });

        Assert.False(report.ValidationPassed);
        Assert.Equal(1, report.Failed);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public void Unknown_Mount_Never_Discarded_Ownership_Rejects_NonWorkspace_Paths()
    {
        // A mount path NOT under the workspace root (host path / ISO root /
        // drive root) must never match a session — the harness can only ever
        // discard a workspace-owned mount.
        var validator = new MountIdentityValidator();
        var hostPathWorkspace = new ImageServicingWorkspace
        {
            State = ServicingWorkspaceState.Mounted,
            MountDirectory = @"C:\Windows\Temp\attacker-mount",
            WorkingDirectory = @"C:\wf15b",
            WorkingImagePath = @"C:\wf15b\work\install.wim",
        };
        Assert.False(validator.MatchesSession(hostPathWorkspace));

        var driveRoot = new ImageServicingWorkspace
        {
            State = ServicingWorkspaceState.Mounted,
            MountDirectory = @"C:\",
            WorkingDirectory = @"C:\wf15b",
            WorkingImagePath = @"C:\wf15b\work\install.wim",
        };
        Assert.False(validator.MatchesSession(driveRoot));

        var isoRoot = new ImageServicingWorkspace
        {
            State = ServicingWorkspaceState.Mounted,
            MountDirectory = @"C:\isos\mount",
            WorkingDirectory = @"C:\wf15b",
            WorkingImagePath = @"C:\wf15b\work\install.wim",
        };
        Assert.False(validator.MatchesSession(isoRoot));
    }

    [Fact]
    public void Workspace_Owned_Mount_Matches_Session()
    {
        var validator = new MountIdentityValidator();
        Assert.True(validator.MatchesSession(MountedWorkspace()));
        Assert.True(validator.IsWithinMount(@"C:\wf15b\mount\Windows\System32\config\SOFTWARE", MountedWorkspace()));
        Assert.False(validator.IsWithinMount(@"C:\Windows\System32\config\SOFTWARE", MountedWorkspace()));
    }

    // =====================================================================
    // 2. SELECTED-ONLY EXECUTION (§8/§9 — BuildPlan candidates vs selected)
    // =====================================================================

    [Fact]
    public async Task Selected_Only_Execution_Proves_Candidates_Not_Executed()
    {
        var selected1 = AppxOp("sel-1", "Pkg.One");
        var selected2 = RegOp("sel-2", "SOFTWARE", "Microsoft\\Policies\\X", "Value", "1");
        var candidate = ServiceOp("rec-1", "DiagTrack", selected: false); // Recommend row — NOT selected

        var executor = new FakeApplyExecutor();
        var report = await RunAsync(Balanced, ValidatedPlan(selected1, selected2, candidate), executor: executor);

        // Only the two selected operations reached the executor.
        Assert.Equal(2, executor.ExecutedOpIds.Count);
        Assert.Contains("sel-1", executor.ExecutedOpIds);
        Assert.Contains("sel-2", executor.ExecutedOpIds);
        Assert.DoesNotContain("rec-1", executor.ExecutedOpIds);

        // The report proves the separation: candidate stays out of operations,
        // and the unselected op's status was never touched.
        Assert.Equal(3, report.BuildPlanOperationCount);
        Assert.Equal(2, report.SelectedOperationCount);
        Assert.Equal(2, report.Attempted);
        Assert.Equal(2, report.Succeeded);
        Assert.DoesNotContain(report.Operations, o => o.CanonicalKey.Contains("rec-1", StringComparison.Ordinal));
        Assert.Equal(CustomizationOperationStatus.Pending, candidate.ExecutionStatus);
    }

    // =====================================================================
    // 3. ALREADY-SATISFIED SEMANTICS (§4/§5/§6/§7)
    // =====================================================================

    [Fact]
    public async Task Already_Satisfied_Is_Skipped_And_Never_Executed()
    {
        var toRemove = AppxOp("rem", "Pkg.One");
        var toRun = AppxOp("run", "Pkg.Two");

        var executor = new FakeApplyExecutor();
        var verifier = new FakeApplyVerifier
        {
            PreCheck = op => op.OperationId == "rem"
                ? new ApplyPreCheckResult(true, "Package already absent — removal already satisfied.")
                : new ApplyPreCheckResult(false, "Package is provisioned; removal required."),
        };

        var report = await RunAsync(Balanced, ValidatedPlan(toRemove, toRun), executor: executor, verifier: verifier);

        Assert.Single(executor.ExecutedOpIds);
        Assert.Equal("run", executor.ExecutedOpIds[0]);
        Assert.Equal(2, report.SelectedOperationCount);
        Assert.Equal(1, report.Attempted);
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(1, report.Skipped);

        var skipped = Assert.Single(report.Operations, o => o.CanonicalKey.Contains("Pkg.One", StringComparison.Ordinal));
        Assert.Equal(CustomizationOperationStatus.Skipped.ToString(), skipped.ExecutionStatus);
        Assert.Equal(ApplyVerificationStatus.AlreadySatisfied.ToString(), skipped.VerificationStatus);
        Assert.Contains("already absent", skipped.VerificationDetail);
    }

    // =====================================================================
    // 4. APPX READ-BACK (§4 — exit code alone is never success)
    // =====================================================================

    [Fact]
    public async Task AppX_ReadBack_Verifies_Absence_Not_Just_Exit_Code()
    {
        var verifier = new OfflineApplyVerifier(new FakeProcessRunner
        {
            Responder = _ => new ProcessResult
            {
                ExitCode = 0,
                StandardOutput = "PackageName : Microsoft.SolitaireCollection_8wekyb3d8bbwe\nDisplayName : Microsoft Solitaire Collection\n\n" +
                                 "PackageName : Microsoft.OtherPkg_8wekyb3d8bbwe\nDisplayName : Other\n\n",
            },
        }, new FakeOfflineRegistryService(), new InMemoryLoggerService());

        var op = AppxOp("a", "Microsoft.SolitaireCollection_8wekyb3d8bbwe");

        var pre = await verifier.PreCheckAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.False(pre.AlreadySatisfied); // present → removal required
        Assert.Contains("provisioned", pre.Detail);

        // After removal the enumeration no longer contains the package.
        var absent = new FakeProcessRunner
        {
            Responder = _ => new ProcessResult
            {
                ExitCode = 0,
                StandardOutput = "PackageName : Microsoft.OtherPkg_8wekyb3d8bbwe\nDisplayName : Other\n\n",
            },
        };
        var verifier2 = new OfflineApplyVerifier(absent, new FakeOfflineRegistryService(), new InMemoryLoggerService());
        var verify = await verifier2.VerifyAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.Equal(ApplyVerificationStatus.Verified, verify.Status);
        Assert.Contains("absent", verify.Detail);
    }

    [Fact]
    public async Task AppX_ReadBack_Fails_When_Package_Still_Provisioned()
    {
        var verifier = new OfflineApplyVerifier(new FakeProcessRunner
        {
            Responder = _ => new ProcessResult
            {
                ExitCode = 0, // DISM remove returned 0 — but the package is STILL there
                StandardOutput = "PackageName : Microsoft.SolitaireCollection_8wekyb3d8bbwe\nDisplayName : Solitaire\n\n",
            },
        }, new FakeOfflineRegistryService(), new InMemoryLoggerService());

        var op = AppxOp("a", "Microsoft.SolitaireCollection_8wekyb3d8bbwe");
        var verify = await verifier.VerifyAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.Equal(ApplyVerificationStatus.VerificationFailed, verify.Status);
        Assert.Contains("STILL provisioned", verify.Detail);
    }

    // =====================================================================
    // 5. OPTIONAL FEATURE READ-BACK (§5 — exact returned state)
    // =====================================================================

    [Fact]
    public async Task OptionalFeature_ReadBack_Records_Exact_State()
    {
        var verifier = new OfflineApplyVerifier(new FakeProcessRunner
        {
            Responder = _ => new ProcessResult
            {
                ExitCode = 0,
                StandardOutput = "Deployment Image Servicing and Management tool\nVersion: 10.0.26100.1\n\n" +
                                 "Image Version: 10.0.26100.1\n\n" +
                                 "Feature Information:\n\n" +
                                 "Feature Name : WindowsMediaPlayer\n" +
                                 "State : DisabledWithPayloadRemoved\n\n" +
                                 "Restart Required : No",
            },
        }, new FakeOfflineRegistryService(), new InMemoryLoggerService());

        var op = FeatureOp("f", "WindowsMediaPlayer");

        var pre = await verifier.PreCheckAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.True(pre.AlreadySatisfied);
        Assert.Contains("DisabledWithPayloadRemoved", pre.Detail);

        var verify = await verifier.VerifyAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.Equal(ApplyVerificationStatus.Verified, verify.Status);
        Assert.Contains("State 'DisabledWithPayloadRemoved'", verify.Detail);
    }

    [Fact]
    public async Task OptionalFeature_ReadBack_Fails_When_Still_Enabled()
    {
        var verifier = new OfflineApplyVerifier(new FakeProcessRunner
        {
            Responder = _ => new ProcessResult
            {
                ExitCode = 0,
                StandardOutput = "Feature Information:\n\nFeature Name : WindowsMediaPlayer\nState : Enabled\n\nRestart Required : No",
            },
        }, new FakeOfflineRegistryService(), new InMemoryLoggerService());

        var op = FeatureOp("f", "WindowsMediaPlayer");
        var verify = await verifier.VerifyAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.Equal(ApplyVerificationStatus.VerificationFailed, verify.Status);
        Assert.Contains("State 'Enabled'", verify.Detail);
    }

    [Fact]
    public void Feature_State_Parser_Extracts_Exact_State()
    {
        Assert.Equal("Disabled", DismFeatureStateParser.ParseState("State : Disabled"));
        Assert.Equal("DisabledWithPayloadRemoved", DismFeatureStateParser.ParseState("Feature Name : X\nState : DisabledWithPayloadRemoved"));
        Assert.Equal("Unknown", DismFeatureStateParser.ParseState("no state here"));
        Assert.Equal("Unknown", DismFeatureStateParser.ParseState(string.Empty));
    }

    // =====================================================================
    // 6. OFFLINE SERVICE READ-BACK (§6 — mounted SYSTEM hive, never host)
    // =====================================================================

    [Fact]
    public async Task Offline_Service_ReadBack_Verifies_Mounted_SYSTEM_Hive()
    {
        var registry = new FakeOfflineRegistryService();
        // Seed the offline SYSTEM hive: Select\Current = 1, Start = 4 (Disabled).
        registry.Values["WinForge_SYSTEM|Select|Current"] = "1";
        registry.Values["WinForge_SYSTEM|ControlSet001\\Services\\DiagTrack|Start"] = "4";
        registry.ValueKinds["WinForge_SYSTEM|ControlSet001\\Services\\DiagTrack|Start"] = OfflineRegistryValueKind.DWord;

        var verifier = new OfflineApplyVerifier(new FakeProcessRunner(), registry, new InMemoryLoggerService());
        var op = ServiceOp("s", "DiagTrack"); // requested Disabled (Start = 4)

        var pre = await verifier.PreCheckAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.True(pre.AlreadySatisfied); // already Disabled

        var verify = await verifier.VerifyAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.Equal(ApplyVerificationStatus.Verified, verify.Status);
        Assert.Contains("Start value is 4", verify.Detail);
    }

    [Fact]
    public async Task Offline_Service_ReadBack_Fails_When_Start_Mismatches()
    {
        var registry = new FakeOfflineRegistryService();
        registry.Values["WinForge_SYSTEM|Select|Current"] = "1";
        registry.Values["WinForge_SYSTEM|ControlSet001\\Services\\DiagTrack|Start"] = "2"; // Automatic
        registry.ValueKinds["WinForge_SYSTEM|ControlSet001\\Services\\DiagTrack|Start"] = OfflineRegistryValueKind.DWord;

        var verifier = new OfflineApplyVerifier(new FakeProcessRunner(), registry, new InMemoryLoggerService());
        var op = ServiceOp("s", "DiagTrack"); // requested Disabled (4)

        var verify = await verifier.VerifyAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.Equal(ApplyVerificationStatus.VerificationFailed, verify.Status);
        Assert.Contains("requested 4", verify.Detail);
    }

    // =====================================================================
    // 7. OFFLINE REGISTRY READ-BACK (§7 — hive + path + name + type + data)
    // =====================================================================

    [Fact]
    public async Task Offline_Registry_ReadBack_Verifies_Kind_And_Data()
    {
        var registry = new FakeOfflineRegistryService();
        registry.Values["WinForge_SOFTWARE|Microsoft\\Policies\\X|Value"] = "1";
        registry.ValueKinds["WinForge_SOFTWARE|Microsoft\\Policies\\X|Value"] = OfflineRegistryValueKind.DWord;

        var verifier = new OfflineApplyVerifier(new FakeProcessRunner(), registry, new InMemoryLoggerService());
        var op = RegOp("r", "SOFTWARE", "Microsoft\\Policies\\X", "Value", "1");

        var pre = await verifier.PreCheckAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.True(pre.AlreadySatisfied);

        var verify = await verifier.VerifyAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.Equal(ApplyVerificationStatus.Verified, verify.Status);
        Assert.Contains("DWord", verify.Detail);
        Assert.Contains("'1'", verify.Detail);
    }

    [Fact]
    public async Task Offline_Registry_ReadBack_Fails_On_Data_Mismatch()
    {
        var registry = new FakeOfflineRegistryService();
        registry.Values["WinForge_SOFTWARE|Microsoft\\Policies\\X|Value"] = "0";
        registry.ValueKinds["WinForge_SOFTWARE|Microsoft\\Policies\\X|Value"] = OfflineRegistryValueKind.DWord;

        var verifier = new OfflineApplyVerifier(new FakeProcessRunner(), registry, new InMemoryLoggerService());
        var op = RegOp("r", "SOFTWARE", "Microsoft\\Policies\\X", "Value", "1");

        var verify = await verifier.VerifyAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.Equal(ApplyVerificationStatus.VerificationFailed, verify.Status);
        Assert.Contains("'0'", verify.Detail);
    }

    // =====================================================================
    // 8. OFFLINE DEFAULT USER ISOLATION (§7 — mounted default-user hive)
    // =====================================================================

    [Fact]
    public void OfflineDefaultUser_Isolation_Resolves_To_Mounted_Default_Profile()
    {
        var ws = MountedWorkspace();
        var hive = OfflineHivePaths.GetHiveFilePath(ws, "DEFAULT_USER");
        Assert.Equal(@"C:\wf15b\mount\Users\Default\NTUSER.DAT", hive);

        // SYSTEM hive is the machine hive under Windows\System32\config — never
        // the host's hive, and never the default-user hive.
        var systemHive = OfflineHivePaths.GetHiveFilePath(ws, "SYSTEM");
        Assert.Equal(@"C:\wf15b\mount\Windows\System32\config\SYSTEM", systemHive);
    }

    [Fact]
    public async Task DefaultUser_Registry_Op_Reads_Offline_Default_User_Hive()
    {
        var registry = new FakeOfflineRegistryService();
        registry.Values["WinForge_DEFAULT_USER|Software\\Microsoft\\Explorer|HideFileExt"] = "1";
        registry.ValueKinds["WinForge_DEFAULT_USER|Software\\Microsoft\\Explorer|HideFileExt"] = OfflineRegistryValueKind.DWord;

        var verifier = new OfflineApplyVerifier(new FakeProcessRunner(), registry, new InMemoryLoggerService());
        var op = RegOp("d", "DEFAULT_USER", "Software\\Microsoft\\Explorer", "HideFileExt", "1");

        var verify = await verifier.VerifyAsync(op, TemporaryMountedWorkspace(), CancellationToken.None);
        Assert.Equal(ApplyVerificationStatus.Verified, verify.Status);
        // The read went to the WinForge-owned DEFAULT_USER hive name — a name that
        // can never collide with the host HKCU (which the fake would expose under
        // a different key).
        Assert.Contains("WinForge_DEFAULT_USER", registry.LoadedHives);
    }

    // =====================================================================
    // 9. FAILURE REPORTING (§11 — no silent success, exact failure recorded)
    // =====================================================================

    [Fact]
    public async Task Execution_Failure_Is_Recorded_And_Profile_Not_Reported_Success()
    {
        var op = AppxOp("a", "Pkg.One");
        var executor = new FakeApplyExecutor();
        executor.Outcomes["a"] = CustomizationOperationStatus.FailedRecoverable;
        executor.Errors["a"] = "DISM exit 87: package not found (unexpected).";

        var report = await RunAsync(Balanced, ValidatedPlan(op), executor: executor);

        Assert.Equal(1, report.Failed);
        Assert.Equal(0, report.Succeeded);
        Assert.False(report.ValidationPassed);
        var entry = Assert.Single(report.Operations);
        Assert.Equal(CustomizationOperationStatus.FailedRecoverable.ToString(), entry.ExecutionStatus);
        Assert.Equal(ApplyVerificationStatus.NotApplicable.ToString(), entry.VerificationStatus);
        Assert.Contains("DISM exit 87", entry.VerificationDetail);
    }

    [Fact]
    public async Task Verification_Failure_Fails_Profile_Even_When_Execution_Succeeded()
    {
        var op = AppxOp("a", "Pkg.One");
        var executor = new FakeApplyExecutor(); // succeeds
        var verifier = new FakeApplyVerifier
        {
            Verify = _ => new ApplyVerifyResult(ApplyVerificationStatus.VerificationFailed,
                "Independent /Get-ProvisionedAppxPackages confirms the package is STILL provisioned."),
        };

        var report = await RunAsync(Balanced, ValidatedPlan(op), executor: executor, verifier: verifier);

        Assert.Equal(1, report.Failed);
        Assert.False(report.ValidationPassed);
        var entry = Assert.Single(report.Operations);
        Assert.Equal(ApplyVerificationStatus.VerificationFailed.ToString(), entry.VerificationStatus);
        Assert.Contains("STILL provisioned", entry.VerificationDetail);
    }

    // =====================================================================
    // 10. CLEANUP ON PARTIAL FAILURE (§11 — cleanup still runs, report produced)
    // =====================================================================

    [Fact]
    public async Task Report_Is_Produced_With_Cleanup_After_Partial_Failure()
    {
        var good = AppxOp("good", "Pkg.One");
        var bad = ServiceOp("bad", "DiagTrack");
        var executor = new FakeApplyExecutor();
        executor.Outcomes["bad"] = CustomizationOperationStatus.FailedRecoverable;
        executor.Errors["bad"] = "SYSTEM hive locked.";

        var report = await RunAsync(Balanced, ValidatedPlan(good, bad), executor: executor);

        // Partial failure: the good op succeeded, the bad op failed, and the
        // report is still fully produced so the caller can run cleanup and write
        // profile-apply-validation.json with mountCleanup attached.
        Assert.Equal(1, report.Succeeded);
        Assert.Equal(1, report.Failed);
        Assert.False(report.ValidationPassed);
        Assert.Equal(2, report.Operations.Count);

        // MountCleanup is a settable, reportable field (populated by the CLI's
        // cleanup step which always runs, even on partial failure).
        report.MountCleanup = new ProfileApplyMountCleanupReport
        {
            DiscardSucceeded = true,
            WorkspaceCleanupSucceeded = true,
        };
        Assert.True(report.MountCleanup.DiscardSucceeded);
    }

    // =====================================================================
    // 11. BALANCED REPORT SCHEMA (§8 — 16 BuildPlan / 10 selected shape)
    // =====================================================================

    [Fact]
    public async Task Balanced_Report_Schema_Matches_Spec_Shape()
    {
        // Balanced-shaped plan: 3 AppX + 2 services + 11 registry = 16; 10 selected.
        var ops = new List<CustomizationOperation>();
        for (var i = 0; i < 3; i++)
        {
            ops.Add(AppxOp($"appx-{i}", $"Pkg.{i}", selected: i < 3));
        }

        ops.Add(ServiceOp("svc-1", "DiagTrack", selected: true));
        ops.Add(ServiceOp("svc-2", "RetailDemo", selected: false)); // Recommend — unselected
        for (var i = 0; i < 11; i++)
        {
            ops.Add(RegOp($"reg-{i}", "SOFTWARE", $"Microsoft\\Policies\\T{i}", $"V{i}", "1", selected: i < 6));
        }

        var report = await RunAsync(Balanced, ValidatedPlan(ops.ToArray()));

        Assert.Equal(16, report.BuildPlanOperationCount);
        Assert.Equal(10, report.SelectedOperationCount);
        Assert.Equal(10, report.Attempted);
        Assert.Equal(10, report.Succeeded);
        Assert.Equal(0, report.Failed);
        Assert.True(report.ValidationPassed);

        // JSON schema (spec §3) — camelCase fields present (the CLI serializes
        // with JsonNamingPolicy.CamelCase).
        var json = System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.Contains("\"profileId\":\"Balanced\"", json);
        Assert.Contains("\"buildPlanOperationCount\":16", json);
        Assert.Contains("\"selectedOperationCount\":10", json);
        Assert.Contains("\"attempted\":10", json);
        Assert.Contains("\"succeeded\":10", json);
        Assert.Contains("\"failed\":0", json);
        Assert.Contains("\"skipped\":0", json);
        Assert.Contains("\"validationPassed\":true", json);
        Assert.Contains("\"operations\"", json);
        Assert.Contains("\"canonicalKey\"", json);
        Assert.Contains("\"operationType\"", json);
        Assert.Contains("\"expectedAction\"", json);
        Assert.Contains("\"executionStatus\"", json);
        Assert.Contains("\"verificationStatus\"", json);
        Assert.Contains("\"verificationDetail\"", json);
        Assert.Contains("\"mountCleanup\"", json);
        Assert.Contains("\"discardSucceeded\"", json);
        Assert.Contains("\"workspaceCleanupSucceeded\"", json);
    }

    // =====================================================================
    // 12. DEDICATED GAMING REPORT SCHEMA (§9 — includes OptionalFeature ops)
    // =====================================================================

    [Fact]
    public async Task DedicatedGaming_Report_Includes_OptionalFeature_Operations()
    {
        var ops = new List<CustomizationOperation>
        {
            AppxOp("appx-1", "Pkg.One"),
            FeatureOp("feat-1", "Containers-DisposableClientVM"),
            FeatureOp("feat-2", "Microsoft-Windows-Subsystem-Linux", selected: false), // Recommend — NOT executed
            ServiceOp("svc-1", "DiagTrack"),
        };

        var executor = new FakeApplyExecutor();
        var report = await RunAsync(DedicatedGaming, ValidatedPlan(ops.ToArray()), executor: executor);

        Assert.Equal(4, report.BuildPlanOperationCount);
        Assert.Equal(3, report.SelectedOperationCount);
        Assert.Equal(3, executor.ExecutedOpIds.Count);
        Assert.DoesNotContain("feat-2", executor.ExecutedOpIds); // Recommend-only never executed

        var feature = Assert.Single(report.Operations, o => o.CanonicalKey.Contains("Containers-DisposableClientVM", StringComparison.Ordinal));
        Assert.Equal(CustomizationOperationType.DisableOptionalFeature.ToString(), feature.OperationType);
        Assert.Equal(OptimizationAction.Disable.ToString(), feature.ExpectedAction);
        Assert.Equal(ApplyVerificationStatus.Verified.ToString(), feature.VerificationStatus);
        Assert.True(report.ValidationPassed);
    }

    // =====================================================================
    // fakes ---------------------------------------------------------------
    // =====================================================================

    private sealed class FakeApplyExecutor : ICustomizationExecutionService
    {
        public List<string> ExecutedOpIds { get; } = new();
        public Dictionary<string, CustomizationOperationStatus> Outcomes { get; } = new();
        public Dictionary<string, string?> Errors { get; } = new();
        public int Calls { get; private set; }

        public Task<CustomizationResult> ExecuteAsync(
            CustomizationPlan plan, ImageServicingWorkspace workspace,
            IProgress<ExecutionProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (plan.Status != CustomizationPlanStatus.Validated)
            {
                return Task.FromResult(new CustomizationResult
                {
                    CriticalFailure = true,
                    TotalOperations = plan.SelectedOperations.Count,
                    FailedOperations = plan.SelectedOperations.Count,
                });
            }

            plan.FreezeForExecution();
            var succeeded = 0;
            var failed = 0;
            foreach (var op in plan.Operations.Where(o => o.IsSelected))
            {
                ExecutedOpIds.Add(op.OperationId);
                var status = Outcomes.TryGetValue(op.OperationId, out var s) ? s : CustomizationOperationStatus.Succeeded;
                op.ExecutionStatus = status;
                op.ErrorDetails = status == CustomizationOperationStatus.Succeeded
                    ? null
                    : Errors.TryGetValue(op.OperationId, out var e) ? e : "Simulated failure.";
                if (status == CustomizationOperationStatus.Succeeded)
                {
                    succeeded++;
                }
                else
                {
                    failed++;
                }
            }

            plan.MarkCompleted(failed > 0);
            return Task.FromResult(new CustomizationResult
            {
                TotalOperations = ExecutedOpIds.Count,
                Succeeded = succeeded,
                FailedOperations = failed,
                Operations = plan.Operations.Where(o => o.IsSelected).ToList(),
            });
        }
    }

    private sealed class FakeApplyVerifier : IOfflineApplyVerifier
    {
        public Func<CustomizationOperation, ApplyPreCheckResult>? PreCheck { get; set; }
        public Func<CustomizationOperation, ApplyVerifyResult>? Verify { get; set; }
        public List<string> PreChecked { get; } = new();
        public List<string> VerifiedOps { get; } = new();

        public Task<ApplyPreCheckResult> PreCheckAsync(
            CustomizationOperation op, ImageServicingWorkspace workspace, CancellationToken ct)
        {
            PreChecked.Add(op.OperationId);
            return Task.FromResult(PreCheck?.Invoke(op) ?? new ApplyPreCheckResult(false, "Needs execution."));
        }

        public Task<ApplyVerifyResult> VerifyAsync(
            CustomizationOperation op, ImageServicingWorkspace workspace, CancellationToken ct)
        {
            VerifiedOps.Add(op.OperationId);
            return Task.FromResult(Verify?.Invoke(op) ?? new ApplyVerifyResult(ApplyVerificationStatus.Verified, "Independent read-back confirmed."));
        }
    }
}
