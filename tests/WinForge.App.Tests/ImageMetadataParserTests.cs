using WinForge.Core.Models;
using WinForge.Infrastructure.ImageMetadata;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Pure parsing of <c>dism /English /Get-WimInfo</c> output. No process, no real
/// image, no Windows required — the parser is a deterministic function of the
/// captured text. These tests guard the field extraction, tolerance for unknown
/// / reordered / localized-vs-English fields, and the top-level "Mixed" rule.
/// </summary>
public class ImageMetadataParserTests
{
    private const string SingleIndexWim = @"
Deployment Image Servicing and Management tool
Version: 10.0.26100.1

Details for image : C:\install.wim

Index : 1
Name : Windows 11 Home
Description : Windows 11 Home
Size : 15,297,491,328 bytes
WIM Bootable : No
Architecture : x64
Hal :
Version : 10.0.26100.1742
ServicePack Build : 1742
ServicePack Level : 0
Edition : Home
Edition Id :
Installation : Client
Languages :
        en-US (Default)
Default Language : en-US
";

    private const string MultiIndexWim = @"
Details for image : C:\install.wim

Index : 1
Name : Windows 11 Home
Description : Windows 11 Home
Architecture : x64
Version : 10.0.26100.1742
Edition : Home
Edition Id :
Installation : Client
Languages :
        en-US (Default)

Index : 2
Name : Windows 11 Pro
Description : Windows 11 Pro
Architecture : x64
Version : 10.0.26100.1742
Edition : Professional
Edition Id :
Installation : Client
Languages :
        en-US (Default)
";

    [Fact]
    public void Single_Index_Wim_Parses_All_Fields()
    {
        var result = DismWimInfoParser.Parse(SingleIndexWim, @"C:\install.wim", WindowsImageType.Wim);

        Assert.Equal(WindowsImageMetadataStatus.Completed, result.Status);
        Assert.Equal(WindowsImageType.Wim, result.ImageType);
        Assert.Single(result.Editions);

        var ed = result.Editions[0];
        Assert.Equal(1, ed.Index);
        Assert.Equal("Windows 11 Home", ed.Name);
        Assert.Equal("Windows 11 Home", ed.Description);
        Assert.Equal("x64", ed.Architecture);
        Assert.Equal("10.0.26100.1742", ed.Version);
        Assert.Equal("26100", ed.Build);
        Assert.Equal("Client", ed.InstallationType);
        Assert.Equal("en-US", Assert.Single(ed.Languages));
    }

    [Fact]
    public void Multi_Index_Wim_Parses_Every_Index()
    {
        var result = DismWimInfoParser.Parse(MultiIndexWim, @"C:\install.wim", WindowsImageType.Wim);

        Assert.Equal(WindowsImageMetadataStatus.Completed, result.Status);
        Assert.Equal(2, result.Editions.Count);

        Assert.Equal(1, result.Editions[0].Index);
        Assert.Equal("Windows 11 Home", result.Editions[0].Name);
        Assert.Equal(2, result.Editions[1].Index);
        Assert.Equal("Windows 11 Pro", result.Editions[1].Name);
    }

    [Fact]
    public void Home_And_Pro_Are_Enumerated()
    {
        var result = DismWimInfoParser.Parse(MultiIndexWim, @"C:\install.wim", WindowsImageType.Wim);

        var names = result.Editions.ConvertAll(e => e.Name!);
        Assert.Contains("Windows 11 Home", names);
        Assert.Contains("Windows 11 Pro", names);
    }

    [Fact]
    public void Architecture_Is_Parsed()
    {
        var result = DismWimInfoParser.Parse(SingleIndexWim, @"C:\install.wim", WindowsImageType.Wim);

        Assert.Equal("x64", result.Editions[0].Architecture);
        Assert.Equal("x64", result.Architecture); // consistent across editions
    }

