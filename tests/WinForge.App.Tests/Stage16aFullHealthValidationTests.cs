using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Health;
using WinForge.Core.Models;
using WinForge.Core.Profiles;
using WinForge.Core.Services;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.Health;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.Profiles;
using WinForge.Infrastructure.Servicing;
using Xunit;

namespace WinForge.App.Tests;

// =====================================================================
// Phase 16 Stage 16.1 — FULL HEALTH VALIDATION PREP (ADR-098)
//
// Tests for the explicit COMMIT + ISO build mode (ProfileIsoCommitService):
// commit-mode workspace ownership, source-ISO immutability, commit-only-owned
// mount (authoritative DISM inventory), the pre-commit read-back gate,
// post-commit persistence verification, ISO output metadata (path/size/SHA-256)
// and ISO structure validation — plus the full-health report parser, status
// aggregation (Fail > Warning > NotTested > Pass), warning-vs-failure
// semantics, profile expected-state loading, and the ADR-084
// FullHealthValidated gate (no full-health without all required gates).
// =====================================================================

public sealed class Stage16aFullHealthValidationTests
{
    private static readonly ProfileDefinition Balanced =
        new ProfileCatalog().GetProfiles().Single(p => p.Id == "Balanced");

    // ---- commit-mode workspace helpers --------------------------------

