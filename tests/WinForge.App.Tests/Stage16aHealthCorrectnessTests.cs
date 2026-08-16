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

    private static string SampleCorrectedReport()
    {
        static string Sec(string status, string checks = "") => $"{{\"status\":\"{status}\",\"checks\":[{checks}]}}";
        return "{" +
               $"\"sections\":{{" +
               $"\"media\":{Sec("Pass", "{\"name\":\"iso\",\"status\":\"Pass\",\"detail\":\"WinForge-Balanced...iso\"}")}," +
               $"\"profile\":{Sec("Pass", "{\"name\":\"profile\",\"status\":\"Pass\",\"detail\":\"Balanced\"}")}," +
               $"\"windowsIdentity\":{Sec("Pass", "{\"name\":\"edition\",\"status\":\"Pass\",\"detail\":\"Windows 11 Pro\"}")}," +
               $"\"bootAndShell\":{Sec("Pass", "{\"name\":\"explorer\",\"status\":\"Pass\",\"detail\":\"running\"}")}," +
               $"\"devices\":{Sec("Pass", "{\"name\":\"deviceProblems\",\"status\":\"Pass\",\"detail\":\"none\"}")}," +
               $"\"network\":{Sec("Pass", "{\"name\":\"dns\",\"status\":\"Pass\",\"detail\":\"ok\"}")}," +
               $"\"servicing\":{Sec("Pass", "{\"name\":\"sfcVerifyOnly\",\"status\":\"Pass\",\"detail\":\"Windows 资源保护未找到任何完整性冲突。\"}")}," +
               $"\"windowsUpdate\":{Sec("Pass")}," +
               $"\"security\":{Sec("Pass")}," +
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