    [Fact]
    public void Version_And_Build_Are_Parsed()
    {
        var result = DismWimInfoParser.Parse(SingleIndexWim, @"C:\install.wim", WindowsImageType.Wim);

        Assert.Equal("10.0.26100.1742", result.Editions[0].Version);
        Assert.Equal("26100", result.Editions[0].Build);
        Assert.Equal("10.0.26100.1742", result.Version);
        Assert.Equal("26100", result.Build);
    }

    [Fact]
    public void Languages_Are_Parsed_From_Multiline_Block()
    {
        const string output = @"
Details for image : C:\install.wim

Index : 1
Name : Windows 11 Home
Architecture : x64
Version : 10.0.26100.1742
Installation : Client
Languages :
        en-US (Default)
        fr-FR
        de-DE
";
        var result = DismWimInfoParser.Parse(output, @"C:\install.wim", WindowsImageType.Wim);

        Assert.Equal(new[] { "en-US", "fr-FR", "de-DE" }, result.Editions[0].Languages);
        // Single edition -> top-level language set is that edition's set.
        Assert.Equal(new[] { "en-US", "fr-FR", "de-DE" }, result.Languages);
    }

    [Fact]
    public void Malformed_Output_Does_Not_Throw_And_Fails()
    {
        var result = DismWimInfoParser.Parse("lorem ipsum\nnot a wim\n===", @"C:\x.wim", WindowsImageType.Wim);

        Assert.Equal(WindowsImageMetadataStatus.Failed, result.Status);
        Assert.Empty(result.Editions);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [Fact]
    public void Empty_Output_Fails_Gracefully()
    {
        var result = DismWimInfoParser.Parse("", @"C:\x.wim", WindowsImageType.Wim);

        Assert.Equal(WindowsImageMetadataStatus.Failed, result.Status);
        Assert.Empty(result.Editions);
    }

    [Fact]
    public void Unknown_Fields_Are_Ignored()
    {
        const string output = @"
Details for image : C:\install.wim

Index : 1
Name : Windows 11 Home
Architecture : x64
Version : 10.0.26100.1742
FutureField : some new value DISM added
AnotherUnknown : 42
Installation : Client
Languages :
        en-US (Default)
";
        var result = DismWimInfoParser.Parse(output, @"C:\install.wim", WindowsImageType.Wim);

        Assert.Equal(WindowsImageMetadataStatus.Completed, result.Status);
        Assert.Single(result.Editions);
        Assert.Equal("Windows 11 Home", result.Editions[0].Name);
        Assert.Equal("x64", result.Editions[0].Architecture);
    }

    [Fact]
    public void Reordered_Fields_Still_Parse()
    {
        const string output = @"
Details for image : C:\install.wim

Index : 1
Version : 10.0.26100.1742
Name : Windows 11 Home
Installation : Client
Languages :
        en-US (Default)
Architecture : x64
";
        var result = DismWimInfoParser.Parse(output, @"C:\install.wim", WindowsImageType.Wim);

        Assert.Equal(WindowsImageMetadataStatus.Completed, result.Status);
        Assert.Equal("Windows 11 Home", result.Editions[0].Name);
        Assert.Equal("x64", result.Editions[0].Architecture);
        Assert.Equal("26100", result.Editions[0].Build);
    }

    [Fact]
    public void Mixed_Architecture_Yields_Null_TopLevel()
    {
        const string output = @"
Details for image : C:\install.wim

Index : 1
Name : Windows 11 Home
Architecture : x64
Version : 10.0.26100.1742
Installation : Client

Index : 2
Name : Windows 11 Arm Home
Architecture : ARM64
Version : 10.0.26100.1742
Installation : Client
";
        var result = DismWimInfoParser.Parse(output, @"C:\install.wim", WindowsImageType.Wim);

        Assert.Equal(2, result.Editions.Count);
        // Architectures disagree -> top-level must be null (UI shows "Mixed"),
        // never a guessed first value.
        Assert.Null(result.Architecture);
        Assert.Equal("26100", result.Build); // versions agree
    }
}
