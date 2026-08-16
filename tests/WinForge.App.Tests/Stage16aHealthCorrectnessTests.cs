using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using WinForge.Core.Health;
using WinForge.Infrastructure.Health;
using Xunit;

namespace WinForge.App.Tests;

// =====================================================================
// Phase 16 Stage 16.1a — HEALTH-CHECK CORRECTNESS (ADR-098 addendum)
//
// Tests for the two real-VM correctness defects plus the encoding/scope
// guards:
//   1. SFC /verifyonly verdicts are EXIT-CODE authoritative and
//      locale-agnostic; native output (UTF-16LE with a LOW NUL ratio, e.g.
//      Chinese text) decodes correctly; NUL-corrupted captures never fail a
//      successful run; genuine failures still Fail.
//   2. Post-install registry expectations declare an EXPLICIT scope:
//      OfflineMachine (HKLM) vs CurrentUserEffective (HKCU, for profile-
//      seeding values consumed by OOBE) vs DefaultUserTemplate — a missing
//      or unknown scope rejects the expected-state file.
//   3. Unicode: the report round-trips Chinese text without mojibake, and the
//      in-VM script file is pure-ASCII (plus a UTF-8 BOM) so PowerShell 5.1
//      ANSI parsing can never mangle its string literals.
// =====================================================================

public sealed class Stage16aHealthCorrectnessTests
{
    // ---- 1. SFC verdicts (exit-code authoritative, locale-agnostic) ----

    [Fact]
    public void Sfc_ExitZero_With_Chinese_Success_Text_Passes()
    {
        // Real manual run on the zh-CN VM: exit 0 + "Windows 资源保护未找到任何完整性冲突。"
        var verdict = SfcVerifyOnlyEvaluator.Evaluate(0, "验证 100% 已完成。\r\nWindows 资源保护未找到任何完整性冲突。");
        Assert.True(verdict.Pass);
        Assert.Contains("未找到任何完整性冲突", verdict.Detail);
    }

    [Fact]
    public void Sfc_ExitZero_With_English_Success_Text_Passes()
    {
        var verdict = SfcVerifyOnlyEvaluator.Evaluate(0, "Verification 100% complete.\r\nWindows Resource Protection did not find any integrity violations.");
        Assert.True(verdict.Pass);
    }

    [Fact]
    public void Sfc_NulCorrupted_Or_Garbage_Capture_With_ExitZero_Still_Passes()
    {
        // The original defect: exit 0 but the captured output was NUL-corrupted
        // ("\0; \0; \0"). The exit code is authoritative — a capture artifact
        // must never turn a successful run into a failure.
        var verdict = SfcVerifyOnlyEvaluator.Evaluate(0, "\u0000; \u0000; \u0000; \u0000");
        Assert.True(verdict.Pass);
        Assert.Equal(-1, verdict.Detail.IndexOf('\u0000')); // detail is sanitized - no NUL
    }

    [Fact]
    public void Sfc_Genuine_Failure_Remains_Fail()
    {
        var verdict = SfcVerifyOnlyEvaluator.Evaluate(1, "Windows Resource Protection found corrupt files and was unable to fix some of them.");
        Assert.False(verdict.Pass);
        Assert.Contains("FAILED", verdict.Detail);
    }

    [Fact]
    public void Sfc_Localized_NonZero_Exit_Remains_Fail()
    {
        // Elevation failure on zh-CN: exit 1 + "为了使用 sfc 工具，你必须作为管理员运行控制台会话。"
        var verdict = SfcVerifyOnlyEvaluator.Evaluate(1, "为了使用 sfc 工具，你必须作为管理员运行控制台会话。");
        Assert.False(verdict.Pass);
        Assert.Contains("为了使用 sfc 工具", verdict.Detail);
    }

    [Fact]
    public void Sfc_NonZero_Exit_With_Success_Marker_Is_Still_Pass()
    {
        // Belt-and-braces: some localized builds may return a non-zero exit
        // while printing the success marker — the marker corroborates Pass.
        var verdict = SfcVerifyOnlyEvaluator.Evaluate(1, "Windows 资源保护未找到任何完整性冲突。");
        Assert.True(verdict.Pass);
    }

    // ---- 2. Native output decoding (UTF-16 / NUL) ----

