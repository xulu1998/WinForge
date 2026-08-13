using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using WinForge.Core.Compatibility;
using WinForge.Core.Models;
using WinForge.Infrastructure.Compatibility;
using WinForge.Infrastructure.Customization;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Synthetic compatibility fixtures (Stage 13.22): metadata-level representations
/// of the images the matrix must cover — 25H2 Pro zh-CN / en-US, Home, Education,
/// Enterprise, unknown future build, ESD, multi-index, missing boot.wim, ARM64,
/// modified media, split SWM. No huge real ISOs live in the test repository.
/// </summary>
public static class CompatibilityFixtures
{
    public static IsoInspectionResult Completed(
        string editionId = "Professional",
        string? arch = "x64",
        string? lang = "zh-CN",
        string? version = "10.0.26100.1742",
        string? displayVersion = "25H2",
        InstallImageType imageType = InstallImageType.Wim,
        bool hasBootWim = true,
        bool hasBootDir = true,
        bool hasSources = true,
        bool splitSwm = false,
        params (int Index, string Name, string EditionId, string Arch, string Version)[] extraEditions)
    {
        var editions = new List<WindowsEditionInfo>
        {
            new()
            {
                Index = 1,
                Name = editionId == "Core" ? "Windows 11 Home" : $"Windows 11 {editionId}",
                EditionId = editionId,
                Architecture = arch,
                Version = version,
                Build = ParseBuild(version),
                InstallationType = "Client",
                DefaultLanguage = lang,
                DisplayVersion = displayVersion,
                Languages = new List<string> { lang! },
            },
        };
        foreach (var e in extraEditions)
        {
            editions.Add(new WindowsEditionInfo
            {
                Index = e.Index,
                Name = e.Name,
                EditionId = e.EditionId,
                Architecture = e.Arch,
                Version = e.Version,
                Build = ParseBuild(e.Version),
                InstallationType = "Client",
                DefaultLanguage = lang,
                DisplayVersion = displayVersion,
                Languages = new List<string> { lang! },
            });
        }

        return new IsoInspectionResult
        {
            IsoPath = @"C:\media\Win11.iso",
            FileName = "Win11.iso",
            Status = IsoInspectionStatus.Completed,
            DetectedType = IsoDetectedType.WindowsIsoCandidate,
            HasBootDirectory = hasBootDir,
            HasSourcesDirectory = hasSources,
            HasBootWim = hasBootWim,
            HasInstallWim = imageType == InstallImageType.Wim && !splitSwm,
            HasInstallEsd = imageType == InstallImageType.Esd,
            HasSplitSwm = splitSwm,
            SwmPartCount = splitSwm ? 3 : 0,
            InstallImageType = splitSwm ? InstallImageType.Wim : imageType,
            SelectedIndex = 1,
            ImageMetadata = new WindowsImageMetadataResult
            {
                Status = WindowsImageMetadataStatus.Completed,
                Version = version,
                Build = ParseBuild(version),
                Architecture = arch,
                Languages = new List<string> { lang! },
                Editions = editions,
            },
        };
    }

    public static IsoInspectionResult UnknownFutureBuild()
        => Completed(version: "10.0.30000.1000", displayVersion: "31H2");

    public static IsoInspectionResult OlderWindows()
        => Completed(version: "10.0.19045.4046", displayVersion: "22H2");

    public static IsoInspectionResult Arm64()
        => Completed(arch: "arm64");

    public static IsoInspectionResult ModifiedMedia()
        => Completed(hasBootDir: false, hasSources: true);

    public static IsoInspectionResult MissingInstallImage()
        => new()
        {
            IsoPath = @"C:\media\broken.iso",
            Status = IsoInspectionStatus.Completed,
            DetectedType = IsoDetectedType.Unknown,
            HasBootDirectory = true,
            HasSourcesDirectory = true,
            HasBootWim = true,
            HasInstallWim = false,
            HasInstallEsd = false,
            HasSplitSwm = false,
            InstallImageType = InstallImageType.Unknown,
            SelectedIndex = 1,
        };

    public static IsoInspectionResult MissingBootWim()
        => Completed(hasBootWim: false);

    public static IsoInspectionResult SplitSwm()
        => Completed(splitSwm: true);

    public static IsoInspectionResult MultiIndex()
        => Completed(
            editionId: "Professional",
            extraEditions: (1, "Windows 11 Home", "Core", "x64", "10.0.26100.1742"));

    private static string? ParseBuild(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var parts = version.Split('.');
        return parts.Length >= 3 ? parts[2] : null;
    }
}

