using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WinForge.Core.Health;
using WinForge.Infrastructure.Health;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Stage 16.2 — DedicatedGaming full-health validation prep.
///
/// The DedicatedGaming expected-state file is built ONLY from the actual
/// AutoApply/selected operations of the real Phase 15 apply validation
/// (profile-apply-validation.json: BuildPlan 33, Selected 20, Attempted 20,
/// Succeeded 20, all read-back Verified). Recommend-only candidates
/// (Containers, WSL, DevHome, OneDriveSync, …) were NOT executed and must NOT
/// appear in the expected post-install state.
/// </summary>
public class Stage16bDedicatedGamingTests
{
    private const string ProfileId = "DedicatedGaming";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinForge.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private static string ExpectedStatePath() => Path.Combine(RepoRoot(), "scripts", "dedicated-gaming-expected-state.json");

    private static string ApplyEvidencePath() => Path.Combine(RepoRoot(), ".tmp", "phase14-real", "profile-apply-validation.json");

    private static ProfileExpectedState LoadExpectedState()
    {
        var state = ProfileExpectedStateParser.Parse(File.ReadAllText(ExpectedStatePath()));
        Assert.NotNull(state);
        return state!;
    }

    private static JsonElement LoadApplyEvidence()
    {
        Assert.True(File.Exists(ApplyEvidencePath()), $"Real apply evidence not found: {ApplyEvidencePath()}");
        using var doc = JsonDocument.Parse(File.ReadAllText(ApplyEvidencePath()));
        return doc.RootElement.Clone();
    }

    // =====================================================================
    // 1. EXPECTED-STATE SCHEMA (exact counts from the real 20-op plan)
    // =====================================================================

    [Fact]
    public void DedicatedGaming_Expected_State_JSON_Matches_Schema()
    {
        var state = LoadExpectedState();
        Assert.Equal(ProfileId, state.ProfileId);

        // 11 provisioned AppX removals from the real selected plan.
        Assert.Equal(11, state.AppxAbsent.Count);

        // 9 registry expectations: 5 machine (OfflineMachine) + 4 Default-User
        // seeded settings verified as CurrentUserEffective after OOBE.
        Assert.Equal(9, state.RegistryChecks.Count);
        Assert.Equal(5, state.RegistryChecks.Count(r => r.Scope == "OfflineMachine"));
        Assert.Equal(4, state.RegistryChecks.Count(r => r.Scope == "CurrentUserEffective"));
        Assert.Equal(0, state.RegistryChecks.Count(r => r.Scope == "DefaultUserTemplate"));

        Assert.All(state.AppxAbsent, a => Assert.False(string.IsNullOrWhiteSpace(a)));
        Assert.All(state.RegistryChecks, r => Assert.False(string.IsNullOrWhiteSpace(r.Path)));
        Assert.All(state.RegistryChecks, r => Assert.False(string.IsNullOrWhiteSpace(r.Name)));
        Assert.All(state.RegistryChecks, r => Assert.False(string.IsNullOrWhiteSpace(r.ExpectedData)));
        Assert.All(state.RegistryChecks, r => Assert.True(r.Scope is "OfflineMachine" or "CurrentUserEffective" or "DefaultUserTemplate"));
    }