    [Fact]
    public void NativeDecoder_Decodes_Utf16_Chinese_With_Low_Nul_Ratio()
    {
        // The real sfc.exe capture: UTF-16LE text with a NUL ratio around 0.16
        // (Chinese dominates, only a few ASCII characters). The dense-NUL
        // heuristic must NOT be required to detect UTF-16.
        const string chinese = "为了使用 sfc 工具，你必须作为管理员运行控制台会话。";
        var utf16 = Encoding.Unicode.GetBytes(chinese);
        var nulRatio = utf16.Count(b => b == 0) / (double)utf16.Length;
        Assert.True(nulRatio < 0.3, $"fixture NUL ratio must be low, was {nulRatio}");

        var decoded = NativeOutputDecoder.DecodeBestEffort(utf16);
        Assert.Equal(chinese, decoded);
    }

    [Fact]
    public void NativeDecoder_Strips_Nuls_From_Utf16_Ascii_Capture()
    {
        // A UTF-16 capture of ASCII text decodes to the same ASCII (no NULs).
        var raw = Encoding.Unicode.GetBytes("sfc /verifyonly\r\n");
        var decoded = NativeOutputDecoder.DecodeBestEffort(raw);
        Assert.Equal(-1, decoded.IndexOf('\u0000'));
        Assert.Contains("sfc /verifyonly", decoded);
    }

    [Fact]
    public void NativeDecoder_Keeps_Utf8_Text()
    {
        var utf8 = Encoding.UTF8.GetBytes("Windows 资源保护未找到任何完整性冲突。");
        Assert.Equal("Windows 资源保护未找到任何完整性冲突。", NativeOutputDecoder.DecodeBestEffort(utf8));
    }

    [Fact]
    public void NativeDecoder_Sanitize_Removes_Nuls_And_CarriageReturns()
    {
        Assert.Equal("ab\ncd", NativeOutputDecoder.Sanitize("a\u0000b\r\nc\u0000d"));
    }

    [Fact]
    public void NativeDecoder_Compact_Collapses_Whitespace()
    {
        Assert.Equal("a b c", NativeOutputDecoder.Compact(" a \r\n b  c \u0000 "));
        Assert.Equal(-1, NativeOutputDecoder.Compact("\u0000\u0000").IndexOf('\u0000'));
    }

    // ---- 3. Registry scope semantics ----