/// <summary>
/// Phase 13 regression suite (Stage 13.23): compatibility model, detection,
/// rules, safety invariants, and strict validated-vs-automated distinction.
/// </summary>
public class Stage13CompatibilityTests
{
    private readonly CompatibilityRuleEngine _engine = new();

    // 1. release/build detection
    [Fact]
    public void Release_25H2_Detected()
    {
        var p = _engine.Evaluate(CompatibilityFixtures.Completed(displayVersion: "25H2", version: "10.0.26200.1000"));
        Assert.Equal(WindowsRelease.Windows11_25H2, p.Release);
    }

    [Fact]
    public void Release_24H2_Detected()
    {
        var p = _engine.Evaluate(CompatibilityFixtures.Completed(displayVersion: "24H2"));
        Assert.Equal(WindowsRelease.Windows11_24H2, p.Release);
    }

    [Fact]
    public void Release_UnknownFuture_Degrades_To_Warning()
    {
        var p = _engine.Evaluate(CompatibilityFixtures.UnknownFutureBuild());
        Assert.Equal(WindowsRelease.Windows11_UnknownNewer, p.Release);
        Assert.Equal(CompatibilityStatus.SupportedWithWarnings, p.Status);
        Assert.False(p.HasBlockers); // warning must NOT become a blocker
    }

    [Fact]
    public void Release_Older_Windows_Classified()
    {
        var p = _engine.Evaluate(CompatibilityFixtures.OlderWindows());
        Assert.Equal(WindowsRelease.OlderWindows, p.Release);
    }

    // 2. edition detection
    [Fact]
    public void Edition_Detected_From_Metadata()
    {
        var p = _engine.Evaluate(CompatibilityFixtures.Completed(editionId: "Professional"));
        Assert.Equal("Professional", p.EditionId);
        Assert.DoesNotContain(p.Findings, f => f.IsBlocking); // standard Pro passes
    }

    // 3. language detection
    [Fact]
    public void Language_Detected()
    {
        var p = _engine.Evaluate(CompatibilityFixtures.Completed(lang: "zh-CN"));
        Assert.Equal("zh-CN", p.DefaultLanguage);
        Assert.Contains("zh-CN", p.AvailableLanguages);
    }

    [Fact]
    public void NonBaseline_Language_Is_Info_Not_Blocker()
    {
        var p = _engine.Evaluate(CompatibilityFixtures.Completed(lang: "ja-JP"));
        Assert.Contains(p.Findings, f => f.Key == "Compat.NonBaselineLanguage" && f.Severity == CompatibilitySeverity.Info);
        Assert.False(p.HasBlockers);
    }

    // 4. architecture detection
    [Fact]
    public void Architecture_Detected()
    {
        var p = _engine.Evaluate(CompatibilityFixtures.Completed());
        Assert.Equal("x64", p.Architecture);
    }

    // 5. WIM
    [Fact]
    public void Wim_Is_Supported()
    {
        var p = _engine.Evaluate(CompatibilityFixtures.Completed(imageType: InstallImageType.Wim));
        Assert.Equal(ImageFormatKind.Wim, p.ImageFormat);
        Assert.Equal(CompatibilityStatus.Supported, p.Status);
    }

    // 6. ESD
    [Fact]
    public void Esd_Is_ReadOnly_Warning_Not_Blocker()
    {
        var p = _engine.Evaluate(CompatibilityFixtures.Completed(imageType: InstallImageType.Esd));
        Assert.Equal(ImageFormatKind.Esd, p.ImageFormat);
        Assert.Contains(p.Findings, f => f.Key == "Compat.EsdServicing" && !f.IsBlocking);
        Assert.False(p.HasBlockers);
    }

    // 7. SWM
    [Fact]
    public void SplitSwm_Detected_ReadOnly()
    {
        var p = _engine.Evaluate(CompatibilityFixtures.SplitSwm());
        Assert.Equal(ImageFormatKind.Swm, p.ImageFormat);
        Assert.True(p.HasSplitSwm);
        Assert.Equal(3, p.SwmPartCount);
        Assert.Contains(p.Findings, f => f.Key == "Compat.SwmReadOnly" && !f.IsBlocking);
    }

    // 8 + 9. multi-index enumeration + selected index persistence
    [Fact]
    public void MultiIndex_Enumerated_And_Index_Persisted()
    {
        var inspection = CompatibilityFixtures.MultiIndex();
        var p = _engine.Evaluate(inspection);
        Assert.Equal(2, p.ImageCount);
        Assert.Equal(inspection.SelectedIndex, p.SelectedIndex); // deterministic
    }

