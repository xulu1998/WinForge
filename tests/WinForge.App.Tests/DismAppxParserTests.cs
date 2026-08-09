using System.Linq;
using WinForge.Core.Models;
using WinForge.Infrastructure.Customization;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Parsing of DISM <c>/Get-ProvisionedAppxPackages</c> output. Verifies exact-identity
/// extraction (no fuzzy matching) and tolerant handling of empty / malformed input.
/// </summary>
public class DismAppxParserTests
{
    // REAL DISM output copied from a Windows 11 Pro mounted image
    // (`dism /English /Image:<mount> /Get-ProvisionedAppxPackages`).
    // Note the SINGLE-WORD headers `DisplayName` then `PackageName`, and that
    // `PackageName` is the FULL identity (name_version_arch_~_publisher-hash)
    // while `DisplayName` is only the friendly name.
    private const string Sample = @"
Deployment Image Servicing and Management tool
Version: 10.0.26100.1

Image Version: 10.0.26100.1742

The following provisioned packages will be listed:

DisplayName : Clipchamp.Clipchamp
Version : 4.4.10720.0
Architecture : neutral
ResourceId : ~
PackageName : Clipchamp.Clipchamp_4.4.10720.0_neutral_~_yxz26nhyzhsrt
Regions : all

DisplayName : Microsoft.BingWeather
Version : 4.53.53006.0
Architecture : neutral
ResourceId : ~
PackageName : Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe
Regions : all

DisplayName : Microsoft.Windows.Photos
Version : 2024.11020.15005.0
Architecture : neutral
ResourceId : ~
PackageName : Microsoft.Windows.Photos_2024.11020.15005.0_neutral_~_8wekyb3d8bbwe
Regions : all
";

    [Fact]
    public void Parses_AllPackages_WithExactIdentity()
    {
        var result = DismAppxParser.Parse(Sample);
        Assert.Equal(3, result.Count);
        Assert.Contains(result, p => p.PackageName == "Clipchamp.Clipchamp_4.4.10720.0_neutral_~_yxz26nhyzhsrt");
        Assert.Contains(result, p => p.PackageName == "Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe");
        Assert.Contains(result, p => p.PackageName == "Microsoft.Windows.Photos_2024.11020.15005.0_neutral_~_8wekyb3d8bbwe");
    }

    [Fact]
    public void Parses_DisplayName_AndVersion()
    {
        var result = DismAppxParser.Parse(Sample);
        var weather = result.First(p => p.PackageName.Contains("BingWeather"));
        Assert.Equal("Microsoft.BingWeather", weather.DisplayName);
        Assert.Equal("4.53.53006.0", weather.Version);
        Assert.Equal(RiskClass.Removable, weather.Risk);
    }

    [Fact]
    public void EmptyOutput_ReturnsEmpty()
    {
        Assert.Empty(DismAppxParser.Parse(string.Empty));
        Assert.Empty(DismAppxParser.Parse(null!));
        Assert.Empty(DismAppxParser.Parse("No packages here"));
    }

    [Fact]
    public void TrimsWhitespace_AroundValues()
    {
        var outp = "PackageName :   Microsoft.XboxApp_1.0.0.0_neutral_~_8wekyb3d8bbwe   \nDisplayName : Xbox\n";
        var result = DismAppxParser.Parse(outp);
        Assert.Single(result);
        Assert.Equal("Microsoft.XboxApp_1.0.0.0_neutral_~_8wekyb3d8bbwe", result[0].PackageName);
    }

    [Fact]
    public void MissingPackageName_BlockSkipped()
    {
        // A block that has only a DisplayName (no PackageName) must be DROPPED,
        // never keyed by the friendly DisplayName — that would be the wrong
        // identity for /Remove-ProvisionedAppxPackage.
        var outp = "DisplayName : Orphan\nVersion : 1.0.0.0\n";
        var result = DismAppxParser.Parse(outp);
        Assert.Empty(result);
    }