    [Fact]
    public void DedicatedGaming_Expected_State_Matches_Real_Selected_Plan_Evidence()
    {
        // Cross-check the expected state against the committed real Phase 15
        // apply evidence — the two must agree exactly (20 selected ops).
        var state = LoadExpectedState();
        var evidence = LoadApplyEvidence();

        Assert.Equal(ProfileId, evidence.GetProperty("profileId").GetString());
        Assert.Equal(20, evidence.GetProperty("selectedOperationCount").GetInt32());
        Assert.Equal(20, evidence.GetProperty("attempted").GetInt32());
        Assert.Equal(0, evidence.GetProperty("failed").GetInt32());
        Assert.True(evidence.GetProperty("validationPassed").GetBoolean());

        var ops = evidence.GetProperty("operations").EnumerateArray().ToList();
        var appxOps = ops.Where(o => o.GetProperty("operationType").GetString() == "RemoveProvisionedAppx").ToList();
        var regOps = ops.Where(o => o.GetProperty("operationType").GetString() == "SetOfflineRegistryValue").ToList();

        // 11 AppX removals + 9 registry writes = the entire selected plan.
        Assert.Equal(11, appxOps.Count);
        Assert.Equal(9, regOps.Count);
        Assert.Equal(20, appxOps.Count + regOps.Count);

        // Every expected AppX absence maps 1:1 to a pkg|<family>_ canonical key.
        var canonicalAppx = appxOps.Select(o => o.GetProperty("canonicalKey").GetString()).ToList();
        foreach (var family in state.AppxAbsent)
        {
            Assert.Contains(canonicalAppx, k => k!.StartsWith($"pkg|{family}_", StringComparison.Ordinal));
        }

        // Every registry expectation maps 1:1 to a reg|<scopeHive>|… canonical key.
        var canonicalReg = regOps.Select(o => o.GetProperty("canonicalKey").GetString()).ToList();
        foreach (var r in state.RegistryChecks)
        {
            var hiveMarker = r.Scope == "OfflineMachine" ? "OfflineMachine" : "OfflineDefaultUser";
            Assert.Contains(canonicalReg,
                k => k!.StartsWith($"reg|{hiveMarker}|", StringComparison.Ordinal)
                     && k.EndsWith($"|{r.Name}", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void DedicatedGaming_RecommendOnly_Families_Are_Excluded()
    {
        // Containers / WSL / DevHome / OneDriveSync were Recommend-only in the
        // real plan and were NEVER executed — they must not be expected states.
        var state = LoadExpectedState();
        var forbid = new[] { "Containers", "WSL", "DevHome", "OneDrive", "HyperV", "Sandbox" };

        foreach (var f in forbid)
        {
            Assert.DoesNotContain(state.AppxAbsent, a => a.Contains(f, StringComparison.Ordinal));
        }

        var joined = string.Join(" ", state.RegistryChecks.Select(r => $"{r.Path}\\{r.Name}"));
        Assert.False(
            forbid.Any(f => joined.Contains(f, StringComparison.Ordinal)),
            $"Recommend-only family leaked into DedicatedGaming expected registry state: {joined}");
    }

    [Fact]
    public void DedicatedGaming_AppX_Expected_Absent_Matches_Canonical_Plan()
    {
        // The 11 expected removals are exactly the real BingSearch/BingWeather/
        // Clipchamp/FeedbackHub/GetHelp/OfficeHub/BingNews/OutlookForWindows/
        // YourPhone/Solitaire/WebExperience set.
        var state = LoadExpectedState();
        var expected = new[]
        {
            "Microsoft.BingSearch",
            "Microsoft.BingWeather",
            "Clipchamp.Clipchamp",
            "Microsoft.WindowsFeedbackHub",
            "Microsoft.GetHelp",
            "Microsoft.MicrosoftOfficeHub",
            "Microsoft.BingNews",
            "Microsoft.OutlookForWindows",
            "Microsoft.YourPhone",
            "Microsoft.MicrosoftSolitaireCollection",
            "MicrosoftWindows.Client.WebExperience",
        };

        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), state.AppxAbsent.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void DedicatedGaming_CurrentUserEffective_Values_And_Data()
    {
        // The 4 Default-User-seeded settings are verified in the effective
        // current-user hive after OOBE, with the exact data values from the
        // optimization catalog (TaskbarSearch = 1 = search icon only).
        var state = LoadExpectedState();
        var effective = state.RegistryChecks.Where(r => r.Scope == "CurrentUserEffective").ToList();
        Assert.Equal(4, effective.Count);

        var recent = effective.Single(r => r.Name == "Start_ShowRecent");
        Assert.Equal("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", recent.Path);
        Assert.Equal("0", recent.ExpectedData);

        var recommended = effective.Single(r => r.Name == "Start_ShowRecommended");
        Assert.Equal("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", recommended.Path);
        Assert.Equal("0", recommended.ExpectedData);

        var webContent = effective.Single(r => r.Name == "EnableWebContent");
        Assert.Equal("Software\\Policies\\Microsoft\\Dsh", webContent.Path);
        Assert.Equal("0", webContent.ExpectedData);

        var taskbarSearch = effective.Single(r => r.Name == "TaskbarSearch");
        Assert.Equal("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", taskbarSearch.Path);
        Assert.Equal("1", taskbarSearch.ExpectedData); // search icon only (hide search box)
    }

    [Fact]
    public void DedicatedGaming_Machine_Policies_Stay_OfflineMachine()
    {
        // The 5 machine writes are policy/telemetry targets (HKLM after install)
        // and must keep the OfflineMachine scope — never reinterpreted as HKCU.
        var state = LoadExpectedState();
        var machine = state.RegistryChecks.Where(r => r.Scope == "OfflineMachine").ToList();
        Assert.Equal(5, machine.Count);

        Assert.Contains(machine, r => r.Name == "DisableSoftLanding" && r.ExpectedData == "1");
        Assert.Contains(machine, r => r.Name == "Enabled" && r.Path.EndsWith("AdvertisingInfo", StringComparison.Ordinal) && r.ExpectedData == "0");
        Assert.Contains(machine, r => r.Name == "DoNotShowFeedbackNotifications" && r.ExpectedData == "1");
        Assert.Contains(machine, r => r.Name == "DisableWindowsSpotlightFeatures" && r.ExpectedData == "1");
        Assert.Contains(machine, r => r.Name == "DisableWindowsConsumerFeatures" && r.ExpectedData == "1");
    }

    // =====================================================================
    // 2. PROFILE-SPECIFIC FULL-HEALTH GATING + PLATFORM PRESERVATION
    // =====================================================================

    private static string Section(string status, params (string Name, string Status, string Detail)[] checks)
    {
        static string J(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var list = string.Join(",", checks.Select(c => $"{{\"name\":\"{J(c.Name)}\",\"status\":\"{J(c.Status)}\",\"detail\":\"{J(c.Detail)}\"}}"));
        return $"{{\"status\":\"{J(status)}\",\"checks\":[{list}]}}";
    }

    /// <summary>
    /// A full DedicatedGaming health report where every expected-state check
    /// (11 AppX + 9 registry) passes and the preserved-platform sections
    /// (security, windowsUpdate, storeAndAppPlatform, devices, servicing,
    /// network fundamentals) all Pass — the genuine post-install success shape.
    /// </summary>
    private static string DedicatedGamingPassReport()
    {
        var s = Section;
        var state = LoadExpectedState();

        // profileExpectedChanges: one Pass check per AppX family + registry check.
        var peChecks = state.AppxAbsent.Select(a => ("appxAbsent_" + a, "Pass", "absent")).ToList();
        peChecks.AddRange(state.RegistryChecks.Select(r => ("reg_" + r.Name, "Pass", $"{r.Scope} {r.Path}\\{r.Name} = {r.ExpectedData}")));

        return "{" +
               $"\"sections\":{{" +
               $"\"media\":{s("Pass", ("iso", "Pass", "WinForge-DedicatedGaming...iso"))}," +
               $"\"profile\":{s("Pass", ("profile", "Pass", ProfileId))}," +
               $"\"windowsIdentity\":{s("Pass", ("edition", "Pass", "Windows 11 Pro"))}," +
               $"\"bootAndShell\":{s("Pass", ("explorer", "Pass", "running"))}," +
               $"\"devices\":{s("Pass", ("deviceProblems", "Pass", "none"))}," +
               $"\"network\":{s("Pass", ("dhcpIp", "Pass", "ok"), ("dns", "Pass", "ok"))}," +
               $"\"servicing\":{s("Pass", ("dismCheckHealth", "Pass", "no corruption"), ("sfcVerifyOnly", "Pass", "ok"))}," +
               $"\"windowsUpdate\":{s("Pass", ("wuauserv", "Pass", "present"))}," +
               $"\"security\":{s("Pass", ("defender", "Pass", "present"), ("firewall", "Pass", "enabled"))}," +
               $"\"storeAndAppPlatform\":{s("Pass", ("store", "Pass", "present"))}," +
               $"\"profileExpectedChanges\":{s("Pass", peChecks.ToArray())}" +
               "}}";
    }

    [Fact]
    public void DedicatedGaming_Platform_Preservation_Report_Is_FullHealth()
    {
        // DedicatedGaming is NOT a kiosk profile: Defender, firewall, Windows
        // Update, Store, servicing, network, devices all remain healthy and the
        // profile-specific expected-state checks all pass.
        var result = HealthReportParser.Parse(DedicatedGamingPassReport());
        Assert.True(result.SchemaValid);
        Assert.Empty(result.Errors);
        Assert.Equal(HealthStatus.Pass, result.Report!.OverallStatus);
        Assert.True(result.Report.FullHealthValidated);
        Assert.Empty(result.Report.Failures);
    }

    [Fact]
    public void DedicatedGaming_Required_Check_Fail_Blocks_FullHealth()
    {
        // A genuine required servicing failure always blocks, even with the
        // DedicatedGaming expected-state checks passing.
        var json = DedicatedGamingPassReport().Replace(
            "{\"name\":\"dismCheckHealth\",\"status\":\"Pass\"",
            "{\"name\":\"dismCheckHealth\",\"status\":\"Fail\"", StringComparison.Ordinal);
        var result = HealthReportParser.Parse(json);
        Assert.True(result.SchemaValid);
        Assert.Equal(HealthStatus.Fail, result.Report!.OverallStatus);
        Assert.False(result.Report.FullHealthValidated);
    }

    [Fact]
    public void DedicatedGaming_Optional_Warning_Does_Not_Block()
    {
        // Activation is informational/report-only; a Warning there must not
        // block FullHealthValidated (ADR-098 required-vs-optional rules).
        var json = DedicatedGamingPassReport().Replace(
            "{\"status\":\"Pass\",\"checks\":[{\"name\":\"edition\",\"status\":\"Pass\",\"detail\":\"Windows 11 Pro\"}]}",
            "{\"status\":\"Pass\",\"checks\":[{\"name\":\"edition\",\"status\":\"Pass\",\"detail\":\"Windows 11 Pro\"},{\"name\":\"activation\",\"status\":\"Warning\",\"detail\":\"Notification (report only)\",\"requiredForFullHealth\":false}]}",
            StringComparison.Ordinal);
        var result = HealthReportParser.Parse(json);
        Assert.True(result.SchemaValid);
        Assert.Equal(HealthStatus.Warning, result.Report!.OverallStatus);
        Assert.True(result.Report.FullHealthValidated);
        Assert.Contains(result.Report.Warnings, w => w.Contains("activation", StringComparison.Ordinal));
    }

    [Fact]
    public void Balanced_FullHealth_Regression_Remains_True()
    {
        // Balanced already passed ADR-084 FullHealthValidated on the real VM;
        // its expected-state file and a passing report must still evaluate true
        // after the 16.2 changes (no regression from the new profile prep).
        var balancedPath = Path.Combine(RepoRoot(), "scripts", "balanced-expected-state.json");
        Assert.True(File.Exists(balancedPath));
        var balanced = ProfileExpectedStateParser.Parse(File.ReadAllText(balancedPath));
        Assert.NotNull(balanced);
        Assert.Equal("Balanced", balanced!.ProfileId);
        Assert.Equal(3, balanced.AppxAbsent.Count);
        Assert.Equal(6, balanced.RegistryChecks.Count);

        var result = HealthReportParser.Parse(BalancedPassReport());
        Assert.True(result.SchemaValid);
        Assert.True(result.Report!.FullHealthValidated);
    }

    private static string BalancedPassReport()
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
               $"\"servicing\":{s("Pass", ("dismCheckHealth", "Pass", "no corruption"), ("sfcVerifyOnly", "Pass", "ok"))}," +
               $"\"windowsUpdate\":{s("Pass", ("wuauserv", "Pass", "present"))}," +
               $"\"security\":{s("Pass", ("defender", "Pass", "present"))}," +
               $"\"storeAndAppPlatform\":{s("Pass", ("store", "Pass", "present"))}," +
               $"\"profileExpectedChanges\":{s("Pass", ("appx", "Pass", "absent"))}" +
               "}}";
    }
}