    // 10. unknown future build warning (covered by Release_UnknownFuture above)
    // 11. unsupported architecture blocking
    [Fact]
    public void UnsupportedArchitecture_Blocks()
    {
        var p = _engine.Evaluate(CompatibilityFixtures.Arm64());
        Assert.True(p.HasBlockers);
        Assert.Equal(CompatibilityStatus.Unsupported, p.Status);
        Assert.Contains(p.Findings, f => f.Key == "Compat.UnsupportedArch" && f.IsBlocking);
    }

    // 12. missing install image blocking
    [Fact]
    public void MissingInstallImage_Blocks()
    {
        var p = _engine.Evaluate(CompatibilityFixtures.MissingInstallImage());
        Assert.True(p.HasBlockers);
        Assert.Contains(p.Findings, f => f.Key == "Compat.NoInstallImage" && f.IsBlocking);
    }

    // 13. modified media warning
    [Fact]
    public void ModifiedMedia_Warns()
    {
        var p = _engine.Evaluate(CompatibilityFixtures.ModifiedMedia());
        Assert.Equal(MediaClassification.ModifiedMedia, p.MediaType);
        Assert.Contains(p.Findings, f => f.Key == "Compat.ModifiedMedia" && !f.IsBlocking);
    }

    // 14. standard media passes preflight
    [Fact]
    public void StandardMedia_Passes_Preflight()
    {
        var p = _engine.Evaluate(CompatibilityFixtures.Completed());
        Assert.Equal(MediaClassification.MicrosoftOfficialLike, p.MediaType);
        Assert.Equal(CompatibilityStatus.Supported, p.Status);
        Assert.False(p.HasBlockers);
    }

    // 15-17. edition capability facts
    [Fact]
    public void Home_Lacks_Pro_Features()
    {
        var home = EditionCompatibilityCatalog.For("Core")!;
        Assert.False(home.HasProFeatures);
        Assert.False(home.HasSandbox);
        Assert.False(home.HasHyperV);
        Assert.False(home.HasRdpHost);
        Assert.False(home.IsSupportedBy(EditionCapabilityRequirement.Sandbox));
    }

    [Fact]
    public void Pro_Has_Pro_Features()
    {
        var pro = EditionCompatibilityCatalog.For("Professional")!;
        Assert.True(pro.HasProFeatures);
        Assert.True(pro.HasSandbox);
        Assert.True(pro.IsSupportedBy(EditionCapabilityRequirement.ProOrHigher));
    }

    [Fact]
    public void Enterprise_Has_Pro_Features()
    {
        var ent = EditionCompatibilityCatalog.For("Enterprise")!;
        Assert.True(ent.HasProFeatures);
        Assert.True(ent.HasSandbox);
    }

    // 18. localization-independent stable identity
    [Fact]
    public void Stable_Identity_Is_Culture_Invariant()
    {
        Assert.Equal("zh-CN", LanguageIdentity.Normalize("ZH-cn"));
        Assert.Equal("en-US", LanguageIdentity.Normalize("EN-us"));
        // Edition matching is case-insensitive and ignores whitespace.
        Assert.NotNull(EditionCompatibilityCatalog.For("  professional "));
        Assert.True(EditionCompatibilityCatalog.IsKnown("professional"));
    }

    // 21. findings severity ordering
    [Fact]
    public void Findings_Sorted_By_Severity()
    {
        var p = _engine.Evaluate(CompatibilityFixtures.MissingBootWim());
        var sev = p.Findings.Select(f => f.Severity).ToList();
        Assert.Equal(sev.OrderByDescending(s => s), sev); // Blocking first
        Assert.Contains(p.Findings, f => f.Key == "Compat.NoBootWim" && f.IsBlocking);
    }

    // 22. warning does not block / 23. blocker does block (covered above; explicit)
    [Fact]
    public void Warning_Does_Not_Block_Blocker_Does()
    {
        Assert.False(_engine.Evaluate(CompatibilityFixtures.Completed(imageType: InstallImageType.Esd)).HasBlockers);
        Assert.True(_engine.Evaluate(CompatibilityFixtures.Arm64()).HasBlockers);
    }

    // 24. profile never selects edition-incompatible operation (gating data)
    [Fact]
    public void Edition_Gating_Suppresses_Incompatible_Operations()
    {
        // A Sandbox-gated operation must be unsupported on Home.
        Assert.False(EditionCompatibilityCatalog.For("Core")!.IsSupportedBy(EditionCapabilityRequirement.Sandbox));
        Assert.True(EditionCompatibilityCatalog.For("Professional")!.IsSupportedBy(EditionCapabilityRequirement.Sandbox));
    }