    [Fact]
    public void Balanced_StartValues_Are_CurrentUserEffective_Scope()
    {
        var state = ProfileExpectedStateParser.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "scripts", "balanced-expected-state.json")));
        Assert.NotNull(state);

        var rec = state!.RegistryChecks.Single(r => r.Name == "Start_ShowRecommended");
        Assert.Equal("CurrentUserEffective", rec.Scope);
        Assert.Equal("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced", rec.Path);
        Assert.Equal("0", rec.ExpectedData);

        var recent = state.RegistryChecks.Single(r => r.Name == "Start_ShowRecent");
        Assert.Equal("CurrentUserEffective", recent.Scope);
        Assert.Equal("0", recent.ExpectedData);
    }

    [Fact]
    public void Balanced_Machine_Policies_Remain_OfflineMachine_Scope()
    {
        var state = ProfileExpectedStateParser.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "scripts", "balanced-expected-state.json")));
        Assert.NotNull(state);

        var machine = state!.RegistryChecks.Where(r => r.Scope == "OfflineMachine").ToList();
        Assert.Equal(4, machine.Count);
        Assert.All(machine, r => Assert.StartsWith("SOFTWARE\\", r.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Expected_State_With_Unknown_Scope_Is_Rejected()
    {
        const string json = """
        {
          "profileId": "Balanced",
          "appxAbsent": [],
          "registryChecks": [
            {"scope": "HostHKCU", "path": "Software\\X", "name": "V", "expectedData": "1"}
          ]
        }
        """;
        Assert.Null(ProfileExpectedStateParser.Parse(json));
    }

    [Fact]
    public void Expected_State_With_Missing_Scope_Is_Rejected()
    {
        const string json = """
        {
          "profileId": "Balanced",
          "appxAbsent": [],
          "registryChecks": [
            {"path": "Software\\X", "name": "V", "expectedData": "1"}
          ]
        }
        """;
        Assert.Null(ProfileExpectedStateParser.Parse(json));
    }

    [Fact]
    public void Expected_State_Scopes_Enum_Values_Are_Complete()
    {
        // Every declared scope value must map to the C# enum (drives the
        // installed-OS verification switch).
        Assert.True(Enum.IsDefined(typeof(RegistryCheckScope), nameof(RegistryCheckScope.OfflineMachine)));
        Assert.True(Enum.IsDefined(typeof(RegistryCheckScope), nameof(RegistryCheckScope.CurrentUserEffective)));
        Assert.True(Enum.IsDefined(typeof(RegistryCheckScope), nameof(RegistryCheckScope.DefaultUserTemplate)));
    }

    // ---- 4. Unicode / mojibake ----

    [Fact]
    public void Health_Parser_RoundTrips_Chinese_Text_Without_Mojibake()
    {
        const string zhDetail = "Windows 资源保护未找到任何完整性冲突。";
        var json = "{" +
                   "\"sections\":{" +
                   "\"media\":{\"status\":\"Pass\",\"checks\":[]}," +
                   "\"profile\":{\"status\":\"Pass\",\"checks\":[]}," +
                   "\"windowsIdentity\":{\"status\":\"Pass\",\"checks\":[]}," +
                   "\"bootAndShell\":{\"status\":\"Pass\",\"checks\":[]}," +
                   "\"devices\":{\"status\":\"Pass\",\"checks\":[]}," +
                   "\"network\":{\"status\":\"Pass\",\"checks\":[]}," +
                   "\"servicing\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"sfcVerifyOnly\",\"status\":\"Pass\",\"detail\":\"" + zhDetail + "\"}]}," +
                   "\"windowsUpdate\":{\"status\":\"Pass\",\"checks\":[]}," +
                   "\"security\":{\"status\":\"Pass\",\"checks\":[]}," +
                   "\"storeAndAppPlatform\":{\"status\":\"Pass\",\"checks\":[]}," +
                   "\"profileExpectedChanges\":{\"status\":\"Pass\",\"checks\":[]}" +
                   "}}";

        var result = HealthReportParser.Parse(json);
        Assert.True(result.SchemaValid);
        var check = result.Report!.Servicing.Checks.Single(c => c.Name == "sfcVerifyOnly");
        Assert.Equal(zhDetail, check.Detail); // exact Unicode round-trip, no mojibake
        Assert.DoesNotContain("\uFFFD", check.Detail);
    }

    [Fact]
    public void Health_Script_File_Is_Ascii_With_Utf8_Bom()
    {
        // The original mojibake ("鈥?") came from PowerShell 5.1 parsing a
        // UTF-8-no-BOM script as ANSI. The script must carry a UTF-8 BOM and a
        // pure-ASCII body (the only non-ASCII is the required Chinese SFC
        // success marker, which the BOM makes safe).
        var path = Path.Combine(RepoRoot(), "scripts", "Validate-WinForgeInstallation.ps1");
        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "script must start with a UTF-8 BOM (PowerShell 5.1 requires it to read UTF-8)");

        var body = bytes.Skip(3).ToArray();
        var nonAscii = body.Count(b => b > 0x7F);
        Assert.True(nonAscii <= 30, $"expected at most the Chinese SFC marker bytes, found {nonAscii}");
    }

    // ---- 5. Aggregation with corrected checks ----

    [Fact]
    public void FullHealth_Aggregation_With_Corrected_Checks_Passes()
    {
        // Simulates the corrected report: all sections Pass, including
        // profileExpectedChanges with CurrentUserEffective Start values verified.
        var result = HealthReportParser.Parse(SampleCorrectedReport());
        Assert.True(result.SchemaValid);
        Assert.Equal(HealthStatus.Pass, result.Report!.OverallStatus);
        Assert.True(result.Report.FullHealthValidated);
        Assert.Empty(result.Report.Failures);
    }

    // =====================================================================
    // 6. Stage 16.1b — REQUIRED vs OPTIONAL gate semantics
    // =====================================================================

    private static string ServicingJson(
        string checkHealth, string sfc, string scanHealth, bool scanHealthOptional = true)
        => "{" +
           $"\"sections\":{{" +
           $"\"media\":{{\"status\":\"Pass\",\"checks\":[{{\"name\":\"iso\",\"status\":\"Pass\"}}]}}," +
           $"\"profile\":{{\"status\":\"Pass\",\"checks\":[{{\"name\":\"profile\",\"status\":\"Pass\"}}]}}," +
           $"\"windowsIdentity\":{{\"status\":\"Pass\",\"checks\":[{{\"name\":\"edition\",\"status\":\"Pass\"}}]}}," +
           $"\"bootAndShell\":{{\"status\":\"Pass\",\"checks\":[{{\"name\":\"explorer\",\"status\":\"Pass\"}}]}}," +
           $"\"devices\":{{\"status\":\"Pass\",\"checks\":[{{\"name\":\"deviceProblems\",\"status\":\"Pass\"}}]}}," +
           $"\"network\":{{\"status\":\"Pass\",\"checks\":[{{\"name\":\"dns\",\"status\":\"Pass\"}}]}}," +
           $"\"servicing\":{{\"status\":\"Pass\",\"checks\":[" +
           $"{{\"name\":\"dismCheckHealth\",\"status\":\"{checkHealth}\"}}," +
           $"{{\"name\":\"sfcVerifyOnly\",\"status\":\"{sfc}\"}}," +
           $"{{\"name\":\"dismScanHealth\",\"status\":\"{scanHealth}\",\"requiredForFullHealth\":{(scanHealthOptional ? "false" : "true")}}}" +
           $"]}}," +
           $"\"windowsUpdate\":{{\"status\":\"Pass\",\"checks\":[{{\"name\":\"wuauserv\",\"status\":\"Pass\"}}]}}," +
           $"\"security\":{{\"status\":\"Pass\",\"checks\":[{{\"name\":\"defender\",\"status\":\"Pass\"}}]}}," +
           $"\"storeAndAppPlatform\":{{\"status\":\"Pass\",\"checks\":[{{\"name\":\"store\",\"status\":\"Pass\"}}]}}," +
           $"\"profileExpectedChanges\":{{\"status\":\"Pass\",\"checks\":[{{\"name\":\"appx\",\"status\":\"Pass\"}}]}}" +
           "}}";

    [Fact]
    public void Required_Pass_Optional_NotTested_Is_FullHealth_Eligible()
    {
        // Stage 16.1b root fix: DISM /ScanHealth is OPTIONAL. CheckHealth + SFC
        // (required) Pass, ScanHealth NotTested -> servicing section Pass and
        // FullHealthValidated true.
        var result = HealthReportParser.Parse(ServicingJson("Pass", "Pass", "NotTested"));
        Assert.True(result.SchemaValid);
        Assert.Equal(HealthStatus.Pass, result.Report!.Servicing.Status); // required-only section status
        Assert.Equal(HealthStatus.Pass, result.Report.OverallStatus);
        Assert.True(result.Report.FullHealthValidated);
        // The optional NotTested check stays visible in the report.
        var scan = result.Report.Servicing.Checks.Single(c => c.Name == "dismScanHealth");
        Assert.Equal(HealthStatus.NotTested, scan.Status);
        Assert.False(scan.RequiredForFullHealth);
    }

    [Fact]
    public void Required_Servicing_Fail_Blocks_FullHealth()
    {
        var result = HealthReportParser.Parse(ServicingJson("Fail", "Pass", "NotTested"));
        Assert.True(result.SchemaValid);
        Assert.Equal(HealthStatus.Fail, result.Report!.Servicing.Status);
        Assert.Equal(HealthStatus.Fail, result.Report.OverallStatus);
        Assert.False(result.Report.FullHealthValidated);
        Assert.Contains(result.Report.Failures, f => f.Contains("dismCheckHealth", StringComparison.Ordinal));
    }

    [Fact]
    public void Optional_ScanHealth_Fail_Is_Deterministic_And_Blocks()
    {
        // Documented deterministic behavior: a Fail on ANY check - including the
        // OPTIONAL ScanHealth - is conservatively treated as a blocker (a
        // corruption finding is never silently certified). It is also surfaced
        // in failures + overall.
        var result = HealthReportParser.Parse(ServicingJson("Pass", "Pass", "Fail"));
        Assert.True(result.SchemaValid);
        Assert.Equal(HealthStatus.Fail, result.Report!.OverallStatus);
        Assert.False(result.Report.FullHealthValidated);
        Assert.Contains(result.Report.Failures, f => f.Contains("dismScanHealth", StringComparison.Ordinal));
    }

    [Fact]
    public void Activation_Warning_Does_Not_Block_FullHealth()
    {
        // Real evidence: windowsIdentity = Warning only because activation is
        // Notification. Activation is informational/report-only.
        var json = AllSectionsPassJson()
            .Replace("\"windowsIdentity\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"edition\",\"status\":\"Pass\"}]}",
                "\"windowsIdentity\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"edition\",\"status\":\"Pass\"},{\"name\":\"activation\",\"status\":\"Warning\",\"detail\":\"Notification\",\"requiredForFullHealth\":false}]}",
                StringComparison.Ordinal);
        var result = HealthReportParser.Parse(json);
        Assert.True(result.SchemaValid);
        Assert.Equal(HealthStatus.Warning, result.Report!.OverallStatus);
        Assert.True(result.Report.FullHealthValidated); // activation never blocks
    }

    [Fact]
    public void Https_Environmental_Warning_Does_Not_Block_FullHealth()
    {
        // Real evidence: network fundamentals Pass, HTTPS TLS-trust Warning.
        var json = AllSectionsPassJson()
            .Replace("\"network\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"dns\",\"status\":\"Pass\"}]}",
                "\"network\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"dhcpIp\",\"status\":\"Pass\"},{\"name\":\"dns\",\"status\":\"Pass\"},{\"name\":\"httpsConnectivity\",\"status\":\"Warning\",\"detail\":\"TLS trust\",\"requiredForFullHealth\":false}]}",
                StringComparison.Ordinal);
        var result = HealthReportParser.Parse(json);
        Assert.True(result.SchemaValid);
        Assert.Equal(HealthStatus.Warning, result.Report!.OverallStatus);
        Assert.True(result.Report.FullHealthValidated);
    }

    [Fact]
    public void IpDns_Failure_Blocks_FullHealth()
    {
        // A genuine adapter/IP/DNS failure must NEVER pass (Stage 16.1b §4).
        var json = AllSectionsPassJson()
            .Replace("\"network\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"dns\",\"status\":\"Pass\"}]}",
                "\"network\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"dhcpIp\",\"status\":\"Fail\",\"detail\":\"No non-APIPA IPv4\"},{\"name\":\"dns\",\"status\":\"Pass\"}]}",
                StringComparison.Ordinal);
        var result = HealthReportParser.Parse(json);
        Assert.True(result.SchemaValid);
        Assert.Equal(HealthStatus.Fail, result.Report!.OverallStatus);
        Assert.False(result.Report.FullHealthValidated);
    }

    [Fact]
    public void Required_NotTested_Blocks_Even_With_No_Failures()
    {
        // failures=[] alone is NOT sufficient: an untested REQUIRED check blocks.
        var result = HealthReportParser.Parse(ServicingJson("Pass", "NotTested", "NotTested"));
        Assert.True(result.SchemaValid);
        Assert.Empty(result.Report!.Failures);
        Assert.False(result.Report.FullHealthValidated);
    }

    [Fact]
    public void Required_Optional_Flag_Serialization_Roundtrip()
    {
        // requiredForFullHealth is exposed in the report JSON; an omitted flag
        // defaults to REQUIRED (true) so old reports stay strict.
        const string json = "{" +
                            "\"sections\":{" +
                            "\"media\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"iso\",\"status\":\"Pass\"}]}," +
                            "\"profile\":{\"status\":\"Pass\",\"checks\":[]}," +
                            "\"windowsIdentity\":{\"status\":\"Pass\",\"checks\":[]}," +
                            "\"bootAndShell\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"explorer\",\"status\":\"Pass\"}]}," +
                            "\"devices\":{\"status\":\"Pass\",\"checks\":[]}," +
                            "\"network\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"dns\",\"status\":\"Pass\"}]}," +
                            "\"servicing\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"dismCheckHealth\",\"status\":\"Pass\"},{\"name\":\"dismScanHealth\",\"status\":\"NotTested\",\"requiredForFullHealth\":false}]}," +
                            "\"windowsUpdate\":{\"status\":\"Pass\",\"checks\":[]}," +
                            "\"security\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"defender\",\"status\":\"Pass\"}]}," +
                            "\"storeAndAppPlatform\":{\"status\":\"Pass\",\"checks\":[]}," +
                            "\"profileExpectedChanges\":{\"status\":\"Pass\",\"checks\":[]}" +
                            "}}";
        var result = HealthReportParser.Parse(json);
        Assert.True(result.SchemaValid);
        Assert.True(result.Report!.Servicing.Checks.Single(c => c.Name == "dismCheckHealth").RequiredForFullHealth); // omitted -> required
        Assert.False(result.Report.Servicing.Checks.Single(c => c.Name == "dismScanHealth").RequiredForFullHealth); // explicit false
    }

    [Fact]
    public void Real_Balanced_Second_Report_Fixture_Evaluates_FullHealth_True()
    {
        // The AUTHORITATIVE second real Balanced report (Stage 16.1b): zero
        // failures, windowsIdentity=Warning (activation only), network=Warning
        // (HTTPS trust only), servicing required checks Pass + optional
        // ScanHealth NotTested. Expected: overallStatus=Warning,
        // fullHealthValidated=true.
        const string json = "{" +
                            "\"sections\":{" +
                            "\"media\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"isoMedia\",\"status\":\"Pass\",\"detail\":\"WinForge-Balanced...iso\"}]}," +
                            "\"profile\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"profileId\",\"status\":\"Pass\",\"detail\":\"Balanced\"}]}," +
                            "\"windowsIdentity\":{\"status\":\"Warning\",\"checks\":[{\"name\":\"edition\",\"status\":\"Pass\",\"detail\":\"Windows 11 Pro\"},{\"name\":\"build\",\"status\":\"Pass\",\"detail\":\"26200.8037 (25H2)\"},{\"name\":\"architecture\",\"status\":\"Pass\",\"detail\":\"64 位\"},{\"name\":\"language\",\"status\":\"Pass\",\"detail\":\"zh-CN\"},{\"name\":\"activation\",\"status\":\"Warning\",\"detail\":\"Notification (report only)\",\"requiredForFullHealth\":false},{\"name\":\"systemBoot\",\"status\":\"Pass\",\"detail\":\"ok\"}]}," +
                            "\"bootAndShell\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"explorer\",\"status\":\"Pass\",\"detail\":\"running\"}]}," +
                            "\"devices\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"deviceProblems\",\"status\":\"Pass\",\"detail\":\"none\"}]}," +
                            "\"network\":{\"status\":\"Warning\",\"checks\":[{\"name\":\"dhcpIp\",\"status\":\"Pass\",\"detail\":\"192.168.x.x\"},{\"name\":\"dns\",\"status\":\"Pass\",\"detail\":\"ok\"},{\"name\":\"httpsConnectivity\",\"status\":\"Warning\",\"detail\":\"TLS trust channel unavailable\",\"requiredForFullHealth\":false}]}," +
                            "\"servicing\":{\"status\":\"NotTested\",\"checks\":[{\"name\":\"dismCheckHealth\",\"status\":\"Pass\",\"detail\":\"no corruption\"},{\"name\":\"dismScanHealth\",\"status\":\"NotTested\",\"detail\":\"Skipped (opt-in)\",\"requiredForFullHealth\":false},{\"name\":\"sfcVerifyOnly\",\"status\":\"Pass\",\"detail\":\"passed (exit 0)\"}]}," +
                            "\"windowsUpdate\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"wuauserv\",\"status\":\"Pass\",\"detail\":\"present\"}]}," +
                            "\"security\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"defender\",\"status\":\"Pass\",\"detail\":\"present\"}]}," +
                            "\"storeAndAppPlatform\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"microsoftStore\",\"status\":\"Pass\",\"detail\":\"present\"}]}," +
                            "\"profileExpectedChanges\":{\"status\":\"Pass\",\"checks\":[{\"name\":\"appxAbsent_Microsoft.WindowsFeedbackHub\",\"status\":\"Pass\",\"detail\":\"absent\"},{\"name\":\"reg_Start_ShowRecent\",\"status\":\"Pass\",\"detail\":\"HKCU = 0\"}]}" +
                            "}}";
        var result = HealthReportParser.Parse(json);
        Assert.True(result.SchemaValid);
        Assert.Empty(result.Report!.Failures);
        Assert.Equal(HealthStatus.Warning, result.Report.OverallStatus);
        Assert.True(result.Report.FullHealthValidated); // the whole point of 16.1b
        Assert.Equal(HealthStatus.Pass, result.Report.Servicing.Status); // required-only display
        Assert.Equal(HealthStatus.Pass, result.Report.WindowsIdentity.Status); // activation warning is optional -> required-only display is Pass
        Assert.Equal(HealthStatus.Pass, result.Report.Network.Status); // HTTPS trust optional -> required-only display is Pass
    }

    private static string AllSectionsPassJson()
    {
        static string Sec(string status, string checks = "") => $"{{\"status\":\"{status}\",\"checks\":[{checks}]}}";
        return "{" +
               $"\"sections\":{{" +
               $"\"media\":{Sec("Pass", "{\"name\":\"iso\",\"status\":\"Pass\"}")}," +
               $"\"profile\":{Sec("Pass", "{\"name\":\"profile\",\"status\":\"Pass\"}")}," +
               $"\"windowsIdentity\":{Sec("Pass", "{\"name\":\"edition\",\"status\":\"Pass\"}")}," +
               $"\"bootAndShell\":{Sec("Pass", "{\"name\":\"explorer\",\"status\":\"Pass\"}")}," +
               $"\"devices\":{Sec("Pass", "{\"name\":\"deviceProblems\",\"status\":\"Pass\"}")}," +
               $"\"network\":{Sec("Pass", "{\"name\":\"dns\",\"status\":\"Pass\"}")}," +
               $"\"servicing\":{Sec("Pass", "{\"name\":\"sfcVerifyOnly\",\"status\":\"Pass\"}")}," +
               $"\"windowsUpdate\":{Sec("Pass")}," +
               $"\"security\":{Sec("Pass", "{\"name\":\"defender\",\"status\":\"Pass\"}")}," +
               $"\"storeAndAppPlatform\":{Sec("Pass")}," +
               $"\"profileExpectedChanges\":{Sec("Pass", "{\"name\":\"appx\",\"status\":\"Pass\"}")}" +
               "}}";
    }

    private static string SampleCorrectedReport()
    {
        static string Sec(string status, string checks = "") => $"{{\"status\":\"{status}\",\"checks\":[{checks}]}}";
        // Every critical section carries at least one REQUIRED check; the
        // optional ScanHealth row is NotTested and must NOT block.
        return "{" +
               $"\"sections\":{{" +
               $"\"media\":{Sec("Pass", "{\"name\":\"iso\",\"status\":\"Pass\",\"detail\":\"WinForge-Balanced...iso\"}")}," +
               $"\"profile\":{Sec("Pass", "{\"name\":\"profile\",\"status\":\"Pass\",\"detail\":\"Balanced\"}")}," +
               $"\"windowsIdentity\":{Sec("Pass", "{\"name\":\"edition\",\"status\":\"Pass\",\"detail\":\"Windows 11 Pro\"}")}," +
               $"\"bootAndShell\":{Sec("Pass", "{\"name\":\"explorer\",\"status\":\"Pass\",\"detail\":\"running\"}")}," +
               $"\"devices\":{Sec("Pass", "{\"name\":\"deviceProblems\",\"status\":\"Pass\",\"detail\":\"none\"}")}," +
               $"\"network\":{Sec("Pass", "{\"name\":\"dns\",\"status\":\"Pass\",\"detail\":\"ok\"}")}," +
               $"\"servicing\":{Sec("Pass", "{\"name\":\"sfcVerifyOnly\",\"status\":\"Pass\",\"detail\":\"Windows 资源保护未找到任何完整性冲突。\"},{\"name\":\"dismScanHealth\",\"status\":\"NotTested\",\"detail\":\"Skipped\",\"requiredForFullHealth\":false}")}," +
               $"\"windowsUpdate\":{Sec("Pass")}," +
               $"\"security\":{Sec("Pass", "{\"name\":\"defender\",\"status\":\"Pass\",\"detail\":\"present\"}")}," +
               $"\"storeAndAppPlatform\":{Sec("Pass")}," +
               $"\"profileExpectedChanges\":{Sec("Pass", "{\"name\":\"reg_Start_ShowRecommended\",\"status\":\"Pass\",\"detail\":\"HKCU ...= 0\"}")}" +
               "}}";
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
}