    [Fact]
    public void Parses_RealDismSingleWordHeaders_DisplayNameBeforePackageName()
    {
        // Real `dism /Get-ProvisionedAppxPackages /English` emits SINGLE-WORD
        // headers, with `DisplayName` listed BEFORE `PackageName` — NOT the
        // multi-word "Deployment package name" invented by earlier parsing. This is
        // the exact format that previously yielded zero discovered apps.
        const string real = @"
Deployment Image Servicing and Management tool
Version: 10.0.26100.1

Image Version: 10.0.26100.1742

DisplayName : Clipchamp.Clipchamp
Version : 4.4.10720.0
Architecture : neutral
ResourceId : ~
PackageName : Clipchamp.Clipchamp_4.4.10720.0_neutral_~_yxz26nhyzhsrt
Regions : all

DisplayName : Microsoft.BingWeather
Version : 4.53.53006.0
Architecture : neutral
ResourceId : ~
PackageName : Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe
Regions : all
";
        var result = DismAppxParser.Parse(real);
        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.PackageName == "Clipchamp.Clipchamp_4.4.10720.0_neutral_~_yxz26nhyzhsrt");
        Assert.Contains(result, p => p.PackageName == "Microsoft.BingWeather_4.53.53006.0_neutral_~_8wekyb3d8bbwe");
        Assert.Contains(result, p => p.DisplayName == "Clipchamp.Clipchamp");
    }

    [Fact]
    public void RemovalIdentity_IsExactPackageName_NotDisplayName()
    {
        // The destructive operation must target the FULL PackageName identity
        // (name_version_arch_~_publisher-hash), never the friendly DisplayName.
        var result = DismAppxParser.Parse(Sample);
        Assert.Equal(3, result.Count);
        foreach (var p in result)
        {
            // Identity carries version + neutral + publisher hash segment.
            Assert.Contains("_neutral_~_", p.PackageName);
            // DisplayName is the short friendly name and must differ from identity.
            Assert.NotEqual(p.PackageName, p.DisplayName);
            Assert.DoesNotContain("_neutral_~_", p.DisplayName);
        }

        var clip = result.First(p => p.PackageName.Contains("Clipchamp"));
        Assert.Equal("Clipchamp.Clipchamp_4.4.10720.0_neutral_~_yxz26nhyzhsrt", clip.PackageName);
        Assert.Equal("Clipchamp.Clipchamp", clip.DisplayName);
    }

    [Fact]
    public void IsRecognizedOutput_True_ForEnglishBanner()
    {
        Assert.True(DismAppxParser.IsRecognizedOutput("Deployment Image Servicing and Management tool\nPackageName : X"));
    }

    [Fact]
    public void IsRecognizedOutput_True_ForGenuineZeroWithBanner()
    {
        // A valid /English run that genuinely lists zero provisioned packages
        // (no PackageName blocks) still carries the DISM banner — it is
        // recognizable as DISM output, so the discovery must treat it as a
        // legitimate SUCCESS(0), NOT as a parser failure. The zero is genuine.
        const string genuineZero = @"
Deployment Image Servicing and Management tool
Version: 10.0.26100.1

Image Version: 10.0.26100.1742

The following provisioned packages will be listed:
";
        Assert.True(DismAppxParser.IsRecognizedOutput(genuineZero));
        Assert.Empty(DismAppxParser.Parse(genuineZero));
    }

    [Fact]
    public void IsRecognizedOutput_False_ForLocalizedGarbage()
    {
        // German-style output with no English banner and no English key — the
        // kind of response produced when /English is not honoured.
        Assert.False(DismAppxParser.IsRecognizedOutput("Paketname : Microsoft.X\nAnzeigename : X"));
    }
}

/// <summary>
/// Parsing and classification of DISM <c>/Get-Packages</c> output. Verifies the
/// safe-removal gating: language / core / driver packages are Protected, optional
/// features are Removable.
/// </summary>
public class DismPackageParserTests
{
    private const string Sample = @"
Package Identity : Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~en-US~10.0.26100.1
State : Installed
Release Type : Language Pack

Package Identity : Microsoft-Windows-Client-ProfessionalEdition-Package~31bf3856ad364e35~amd64~~10.0.26100.1
State : Installed
Release Type : Feature Pack

Package Identity : Microsoft-Windows-InternetExplorer-Optional-Package~31bf3856ad364e35~amd64~~10.0.26100.1
State : Installed
Release Type : Feature Pack
";

    [Fact]
    public void Parses_AllPackages()
    {
        var result = DismPackageParser.Parse(Sample);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void LanguagePack_IsProtected()
    {
        var result = DismPackageParser.Parse(Sample);
        var lang = result.First(p => p.PackageIdentity.Contains("LanguagePack"));
        Assert.Equal(PackageClassification.Language, lang.Classification);
        Assert.Equal(RiskClass.Protected, lang.Risk);
    }

    [Fact]
    public void EditionPackage_IsProtected()
    {
        var result = DismPackageParser.Parse(Sample);
        var edition = result.First(p => p.PackageIdentity.Contains("ProfessionalEdition"));
        Assert.Equal(RiskClass.Protected, edition.Risk);
    }

    [Fact]
    public void OptionalFeature_IsRemovable()
    {
        var result = DismPackageParser.Parse(Sample);
        var ie = result.First(p => p.PackageIdentity.Contains("InternetExplorer-Optional"));
        Assert.Equal(PackageClassification.Feature, ie.Classification);
        Assert.Equal(RiskClass.Removable, ie.Risk);
    }

    [Fact]
    public void EmptyOutput_ReturnsEmpty()
    {
        Assert.Empty(DismPackageParser.Parse(string.Empty));
    }

    [Fact]
    public void DriverPackage_IsProtected()
    {
        var outp = "Package Identity : Microsoft-Windows-DisplayDriver-Package~31bf3856ad364e35~amd64~~10.0.26100.1\nState : Installed\nRelease Type : Driver\n";
        var result = DismPackageParser.Parse(outp);
        Assert.Single(result);
        Assert.Equal(RiskClass.Protected, result[0].Risk);
    }

    [Fact]
    public void NonAllowlistedCorePackage_IsProtected_NotSelectable()
    {
        // Microsoft-OneCore-ApplicationModel-Sync-Desktop-* is a core component,
        // not on the Step 3.3 removal allowlist — it must be Protected so the UI
        // cannot offer (and execution cannot remove) it.
        var outp = "Package Identity : Microsoft-OneCore-ApplicationModel-Sync-Desktop-Package~31bf3856ad364e35~amd64~~10.0.26100.1\nState : Installed\nRelease Type : Feature Pack\n";
        var result = DismPackageParser.Parse(outp);
        Assert.Single(result);
        Assert.Equal(PackageClassification.Feature, result[0].Classification);
        Assert.Equal(RiskClass.Protected, result[0].Risk);
        Assert.False(PackageRemovalPolicy.IsRemovalAllowed(result[0].PackageIdentity));
    }

    [Fact]
    public void AllowlistedPackage_RemainsRemovable_AndApprovedByPolicy()
    {
        var outp = "Package Identity : Microsoft-Windows-InternetExplorer-Optional-Package~31bf3856ad364e35~amd64~~10.0.26100.1\nState : Installed\nRelease Type : Feature Pack\n";
        var result = DismPackageParser.Parse(outp);
        Assert.Single(result);
        Assert.Equal(RiskClass.Removable, result[0].Risk);
        Assert.True(PackageRemovalPolicy.IsRemovalAllowed(result[0].PackageIdentity));
    }
}