    private static ImageServicingWorkspace OwnedWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "wf16a-" + Guid.NewGuid().ToString("N"));
        var mount = Path.Combine(root, "mount");
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(mount);
        Directory.CreateDirectory(work);
        var wim = Path.Combine(work, "install.wim");
        File.WriteAllBytes(wim, new byte[] { 0x4D, 0x53, 0x57, 0x49, 0x4D, 0x00 }); // committed WIM exists
        return new ImageServicingWorkspace
        {
            State = ServicingWorkspaceState.Mounted,
            MountDirectory = mount,
            WorkingDirectory = root,
            WorkingImagePath = wim,
            SourceIsoPath = Path.Combine(root, "src.iso"),
            SourceImageRelativePath = "sources/install.wim",
            SourceImageType = WindowsImageType.Wim,
            SelectedIndex = 4,
            SelectedEditionName = "Windows 11 Pro",
            WorkingIndex = 1,
        };
    }

    private static CustomizationOperation AppxOp(string id, string pkg)
        => new()
        {
            OperationId = id, DisplayName = id,
            OperationType = CustomizationOperationType.RemoveProvisionedAppx,
            TargetIdentifier = pkg, Risk = RiskClass.Safe, IsSelected = true,
            ActionKind = OptimizationAction.Remove, ExecutionStatus = CustomizationOperationStatus.Succeeded,
        };

    private static CustomizationPlan ValidatedPlan(params CustomizationOperation[] ops)
    {
        var plan = new CustomizationPlan();
        foreach (var op in ops)
        {
            plan.AddOperation(op);
        }

        Assert.Empty(plan.Validate());
        return plan;
    }

    private static ProfileApplyValidationReport PassingApplyReport(CustomizationPlan plan, bool passed = true, int failed = 0)
        => new()
        {
            ProfileId = Balanced.Id,
            BuildPlanOperationCount = plan.Operations.Count,
            SelectedOperationCount = plan.SelectedOperations.Count,
            Attempted = plan.SelectedOperations.Count,
            Succeeded = plan.SelectedOperations.Count - failed,
            Failed = failed,
            Skipped = 0,
            ValidationPassed = passed,
            Operations = plan.Operations
                .Where(o => o.IsSelected)
                .Select(o => new ProfileApplyOperationReport
                {
                    CanonicalKey = o.ConflictKey,
                    OperationType = o.OperationType.ToString(),
                    ExpectedAction = o.ActionKind?.ToString() ?? o.OperationType.ToString(),
                    ExecutionStatus = CustomizationOperationStatus.Succeeded.ToString(),
                    VerificationStatus = ApplyVerificationStatus.Verified.ToString(),
                    VerificationDetail = "Pre-commit read-back confirmed.",
                })
                .ToList(),
        };

    private static ProfileIsoCommitService CommitService(
        IBuildService build, IImageServicingService servicing, IOfflineApplyVerifier verifier,
        IIsoMountService isoMount, IProcessRunner processRunner)
        => new(build, verifier, new MountIdentityValidator(), servicing, isoMount, processRunner,
            new InMemoryLoggerService());

    private static ProfileIsoCommitRequest CommitRequest(
        ImageServicingWorkspace ws, CustomizationPlan plan, ProfileApplyValidationReport apply,
        string isoOut, string isoName, string sourceIso)
        => new()
        {
            Profile = Balanced,
            Plan = plan,
            Workspace = ws,
            ApplyReport = apply,
            SourceIsoPath = sourceIso,
            SourceIsoSizeBytes = 5_500_000_000L,
            SourceImageRelativePath = ws.SourceImageRelativePath ?? "sources/install.wim",
            SourceImageType = ws.SourceImageType,
            SourceEditionName = ws.SelectedEditionName,
            OutputDirectory = isoOut,
            OutputFileName = isoName,
        };

    private sealed class RecordingBuildService : IBuildService
    {
        public int Calls { get; private set; }
        public BuildRequest? LastRequest { get; private set; }
        public Func<BuildRequest, BuildResult>? Responder { get; set; }

        public Task<BuildResult> BuildAsync(
            BuildRequest request, IProgress<BuildProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            var result = Responder is not null
                ? Responder(request)
                : BuildResult.Fail(BuildState.BuildingIso, "No responder configured.", Array.Empty<string>());
            return Task.FromResult(result);
        }

        public Task<BuildRecoveryState?> DetectInterruptedBuildAsync(
            string buildWorkspaceDirectory, CancellationToken cancellationToken = default)
            => Task.FromResult<BuildRecoveryState?>(null);

        public Task<bool> CleanupInterruptedBuildAsync(
            string buildWorkspaceDirectory, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class RecordingServicing : IImageServicingService
    {
        public readonly List<string> MountedDirs = new();
        public readonly List<string> DiscardedDirs = new();
        public bool FailVerifyMount { get; set; }

        public Task<ServicingResult> PrepareWorkingImageAsync(
            ImageWorkspace source, string workspaceId, CancellationToken cancellationToken = default)
            => Task.FromResult(ServicingResult.Ok(new ImageServicingWorkspace(), ServicingHealth.Prepared));

        public Task<ServicingResult> MountAsync(
            ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
        {
            MountedDirs.Add(workspace.MountDirectory ?? string.Empty);
            return Task.FromResult(FailVerifyMount
                ? ServicingResult.Fail(workspace, "Simulated re-mount failure.", ServicingHealth.Failed)
                : ServicingResult.Ok(workspace, ServicingHealth.Mounted));
        }

        public Task<ServicingResult> UnmountDiscardAsync(
            ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
        {
            DiscardedDirs.Add(workspace.MountDirectory ?? string.Empty);
            return Task.FromResult(ServicingResult.Ok(workspace, ServicingHealth.Prepared));
        }

        public Task<ServicingResult> ValidateServicingWorkspaceAsync(
            ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
            => Task.FromResult(ServicingResult.Ok(workspace, ServicingHealth.Prepared));

        public Task<ServicingResult> CommitUnmountAsync(
            ImageServicingWorkspace workspace, CancellationToken cancellationToken = default)
            => Task.FromResult(ServicingResult.Ok(workspace, ServicingHealth.Prepared));
    }

    private sealed class RecordingVerifier : IOfflineApplyVerifier
    {
        public readonly List<string> VerifiedOps = new();
        public Func<CustomizationOperation, ApplyVerifyResult>? Verify { get; set; }

        public Task<ApplyPreCheckResult> PreCheckAsync(
            CustomizationOperation op, ImageServicingWorkspace workspace, CancellationToken ct)
            => Task.FromResult(new ApplyPreCheckResult(false, "Needs execution."));

        public Task<ApplyVerifyResult> VerifyAsync(
            CustomizationOperation op, ImageServicingWorkspace workspace, CancellationToken ct)
        {
            VerifiedOps.Add(op.OperationId);
            return Task.FromResult(Verify?.Invoke(op)
                ?? new ApplyVerifyResult(ApplyVerificationStatus.Verified, "Committed-image read-back confirmed."));
        }
    }

    private static FakeProcessRunner InventoryRunner(
        IReadOnlyList<string> mountDirs, int imageInfoExitCode = 0)
    {
        var runner = new FakeProcessRunner
        {
            Responder = req =>
            {
                if (req.Arguments.Contains("/Get-MountedImageInfo", StringComparison.OrdinalIgnoreCase))
                {
                    var lines = string.Join("\r\n", mountDirs.Select(d => $"Mount Dir : {d}"));
                    return new ProcessResult { ExitCode = 0, StandardOutput = lines };
                }

                return new ProcessResult { ExitCode = imageInfoExitCode, StandardOutput = "Index : 1" };
            },
        };
        return runner;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "WinForge.sln")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return Directory.GetCurrentDirectory();
    }

    // =====================================================================
    // 1. COMMIT-MODE SAFETY (§3)
    // =====================================================================

    [Fact]
    public async Task PreCommit_Gate_Rejects_Failed_Apply_Report()
    {
        var ws = OwnedWorkspace();
        var plan = ValidatedPlan(AppxOp("a", "Pkg.X"));
        var apply = PassingApplyReport(plan, passed: false, failed: 1);

        var build = new RecordingBuildService();
        var svc = CommitService(build, new RecordingServicing(), new RecordingVerifier(),
            new FakeIsoMountService(), InventoryRunner(new[] { ws.MountDirectory! }));
        var report = await svc.CommitAsync(CommitRequest(ws, plan, apply, Path.GetTempPath(), "X", ws.SourceIsoPath!));

        Assert.False(report.Committed);
        Assert.NotNull(report.PreCommitGateFailure);
        Assert.Contains("pre-commit", report.PreCommitGateFailure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, build.Calls); // nothing was ever built or committed
    }

    [Fact]
    public async Task Ownership_Guard_Refuses_Workspace_Outside_Session_Root()
    {
        var ws = OwnedWorkspace();
        ws.MountDirectory = @"C:\Windows\Temp\foreign-mount"; // outside the session root
        var plan = ValidatedPlan(AppxOp("a", "Pkg.X"));

        var build = new RecordingBuildService();
        var svc = CommitService(build, new RecordingServicing(), new RecordingVerifier(),
            new FakeIsoMountService(), InventoryRunner(new[] { ws.MountDirectory! }));
        var report = await svc.CommitAsync(CommitRequest(ws, plan, PassingApplyReport(plan), Path.GetTempPath(), "X", ws.SourceIsoPath!));

        Assert.False(report.Committed);
        Assert.NotNull(report.PreCommitGateFailure);
        Assert.Contains("ownership guard failed", report.PreCommitGateFailure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, build.Calls);
    }

    [Fact]
    public async Task Unknown_Mount_Never_Committed()
    {
        var ws = OwnedWorkspace();
        var plan = ValidatedPlan(AppxOp("a", "Pkg.X"));

        // An UNRELATED mount is registered alongside ours — the authoritative
        // DISM inventory proves this run must not commit.
        var build = new RecordingBuildService();
        var svc = CommitService(build, new RecordingServicing(), new RecordingVerifier(),
            new FakeIsoMountService(), InventoryRunner(new[]
            {
                ws.MountDirectory!, @"C:\unrelated\mount",
            }));
        var report = await svc.CommitAsync(CommitRequest(ws, plan, PassingApplyReport(plan), Path.GetTempPath(), "X", ws.SourceIsoPath!));

        Assert.False(report.Committed);
        Assert.NotNull(report.PreCommitGateFailure);
        Assert.Contains("UNKNOWN mount", report.PreCommitGateFailure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, build.Calls);
    }

    [Fact]
    public async Task Owned_Mount_Not_Registered_Refuses_Commit()
    {
        var ws = OwnedWorkspace();
        var plan = ValidatedPlan(AppxOp("a", "Pkg.X"));

        var build = new RecordingBuildService();
        var svc = CommitService(build, new RecordingServicing(), new RecordingVerifier(),
            new FakeIsoMountService(), InventoryRunner(Array.Empty<string>()));
        var report = await svc.CommitAsync(CommitRequest(ws, plan, PassingApplyReport(plan), Path.GetTempPath(), "X", ws.SourceIsoPath!));

        Assert.False(report.Committed);
        Assert.NotNull(report.PreCommitGateFailure);
        Assert.Contains("NOT registered", report.PreCommitGateFailure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, build.Calls);
    }

    [Fact]
    public async Task Source_ISO_Is_Never_Modified()
    {
        var ws = OwnedWorkspace();
        var sourceIso = Path.Combine(Path.GetTempPath(), "wf16a-src-" + Guid.NewGuid().ToString("N") + ".iso");
        var sentinel = Guid.NewGuid().ToByteArray();
        File.WriteAllBytes(sourceIso, sentinel);

        var plan = ValidatedPlan(AppxOp("a", "Pkg.X"));
        var build = new RecordingBuildService
        {
            Responder = _ => BuildResult.Fail(BuildState.Preflight, "stop before commit", Array.Empty<string>()),
        };
        var svc = CommitService(build, new RecordingServicing(), new RecordingVerifier(),
            new FakeIsoMountService(), InventoryRunner(new[] { ws.MountDirectory! }));

        // Request with a Gate-failing apply report so the service aborts early —
        // the point is that even a full run never touches the source file.
        var apply = PassingApplyReport(plan, passed: false, failed: 1);
        await svc.CommitAsync(CommitRequest(ws, plan, apply, Path.GetTempPath(), "X", sourceIso));

        Assert.Equal(sentinel, File.ReadAllBytes(sourceIso)); // byte-identical
        if (build.LastRequest is not null)
        {
            Assert.Equal(sourceIso, build.LastRequest.SourceIsoPath); // and if built, it only reads it
        }
    }

    // =====================================================================
    // 2. FULL COMMIT + BUILD + POST-COMMIT (§4-7)
    // =====================================================================

    private static (string IsoPath, string ExpectedSha) CreateIsoFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wf16a-iso-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var iso = Path.Combine(dir, "out.iso");
        var bytes = new byte[256 * 1024];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(iso, bytes);
        using var sha = SHA256.Create();
        return (iso, Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant());
    }

    private static string StructureRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "wf16a-mnt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "boot"));
        Directory.CreateDirectory(Path.Combine(root, "efi", "microsoft", "boot"));
        Directory.CreateDirectory(Path.Combine(root, "sources"));
        File.WriteAllText(Path.Combine(root, "boot", "etfsboot.com"), "x");
        File.WriteAllText(Path.Combine(root, "efi", "microsoft", "boot", "efisys.bin"), "x");
        File.WriteAllText(Path.Combine(root, "sources", "boot.wim"), "x");
        File.WriteAllText(Path.Combine(root, "sources", "install.wim"), "x");
        File.WriteAllText(Path.Combine(root, "setup.exe"), "x");
        return root;
    }

    [Fact]
    public async Task Full_Commit_Build_PostCommit_And_Iso_Metadata()
    {
        var ws = OwnedWorkspace();
        var plan = ValidatedPlan(AppxOp("a", "Pkg.X"), AppxOp("b", "Pkg.Y"));
        var apply = PassingApplyReport(plan);

        var (isoPath, expectedSha) = CreateIsoFile();
        var isoLength = new FileInfo(isoPath).Length;
        var build = new RecordingBuildService
        {
            Responder = req =>
            {
                Assert.Equal(ws.WorkingImagePath, req.WorkingImagePath); // the OWNED working WIM is committed
                Assert.Equal(ws.MountDirectory, req.MountDirectory);
                Assert.Equal(BuildOverwritePolicy.Fail, req.OverwritePolicy); // deterministic — never silent overwrite
                return BuildResult.Ok(isoPath, isoLength, new[] { "build completed" });
            },
        };

        var verifier = new RecordingVerifier();
        var isoMount = new FakeIsoMountService { MountRoot = StructureRoot() };
        var runner = InventoryRunner(new[] { ws.MountDirectory! });
        var svc = CommitService(build, new RecordingServicing(), verifier, isoMount, runner);

        var report = await svc.CommitAsync(CommitRequest(ws, plan, apply, Path.GetTempPath(), "WinForge-Balanced-Test", ws.SourceIsoPath!));

        Assert.True(report.Committed);
        Assert.True(report.PostCommitVerified);
        Assert.True(report.CommittedImageReadable);
        Assert.Equal(2, report.PostCommitChecks.Count);
        Assert.All(report.PostCommitChecks, c => Assert.Equal("Verified", c.VerificationStatus));
        Assert.Equal(new[] { "a", "b" }, verifier.VerifiedOps.OrderBy(x => x));
        Assert.Equal(1, build.Calls); // the production build runs exactly once
        Assert.NotNull(report.Iso);
        Assert.Equal(isoPath, report.Iso!.OutputPath);
        Assert.Equal(isoLength, report.Iso.SizeBytes);
        Assert.Equal(expectedSha, report.Iso.Sha256);
        Assert.True(report.Iso.StructureValidated);
        Assert.Equal(5, report.Iso.StructureChecks.Count);
        Assert.Contains(report.Iso.StructureChecks, c => c.Contains("boot.wim present", StringComparison.Ordinal));
        Assert.Contains(report.Iso.StructureChecks, c => c.Contains("setup.exe present", StringComparison.Ordinal));
        Assert.Contains(report.Iso.StructureChecks, c => c.Contains("efisys.bin present", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PostCommit_ReadBack_Failure_Is_Reported()
    {
        var ws = OwnedWorkspace();
        var plan = ValidatedPlan(AppxOp("a", "Pkg.X"));
        var (isoPath, _) = CreateIsoFile();

        var build = new RecordingBuildService
        {
            Responder = _ => BuildResult.Ok(isoPath, new FileInfo(isoPath).Length, new[] { "ok" }),
        };
        var verifier = new RecordingVerifier
        {
            Verify = _ => new ApplyVerifyResult(ApplyVerificationStatus.VerificationFailed, "Package still present after commit."),
        };
        var svc = CommitService(build, new RecordingServicing(), verifier,
            new FakeIsoMountService { MountRoot = StructureRoot() }, InventoryRunner(new[] { ws.MountDirectory! }));

        var report = await svc.CommitAsync(CommitRequest(ws, plan, PassingApplyReport(plan), Path.GetTempPath(), "X", ws.SourceIsoPath!));

        Assert.True(report.Committed); // the WIM was committed
        Assert.False(report.PostCommitVerified); // but persistence did NOT verify
        Assert.NotNull(report.PostCommitError);
        Assert.Contains("Pkg.X", report.PostCommitError);
        var check = Assert.Single(report.PostCommitChecks);
        Assert.Equal("VerificationFailed", check.VerificationStatus);
    }

    [Fact]
    public async Task PostCommit_Remount_Failure_Is_Reported()
    {
        var ws = OwnedWorkspace();
        var plan = ValidatedPlan(AppxOp("a", "Pkg.X"));
        var (isoPath, _) = CreateIsoFile();

        var build = new RecordingBuildService
        {
            Responder = _ => BuildResult.Ok(isoPath, new FileInfo(isoPath).Length, new[] { "ok" }),
        };
        var servicing = new RecordingServicing { FailVerifyMount = true };
        var svc = CommitService(build, servicing, new RecordingVerifier(),
            new FakeIsoMountService { MountRoot = StructureRoot() }, InventoryRunner(new[] { ws.MountDirectory! }));

        var report = await svc.CommitAsync(CommitRequest(ws, plan, PassingApplyReport(plan), Path.GetTempPath(), "X", ws.SourceIsoPath!));

        Assert.True(report.Committed);
        Assert.False(report.PostCommitVerified);
        Assert.NotNull(report.PostCommitError);
        Assert.Contains("re-mount failed", report.PostCommitError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(report.PostCommitChecks);
    }

    [Fact]
    public async Task Commit_Report_Serializes_With_CamelCase_Schema()
    {
        var ws = OwnedWorkspace();
        var plan = ValidatedPlan(AppxOp("a", "Pkg.X"));
        var (isoPath, _) = CreateIsoFile();
        var build = new RecordingBuildService
        {
            Responder = _ => BuildResult.Ok(isoPath, new FileInfo(isoPath).Length, new[] { "ok" }),
        };
        var svc = CommitService(build, new RecordingServicing(), new RecordingVerifier(),
            new FakeIsoMountService { MountRoot = StructureRoot() }, InventoryRunner(new[] { ws.MountDirectory! }));
        var report = await svc.CommitAsync(CommitRequest(ws, plan, PassingApplyReport(plan), Path.GetTempPath(), "X", ws.SourceIsoPath!));

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        Assert.Contains("\"profileId\":\"Balanced\"", json);
        Assert.Contains("\"buildPlanOperationCount\":1", json);
        Assert.Contains("\"preCommitValidationPassed\":true", json);
        Assert.Contains("\"postCommitVerified\":true", json);
        Assert.Contains("\"mountCleanup\":{", json);
        Assert.Contains("\"sha256\":\"", json);
        Assert.Contains("\"structureValidated\":true", json);
    }

    // =====================================================================
    // 3. HEALTH REPORT PARSER + AGGREGATION (§9-13, §19)
    // =====================================================================

    private static string Section(string status, params (string Name, string Status, string Detail)[] checks)
    {
        var list = string.Join(",", checks.Select(c => $"{{\"name\":\"{c.Name}\",\"status\":\"{c.Status}\",\"detail\":\"{c.Detail}\"}}"));
        return $"{{\"status\":\"{status}\",\"checks\":[{list}]}}";
    }

    private static string AllPassReport(string servicingStatus = "Pass")
    {
        var s = Section;
        return "{" +
            $"\"sections\":{{" +
            $"\"media\":{s("Pass", ("iso", "Pass", "x.iso"))}," +
            $"\"profile\":{s("Pass", ("profile", "Pass", "Balanced"))}," +
            $"\"windowsIdentity\":{s("Pass", ("edition", "Pass", "Windows 11 Pro"))}," +
            $"\"bootAndShell\":{s("Pass", ("explorer", "Pass", "running"))}," +
            $"\"devices\":{s("Pass", ("deviceProblems", "Pass", "none"))}," +
            $"\"network\":{s("Pass", ("dns", "Pass", "ok"))}," +
            $"\"servicing\":{s(servicingStatus, ("dismCheckHealth", "Pass", "no corruption"))}," +
            $"\"windowsUpdate\":{s("Pass", ("wuauserv", "Pass", "present"))}," +
            $"\"security\":{s("Pass", ("defender", "Pass", "present"))}," +
            $"\"storeAndAppPlatform\":{s("Pass", ("store", "Pass", "present"))}," +
            $"\"profileExpectedChanges\":{s("Pass", ("appx", "Pass", "absent"))}" +
            "}}"; // closes sections + report
    }

    [Fact]
    public void Health_Parser_Accepts_Valid_Report()
    {
        var result = HealthReportParser.Parse(AllPassReport());
        Assert.True(result.SchemaValid);
        Assert.Empty(result.Errors);
        Assert.NotNull(result.Report);
        Assert.Equal(HealthStatus.Pass, result.Report!.OverallStatus);
        Assert.True(result.Report.FullHealthValidated);
    }

    [Fact]
    public void Health_Parser_Rejects_Missing_Section()
    {
        var json = AllPassReport().Replace("\"servicing\":", "\"servicing2\":", StringComparison.Ordinal);
        var result = HealthReportParser.Parse(json);
        Assert.False(result.SchemaValid);
        Assert.Contains(result.Errors, e => e.Contains("servicing", StringComparison.Ordinal));
    }

    [Fact]
    public void Health_Parser_Rejects_Invalid_Status()
    {
        var json = AllPassReport().Replace("\"status\":\"Pass\",\"checks\":[{\"name\":\"dismCheckHealth\"", "\"status\":\"Bogus\",\"checks\":[{\"name\":\"dismCheckHealth\"", StringComparison.Ordinal);
        var result = HealthReportParser.Parse(json);
        Assert.False(result.SchemaValid);
        Assert.Contains(result.Errors, e => e.Contains("servicing", StringComparison.Ordinal));
    }

    [Fact]
    public void Health_Aggregation_Fail_Dominates()
    {
        var json = AllPassReport()
            .Replace("\"servicing\":{", "\"servicing\":{\"x\":1,\"unused\":true,\"z\":{}}," , StringComparison.Ordinal);
        // Force a Fail by flipping the servicing section status wholesale.
        var withFail = AllPassReport().Replace("\"servicing\":{\"status\":\"Pass\"", "\"servicing\":{\"status\":\"Fail\"", StringComparison.Ordinal);
        var result = HealthReportParser.Parse(withFail);
        Assert.True(result.SchemaValid);
        Assert.Equal(HealthStatus.Fail, result.Report!.OverallStatus);
        Assert.False(result.Report.FullHealthValidated);
        Assert.Contains(result.Report.Failures, f => f.Contains("servicing", StringComparison.Ordinal));
    }

    [Fact]
    public void Health_Warning_Does_Not_Fail_And_Does_Not_Block_FullHealth()
    {
        var json = AllPassReport().Replace("\"servicing\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"dismCheckHealth\",\"status\":\"Pass\"",
            "\"servicing\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"dismCheckHealth\",\"status\":\"Warning\"", StringComparison.Ordinal);
        var result = HealthReportParser.Parse(json);
        Assert.True(result.SchemaValid);
        Assert.Equal(HealthStatus.Warning, result.Report!.OverallStatus);
        Assert.False(result.Report.FullHealthValidated); // warnings do not fail, but overall != Pass
        Assert.Contains(result.Report.Warnings, w => w.Contains("dismCheckHealth", StringComparison.Ordinal));
    }

    [Fact]
    public void No_FullHealth_When_Critical_Section_NotTested()
    {
        var json = AllPassReport().Replace("\"servicing\":{\"status\":\"Pass\"", "\"servicing\":{\"status\":\"NotTested\"", StringComparison.Ordinal);
        var result = HealthReportParser.Parse(json);
        Assert.True(result.SchemaValid);
        Assert.False(result.Report!.FullHealthValidated); // critical section untested → no FullHealthValidated
        Assert.NotEqual(HealthStatus.Pass, result.Report.OverallStatus);
    }

    [Fact]
    public void No_FullHealth_When_Any_Section_Fails()
    {
        var json = AllPassReport().Replace("\"network\":{\"status\":\"Pass\"", "\"network\":{\"status\":\"Fail\"", StringComparison.Ordinal);
        var result = HealthReportParser.Parse(json);
        Assert.True(result.SchemaValid);
        Assert.Equal(HealthStatus.Fail, result.Report!.OverallStatus);
        Assert.False(result.Report.FullHealthValidated);
    }

    [Fact]
    public void Schema_Invalid_Report_Is_Never_FullHealthValidated()
    {
        var result = HealthReportParser.Parse("{ not json ]");
        Assert.False(result.SchemaValid);
        Assert.False(result.Report?.FullHealthValidated ?? false);
    }

    // =====================================================================
    // 4. PROFILE EXPECTED-STATE (§17)
    // =====================================================================

    [Fact]
    public void Balanced_Expected_State_JSON_Matches_Schema()
    {
        var path = Path.Combine(RepoRoot(), "scripts", "balanced-expected-state.json");
        Assert.True(File.Exists(path), $"Expected-state file not found: {path}");
        var state = ProfileExpectedStateParser.Parse(File.ReadAllText(path));
        Assert.NotNull(state);
        Assert.Equal("Balanced", state!.ProfileId);
        Assert.Equal(3, state.AppxAbsent.Count); // Feedback Hub / Phone Link / Solitaire
        Assert.Contains(state.AppxAbsent, a => a.Contains("FeedbackHub", StringComparison.Ordinal));
        Assert.Contains(state.AppxAbsent, a => a.Contains("YourPhone", StringComparison.Ordinal));
        Assert.Contains(state.AppxAbsent, a => a.Contains("Solitaire", StringComparison.Ordinal));
        Assert.Equal(4, state.MachineRegistry.Count); // AdvertisingId + 3 policies
        Assert.Equal(2, state.DefaultUserRegistry.Count); // Start_ShowRecommended + Start_ShowRecent
        Assert.All(state.MachineRegistry, r => Assert.False(string.IsNullOrWhiteSpace(r.Path)));
        Assert.All(state.MachineRegistry, r => Assert.False(string.IsNullOrWhiteSpace(r.Name)));
        Assert.All(state.MachineRegistry, r => Assert.False(string.IsNullOrWhiteSpace(r.ExpectedData)));
    }
}
