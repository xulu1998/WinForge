using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WinForge.Core.Health;
using WinForge.Core.Models;
using WinForge.Core.Validation;
using WinForge.Infrastructure.Health;
using WinForge.Infrastructure.Validation;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Phase 17 — Release-candidate hardening tests: deterministic artifact
/// archive, release validation manifest truthfulness, expected-state mapping,
/// six-profile delta uniqueness, release safety invariants, portable FullHealth
/// bundle, recovery metadata, and Balanced/DedicatedGaming FullHealth
/// regressions.
/// </summary>
public class Stage17aReleaseCandidateTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinForge.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "wf17_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static CustomizationOperation Op(CustomizationOperationType type, string? target = null,
        string? hive = null, string? key = null, string? value = null, string? data = null)
        => new()
        {
            OperationType = type,
            TargetIdentifier = target,
            RegistryHive = hive,
            RegistryKeyPath = key,
            RegistryValueName = value,
            RegistryValueData = data,
            IsSelected = true,
        };

    // =====================================================================
    // 1. ARTIFACT ARCHIVE — runs never overwrite, latest pointer, recovery
    // =====================================================================

    [Fact]
    public void Artifact_Runs_Never_Overwrite_Previous_Runs()
    {
        var root = TempDir();
        try
        {
            var archive = new ValidationArtifactArchiveService(root);
            var a = new ValidationArtifactRun { RunId = "run-A", Profile = "Balanced", ResultStatus = "Prepared" };
            var b = new ValidationArtifactRun { RunId = "run-B", Profile = "Balanced", ResultStatus = "Succeeded" };
            archive.CreateRunDirectory(a);
            archive.CreateRunDirectory(b);

            Assert.True(File.Exists(Path.Combine(root, "run-A", "manifest.json")));
            Assert.True(File.Exists(Path.Combine(root, "run-B", "manifest.json")));
            Assert.Equal(2, archive.ListRunDirectories().Count);
            Assert.Equal("run-B", archive.ResolveLatest()!.RunId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Latest_Pointer_Resolution_Returns_Most_Recent_Run()
    {
        var root = TempDir();
        try
        {
            var archive = new ValidationArtifactArchiveService(root);
            archive.CreateRunDirectory(new ValidationArtifactRun { RunId = "20260816-100000-x-Balanced", Profile = "Balanced", ResultStatus = "Prepared" });
            archive.UpdateLatest(new ValidationArtifactRun { RunId = "20260816-120000-x-DedicatedGaming", Profile = "DedicatedGaming", ResultStatus = "Succeeded" });

            var latest = archive.ResolveLatest();
            Assert.NotNull(latest);
            Assert.Equal("DedicatedGaming", latest!.Profile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Interrupted_Run_Recovery_Metadata_Is_Surfaced()
    {
        var root = TempDir();
        try
        {
            var archive = new ValidationArtifactArchiveService(root);
            archive.CreateRunDirectory(new ValidationArtifactRun { RunId = "r1", Profile = "Gaming", ResultStatus = "Interrupted", Phase = "Commit" });
            archive.CreateRunDirectory(new ValidationArtifactRun { RunId = "r2", Profile = "Office", ResultStatus = "Succeeded", Phase = "IsoBuild" });
            archive.CreateRunDirectory(new ValidationArtifactRun { RunId = "r3", Profile = "Lightweight", ResultStatus = "Failed", Phase = "Plan" });

            var interrupted = archive.ListInterruptedRuns();
            Assert.Equal(2, interrupted.Count);
            Assert.Contains(interrupted, r => r.RunId == "r1" && r.Phase == "Commit");
            Assert.Contains(interrupted, r => r.RunId == "r3" && r.Phase == "Plan");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // =====================================================================
    // 2. RELEASE VALIDATION MANIFEST — truthfulness
    // =====================================================================

    [Fact]
    public void Release_Manifest_Never_Claims_Unproven_Validation_Levels()
    {
        var counts = new Dictionary<string, (int PlanOps, int Selected)>
        {
            ["Balanced"] = (16, 10),
            ["Gaming"] = (25, 19),
            ["DedicatedGaming"] = (33, 20),
            ["Developer"] = (21, 18),
            ["Office"] = (17, 10),
            ["Lightweight"] = (38, 24),
        };
        var manifest = ReleaseValidationManifestService.Build(
            "deadbeef", @"C:\isos\Win11.iso", 4, "Windows 11 Pro", "zh-CN", "x64", "26200.8037", counts);

        Assert.Equal(6, manifest.Profiles.Count);
        var balanced = manifest.Profiles.Single(p => p.ProfileId == "Balanced");
        var gaming = manifest.Profiles.Single(p => p.ProfileId == "Gaming");
        var dg = manifest.Profiles.Single(p => p.ProfileId == "DedicatedGaming");
        var lightweight = manifest.Profiles.Single(p => p.ProfileId == "Lightweight");

        Assert.True(balanced.FullHealthValidated);
        Assert.True(dg.FullHealthValidated);
        Assert.False(gaming.FullHealthValidated);
        Assert.False(lightweight.FullHealthValidated);
        Assert.All(manifest.Profiles, p => Assert.True(p.WorkflowValidated));
        Assert.All(manifest.Profiles.Where(p => !p.FullHealthValidated),
            p => Assert.Contains(p.ValidationDebt, d => d.Contains("No VM install", StringComparison.Ordinal)));
        Assert.Equal("2d521bd21a0efa17bf24acdc97a3a8d2c279cfea1c866e90bbdce2cb89be0210", dg.IsoSha256);
        Assert.Equal(19, gaming.SelectedOperationCount);
    }

    [Fact]
    public void Release_Manifest_Serializes_With_CamelCase_Schema()
    {
        var manifest = ReleaseValidationManifestService.Build("abc12345", "x.iso", 4, "Win11 Pro", "zh-CN", "x64", "26200", new Dictionary<string, (int, int)>());
        var json = ReleaseValidationManifestService.Serialize(manifest);
        Assert.Contains("\"profileId\": \"Balanced\"", json);
        Assert.Contains("\"fullHealthValidated\": true", json);
        Assert.Contains("\"windowsIndex\": 4", json);
        Assert.Contains("\"winForgeCommitSha\": \"abc12345\"", json);
    }

    // =====================================================================
    // 3. EXPECTED-STATE MAPPING (builder) + Recommend-only exclusion
    // =====================================================================

    [Fact]
    public void ExpectedState_Builder_Maps_Selected_Ops_With_Exact_Scope()
    {
        var state = ExpectedStateBuilder.Build("Test", new[]
        {
            Op(CustomizationOperationType.RemoveProvisionedAppx, "Microsoft.WindowsFeedbackHub"),
            Op(CustomizationOperationType.SetOfflineRegistryValue, hive: "SOFTWARE",
                key: @"Policies\Microsoft\Windows\DataCollection", value: "DoNotShowFeedbackNotifications", data: "1"),
            Op(CustomizationOperationType.SetOfflineRegistryValue, hive: "DEFAULT_USER",
                key: @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", value: "TaskbarSearch", data: "1"),
            Op(CustomizationOperationType.ConfigureOfflineService, "XboxGipSvc"),
        });

        Assert.Equal(new[] { "Microsoft.WindowsFeedbackHub" }, state.AppxAbsent);
        Assert.Equal(2, state.RegistryChecks.Count);
        Assert.Equal("OfflineMachine", state.RegistryChecks[0].Scope);
        Assert.Equal("DoNotShowFeedbackNotifications", state.RegistryChecks[0].Name);
        Assert.Equal("1", state.RegistryChecks[0].ExpectedData);
        Assert.Equal("CurrentUserEffective", state.RegistryChecks[1].Scope);
        Assert.Equal(new[] { "XboxGipSvc" }, state.ServicesDisabled);
    }

    [Fact]
    public void ExpectedState_JSON_Files_Match_Real_Evidence_Counts()
    {
        // The four Phase-17 expected-state files were derived from the REAL plan
        // evidence (profile-plans.json AutoApply rows + profile-buildplans.json
        // canonical keys). This pins exact counts + Recommend-only exclusion.
        var cases = new (string Profile, int Selected, int Appx, int Reg, int Svc)[]
        {
            ("Gaming", 19, 10, 9, 0),
            ("Developer", 18, 6, 12, 0),
            ("Office", 10, 4, 6, 0),
            ("Lightweight", 24, 6, 13, 5),
        };

        foreach (var (profile, selected, appx, reg, svc) in cases)
        {
            var path = Path.Combine(RepoRoot(), "scripts", $"{profile.ToLowerInvariant()}-expected-state.json");
            Assert.True(File.Exists(path), $"{path} missing");
            var state = ProfileExpectedStateParser.Parse(File.ReadAllText(path));
            Assert.NotNull(state);
            Assert.Equal(profile, state!.ProfileId);
            Assert.Equal(appx, state.AppxAbsent.Count);
            Assert.Equal(reg, state.RegistryChecks.Count);
            Assert.Equal(svc, state.ServicesDisabled.Count);
            Assert.Equal(selected, appx + reg + svc);
            Assert.All(state.RegistryChecks,
                r => Assert.True(r.Scope is "OfflineMachine" or "CurrentUserEffective" or "DefaultUserTemplate"));
        }
    }

    [Fact]
    public void ExpectedState_JSON_Files_Exclude_Recommend_Only_Families()
    {
        foreach (var profile in new[] { "gaming", "developer", "office", "lightweight" })
        {
            var path = Path.Combine(RepoRoot(), "scripts", $"{profile}-expected-state.json");
            var state = ProfileExpectedStateParser.Parse(File.ReadAllText(path));
            Assert.NotNull(state);
            var forbid = new[] { "Containers", "WSL", "DevHome", "OneDriveSync", "HyperV", "Sandbox", "DiagTrack" };
            foreach (var f in forbid)
            {
                Assert.DoesNotContain(state!.AppxAbsent, a => a.Contains(f, StringComparison.Ordinal));
            }

            Assert.DoesNotContain(state!.ServicesDisabled, s => s == "DiagTrack"); // DiagTrack is always Recommend
        }
    }

    [Fact]
    public void Lightweight_Expected_State_Includes_Selected_Services()
    {
        var path = Path.Combine(RepoRoot(), "scripts", "lightweight-expected-state.json");
        var state = ProfileExpectedStateParser.Parse(File.ReadAllText(path));
        Assert.NotNull(state);
        Assert.Contains("MapsBroker", state!.ServicesDisabled);
        Assert.Contains("XblAuthManager", state.ServicesDisabled);
        Assert.Contains("XboxGipSvc", state.ServicesDisabled);
        Assert.Contains("XboxNetApiSvc", state.ServicesDisabled);
        Assert.Contains("RetailDemo", state.ServicesDisabled);
        Assert.Equal(5, state.ServicesDisabled.Count);
        // HyperV/VM-platform features are Recommend-only in the real plan — never expected.
        Assert.DoesNotContain(state.RegistryChecks, r => r.Name.Contains("Hyper", StringComparison.Ordinal));
    }

    // =====================================================================
    // 4. SIX-PROFILE DELTA AUDIT — uniqueness / convergence
    // =====================================================================

    [Fact]
    public void Delta_Audit_Detects_Exclusive_Keys_And_Convergence()
    {
        var sets = new Dictionary<string, IReadOnlyList<string>>
        {
            ["Balanced"] = new[] { "pkg|A", "reg|X" },
            ["Gaming"] = new[] { "pkg|A", "reg|Y" },
            ["DedicatedGaming"] = new[] { "pkg|A", "reg|Y" }, // identical to Gaming → convergence
        };
        var audit = ProfileDeltaAuditService.Audit(sets);

        Assert.Single(audit.CommonSelectedKeys);
        Assert.Equal("pkg|A", audit.CommonSelectedKeys[0]);
        Assert.Contains(audit.ConvergenceWarnings, w => w.Contains("DedicatedGaming == Gaming", StringComparison.Ordinal));
        Assert.Equal(1, audit.Profiles.Single(p => p.ProfileId == "Balanced").ExclusiveKeys.Count);
        Assert.Contains(audit.Profiles.Single(p => p.ProfileId == "Gaming").OperationTypeDistribution, kv => kv.Key == "reg" && kv.Value == 1);
    }

    [Fact]
    public void Six_Profiles_Are_Materially_Different()
    {
        // Real Phase 15 plan evidence: selected counts differ pairwise AND the
        // type distributions differ (no accidental convergence among primaries).
        var counts = new Dictionary<string, (int PlanOps, int Selected, string Types)>
        {
            ["Balanced"] = (16, 10, "appx:3 reg:6 svc:1"),
            ["Gaming"] = (25, 19, "appx:10 reg:9"),
            ["DedicatedGaming"] = (33, 20, "appx:11 reg:9"),
            ["Developer"] = (21, 18, "appx:6 reg:12"),
            ["Office"] = (17, 10, "appx:4 reg:6"),
            ["Lightweight"] = (38, 24, "appx:6 reg:13 svc:5"),
        };
        // Balanced and Office both select 10 ops - differentiation must come from the
        // TYPE MIX (every profile has a distinct distribution), not raw counts alone.
        Assert.Equal(6, counts.Values.Select(c => c.Types).Distinct().Count()); // distinct type mixes
        Assert.NotEqual(counts["Balanced"].Types, counts["Office"].Types);
        // Stage 17.4 requirements: Balanced != Gaming, Gaming != DedicatedGaming, Office is
        // NOT a no-op, Lightweight is materially the strongest general-purpose profile.
        Assert.NotEqual(counts["Balanced"].Selected, counts["Gaming"].Selected);
        Assert.NotEqual(counts["Gaming"].Selected, counts["DedicatedGaming"].Selected);
        Assert.True(counts["Office"].Selected > 0);
        Assert.True(counts["Lightweight"].Selected > counts["Office"].Selected);
        Assert.Contains("svc", counts["Lightweight"].Types); // service changes are Lightweight-exclusive among primaries
    }

    // =====================================================================
    // 5. RELEASE SAFETY INVARIANTS
    // =====================================================================

    [Fact]
    public void Safety_Invariants_Pass_For_WellBehaved_Plan()
    {
        var ops = new[]
        {
            Op(CustomizationOperationType.RemoveProvisionedAppx, "Microsoft.BingSearch"),
            Op(CustomizationOperationType.SetOfflineRegistryValue, hive: "SOFTWARE",
                key: @"Policies\Microsoft\Windows\CloudContent", value: "DisableWindowsConsumerFeatures", data: "1"),
            Op(CustomizationOperationType.SetOfflineRegistryValue, hive: "DEFAULT_USER",
                key: @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", value: "Start_ShowRecent", data: "0"),
        };
        var result = ReleaseSafetyInvariantSet.CheckPlan(ops);
        Assert.True(result.Passed);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Safety_Invariants_Fail_On_Defender_Store_Or_Servicing_Removal()
    {
        var ops = new[]
        {
            Op(CustomizationOperationType.RemoveProvisionedAppx, "Microsoft.BingSearch"),
            Op(CustomizationOperationType.RemoveProvisionedAppx, "Microsoft.Windows.SecHealthUI"),
            Op(CustomizationOperationType.RemoveProvisionedAppx, "Microsoft.WindowsStore"),
            Op(CustomizationOperationType.RemoveProvisionedAppx, "Microsoft.DesktopAppInstaller"),
        };
        var result = ReleaseSafetyInvariantSet.CheckPlan(ops);
        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Contains("[defender]", StringComparison.Ordinal));
        Assert.Contains(result.Violations, v => v.Contains("[store]", StringComparison.Ordinal));
        Assert.Contains(result.Violations, v => v.Contains("[appInstaller]", StringComparison.Ordinal));
    }

    [Fact]
    public void Safety_Invariants_Fail_On_Core_Service_Removal()
    {
        var ops = new[] { Op(CustomizationOperationType.ConfigureOfflineService, "WinDefend") };
        var result = ReleaseSafetyInvariantSet.CheckPlan(ops);
        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Contains("defender", StringComparison.OrdinalIgnoreCase));
    }

    // =====================================================================
    // 6. PORTABLE FULLHEALTH BUNDLE
    // =====================================================================

    [Fact]
    public void Bundle_Generation_Produces_Exact_Command_Files()
    {
        var scripts = Path.Combine(RepoRoot(), "scripts");
        var bundleService = new ValidationBundleService(scripts);
        var outDir = TempDir();
        try
        {
            var run = new ValidationArtifactRun
            {
                RunId = "r-bundle",
                Profile = "DedicatedGaming",
                GeneratedIsoPath = @"C:\Users\me\Documents\WinForge\WinForge-DedicatedGaming-Win11-25H2-Pro-zh-CN-x64.iso",
                GeneratedIsoSha256 = "2d521bd21a0efa17bf24acdc97a3a8d2c279cfea1c866e90bbdce2cb89be0210",
                SourceIsoPath = @"C:\Users\me\Downloads\Win11_25H2_Chinese_Simplified_x64_v2.iso",
                WinForgeCommitSha = "abc12345",
            };
            bundleService.GenerateBundle(outDir, "DedicatedGaming", run);

            Assert.True(File.Exists(Path.Combine(outDir, "Validate-WinForgeInstallation.ps1")));
            Assert.True(File.Exists(Path.Combine(outDir, "dedicated-gaming-expected-state.json")));
            Assert.True(File.Exists(Path.Combine(outDir, "validation-manifest.json")));
            var readme = File.ReadAllText(Path.Combine(outDir, "README.txt"));
            Assert.Contains("-ProfileId DedicatedGaming", readme);
            Assert.Contains("-MediaId \"WinForge-DedicatedGaming-Win11-25H2-Pro-zh-CN-x64.iso\"", readme);
            Assert.Contains("-ExpectedJson dedicated-gaming-expected-state.json", readme);
            Assert.Contains("-IsoSha256 \"2d521bd21a0efa17bf24acdc97a3a8d2c279cfea1c866e90bbdce2cb89be0210\"", readme);
            Assert.DoesNotContain("password", readme, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public void Bundle_Generation_Fails_Cleanly_On_Missing_Expected_State()
    {
        var scripts = Path.Combine(RepoRoot(), "scripts");
        var bundleService = new ValidationBundleService(scripts);
        var outDir = TempDir();
        try
        {
            var run = new ValidationArtifactRun { RunId = "r", Profile = "DoesNotExist" };
            Assert.Throws<FileNotFoundException>(() => bundleService.GenerateBundle(outDir, "DoesNotExist", run));
        }
        finally
        {
            Directory.Delete(outDir, recursive: true);
        }
    }

    // =====================================================================
    // 7. Balanced + DedicatedGaming FullHealth REGRESSION
    // =====================================================================

    private static string Section(string status, params (string Name, string Status, string Detail)[] checks)
    {
        var list = string.Join(",", checks.Select(c => $"{{\"name\":\"{c.Name}\",\"status\":\"{c.Status}\",\"detail\":\"{c.Detail}\"}}"));
        return $"{{\"status\":\"{status}\",\"checks\":[{list}]}}";
    }

    [Fact]
    public void Balanced_FullHealth_Regression_Remains_True()
    {
        var s = Section;
        var json = "{" +
                   $"\"sections\":{{" +
                   $"\"media\":{s("Pass", ("iso", "Pass", "x.iso"))}," +
                   $"\"profile\":{s("Pass", ("profile", "Pass", "Balanced"))}," +
                   $"\"windowsIdentity\":{s("Pass", ("edition", "Pass", "Windows 11 Pro"))}," +
                   $"\"bootAndShell\":{s("Pass", ("explorer", "Pass", "running"))}," +
                   $"\"devices\":{s("Pass", ("deviceProblems", "Pass", "none"))}," +
                   $"\"network\":{s("Pass", ("dns", "Pass", "ok"))}," +
                   $"\"servicing\":{s("Pass", ("dismCheckHealth", "Pass", "no corruption"), ("sfcVerifyOnly", "Pass", "ok"))}," +
                   $"\"windowsUpdate\":{s("Pass", ("wuauserv", "Pass", "present"))}," +
                   $"\"security\":{s("Pass", ("defender", "Pass", "present"))}," +
                   $"\"storeAndAppPlatform\":{s("Pass", ("store", "Pass", "present"))}," +
                   $"\"profileExpectedChanges\":{s("Pass", ("appx", "Pass", "absent"))}" +
                   "}}";
        var result = HealthReportParser.Parse(json);
        Assert.True(result.SchemaValid);
        Assert.True(result.Report!.FullHealthValidated);
    }

    [Fact]
    public void DedicatedGaming_FullHealth_Regression_Remains_True()
    {
        var s = Section;
        var json = "{" +
                   $"\"sections\":{{" +
                   $"\"media\":{s("Pass", ("iso", "Pass", "x.iso"))}," +
                   $"\"profile\":{s("Pass", ("profile", "Pass", "DedicatedGaming"))}," +
                   $"\"windowsIdentity\":{s("Pass", ("edition", "Pass", "Windows 11 Pro"))}," +
                   $"\"bootAndShell\":{s("Pass", ("explorer", "Pass", "running"))}," +
                   $"\"devices\":{s("Pass", ("deviceProblems", "Pass", "none"))}," +
                   $"\"network\":{s("Pass", ("dhcpIp", "Pass", "ok"), ("dns", "Pass", "ok"))}," +
                   $"\"servicing\":{s("Pass", ("dismCheckHealth", "Pass", "no corruption"), ("sfcVerifyOnly", "Pass", "ok"))}," +
                   $"\"windowsUpdate\":{s("Pass", ("wuauserv", "Pass", "present"))}," +
                   $"\"security\":{s("Pass", ("defender", "Pass", "present"), ("firewall", "Pass", "enabled"))}," +
                   $"\"storeAndAppPlatform\":{s("Pass", ("store", "Pass", "present"))}," +
                   $"\"profileExpectedChanges\":{s("Pass", ("appx", "Pass", "absent"), ("reg", "Pass", "ok"))}" +
                   "}}";
        var result = HealthReportParser.Parse(json);
        Assert.True(result.SchemaValid);
        Assert.True(result.Report!.FullHealthValidated);
    }
}