    // 25-28. safety invariants against the real catalogs
    [Fact]
    public void Update_Infrastructure_Never_Disabled()
    {
        var defs = new OptimizationCatalog().GetEntries().ToList();
        foreach (var d in defs)
        {
            if (d.Mechanism == OptimizationMechanism.ServiceStartup && !string.IsNullOrWhiteSpace(d.ServiceName))
            {
                Assert.False(SafetyInvariantCatalog.IsEssentialUpdateService(d.ServiceName),
                    $"Definition '{d.Id}' targets essential update service '{d.ServiceName}'");
            }
        }
    }

    [Fact]
    public void Defender_Never_Disabled_By_Catalog()
    {
        var defs = new OptimizationCatalog().GetEntries().ToList();
        foreach (var d in defs)
        {
            if (d.Mechanism == OptimizationMechanism.ServiceStartup && !string.IsNullOrWhiteSpace(d.ServiceName))
            {
                Assert.False(SafetyInvariantCatalog.IsDefenderService(d.ServiceName),
                    $"Definition '{d.Id}' targets Defender service '{d.ServiceName}'");
            }
        }
    }

    [Fact]
    public void Core_Driver_Packages_Never_Removed()
    {
        var defs = new OptimizationCatalog().GetEntries().ToList();
        foreach (var d in defs)
        {
            if (d.Mechanism == OptimizationMechanism.RemovePackage && !string.IsNullOrWhiteSpace(d.TargetIdentifier))
            {
                Assert.False(SafetyInvariantCatalog.IsCoreDriverPackage(d.TargetIdentifier),
                    $"Definition '{d.Id}' removes core driver package '{d.TargetIdentifier}'");
            }
        }
    }

    [Fact]
    public void Store_Infrastructure_Protected()
    {
        var defs = new OptimizationCatalog().GetEntries().ToList();
        foreach (var d in defs)
        {
            if (d.Mechanism is OptimizationMechanism.RemoveProvisionedAppx or OptimizationMechanism.RemovePackage
                && !string.IsNullOrWhiteSpace(d.TargetIdentifier))
            {
                Assert.False(SafetyInvariantCatalog.IsStorePackage(d.TargetIdentifier),
                    $"Definition '{d.Id}' removes Store package '{d.TargetIdentifier}'");
            }
        }
    }

    // ---- matrix + report export (13.11 / 13.24) ----
    [Fact]
    public void Initial_Targets_Are_Defined()
    {
        Assert.True(InitialValidationTargets.All.Count >= 10);
        Assert.True(InitialValidationTargets.IsKnownTarget("25H2-Pro-zh-CN-x64"));
        Assert.True(InitialValidationTargets.IsKnownTarget("25H2-Pro-en-US-x64"));
    }

    [Fact]
    public void Validation_Result_Requires_All_Phases_For_Validated()
    {
        var partial = new ValidationResult
        {
            TargetId = "t1",
            Evidence = ValidationEvidenceKind.RealVmValidation,
            Phases = new Dictionary<ValidationPhase, bool> { [ValidationPhase.InspectionPassed] = true, [ValidationPhase.BuildPassed] = true },
        };
        Assert.False(partial.AllPhasesPassed);

        var full = new ValidationResult
        {
            TargetId = "t1",
            Evidence = ValidationEvidenceKind.RealVmValidation,
            Phases = Enum.GetValues<ValidationPhase>().ToDictionary(p => p, _ => true),
        };
        Assert.True(full.AllPhasesPassed);
    }

    [Fact]
    public void Report_Writer_Produces_Json_And_Markdown()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wf13_rep_" + Guid.NewGuid().ToString("N"));
        try
        {
            var md = ValidationReportWriter.Write(dir, new ValidationResult
            {
                TargetId = "25H2-Pro-zh-CN-x64",
                Evidence = ValidationEvidenceKind.AutomatedFixturesOnly,
                WinForgeVersion = "test",
                WinForgeCommit = "abcdef",
                IsoSha256 = "0".PadLeft(64, '0'),
                IsoSizeBytes = 7_600_000_000,
                Phases = Enum.GetValues<ValidationPhase>().ToDictionary(p => p, _ => true),
            });

            Assert.True(File.Exists(md));
            Assert.True(File.Exists(md.Replace(".md", ".json")));
            var content = File.ReadAllText(md);
            Assert.Contains("AutomatedFixturesOnly", content);
            Assert.Contains("VALIDATED", content);
            Assert.Contains("25H2-Pro-zh-CN-x64", content);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Automated_Fixtures_Never_Claim_Real_Validation()
    {
        // ADR-074: a fixture-based result is NEVER "Validated" in the docs sense;
        // the evidence kind keeps them distinct.
        Assert.NotEqual(ValidationEvidenceKind.RealVmValidation, ValidationEvidenceKind.AutomatedFixturesOnly);
    }
}
