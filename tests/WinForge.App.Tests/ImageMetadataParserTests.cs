using System.Linq;
using WinForge.Core.Models;
using WinForge.Infrastructure.ImageMetadata;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Pure parsing of <c>dism /English /Get-WimInfo</c> output, split into the two
/// real DISM stages (see Step 2.2 fix):
/// - <see cref="DismWimInfoParser.ParseImageList"/> parses the *enumeration*
///   query (no /Index) — only Index / Name / Description are reliable there.
/// - <see cref="DismWimInfoParser.ParseImageDetails"/> parses a single per-index
///   *detail* query (/Index:n) — Architecture / Version / Edition Id /
///   Installation / Languages / Default Language.
///
/// No process, no real image, no Windows required — the parsers are
/// deterministic functions of the captured text. These tests guard field
/// extraction, tolerance for unknown / reordered fields, and the separation of
/// the two stages (the list parser must NOT read detail-only fields even if a
/// future DISM build prints them during enumeration).
/// </summary>
public class ImageMetadataParserTests
{
    // ---- Realistic enumeration (no /Index) output ----------------------------

    private const string EnumHome = @"
Deployment Image Servicing and Management tool
Version: 10.0.26100.1

Details for image : C:\sources\install.wim

Index : 1
Name : Windows 11 Home
Description : Windows 11 Home
Size : 15,297,491,328 bytes
";

    private const string EnumHomeAndPro = @"
Details for image : C:\sources\install.wim

Index : 1
Name : Windows 11 Home
Description : Windows 11 Home
Size : 15,297,491,328 bytes

Index : 2
Name : Windows 11 Pro
Description : Windows 11 Pro
Size : 15,314,268,160 bytes
";

    // Non-sequential indexes (DISM does not guarantee 1..N contiguity).
    private const string EnumNonSequential = @"
Details for image : C:\sources\install.wim

Index : 1
Name : Windows 11 Home
Description : Windows 11 Home
Size : 15,297,491,328 bytes

Index : 6
Name : Windows 11 Pro
Description : Windows 11 Pro
Size : 15,314,268,160 bytes
";

    // Enumeration that incidentally carries a detail field (some DISM builds).
    // The list parser MUST ignore it — detail comes only from the detail query.
    private const string EnumWithStrayArchitecture = @"
Index : 1
Name : Windows 11 Home
Description : Windows 11 Home
Size : 15,297,491,328 bytes
Architecture : x64
";

    // ---- Realistic per-index detail (/Index:n) output ------------------------

    private const string DetailHome = @"
Deployment Image Servicing and Management tool
Version: 10.0.26100.1

Details for image : C:\sources\install.wim

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

    private const string DetailMultiLanguage = @"
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
        fr-FR
        de-DE
Default Language : en-US
";

    private const string DetailReorderedUnknown = @"
Index : 2
Installation : Client
Version : 10.0.26100.1742
Architecture : x64
Name : Windows 11 Pro
FutureField : whatever DISM added
AnotherUnknown : 99
Edition : Professional
Edition Id :
Languages :
        en-US (Default)
Default Language : en-US
";

    [Fact]
    public void ParseImageList_Reads_Index_Name_Description_Only() // Req 1
    {
        var list = DismWimInfoParser.ParseImageList(EnumHome);

        var ed = Assert.Single(list);
        Assert.Equal(1, ed.Index);
        Assert.Equal("Windows 11 Home", ed.Name);
        Assert.Equal("Windows 11 Home", ed.Description);
        // Detail-only fields are NOT present at enumeration time.
        Assert.Null(ed.Architecture);
        Assert.Null(ed.Version);
        Assert.Null(ed.Build);
        Assert.Empty(ed.Languages);
        Assert.Equal(WindowsEditionDetailStatus.NotQueried, ed.DetailStatus);
    }

    [Fact]
    public void ParseImageList_Enumerates_Home_And_Pro() // Req 2
    {
        var list = DismWimInfoParser.ParseImageList(EnumHomeAndPro);

        Assert.Equal(2, list.Count);
        Assert.Equal(1, list[0].Index);
        Assert.Equal("Windows 11 Home", list[0].Name);
        Assert.Equal(2, list[1].Index);
        Assert.Equal("Windows 11 Pro", list[1].Name);
    }

    [Fact]
    public void ParseImageList_Handles_NonSequential_Indexes() // Req 3
    {
        var list = DismWimInfoParser.ParseImageList(EnumNonSequential);

        Assert.Equal(2, list.Count);
        Assert.Equal(1, list[0].Index);
        Assert.Equal(6, list[1].Index);
        Assert.Equal("Windows 11 Pro", list[1].Name);
    }

    [Fact]
    public void ParseImageList_Ignores_Detail_Fields_Even_If_Present() // separation proof
    {
        var list = DismWimInfoParser.ParseImageList(EnumWithStrayArchitecture);

        var ed = Assert.Single(list);
        Assert.Equal(1, ed.Index);
        Assert.Equal("Windows 11 Home", ed.Name);
        // The stray Architecture line in the enumeration output must NOT leak.
        Assert.Null(ed.Architecture);
    }

    [Fact]
    public void ParseImageDetails_Reads_Full_Single_Index() // Req 4, 6, 7, 8, 9
    {
        var ed = DismWimInfoParser.ParseImageDetails(DetailHome);

        Assert.NotNull(ed);
        Assert.Equal(1, ed!.Index);
        Assert.Equal("Windows 11 Home", ed.Name);
        Assert.Equal("x64", ed.Architecture);
        Assert.Equal("10.0.26100.1742", ed.Version);
        Assert.Equal("26100", ed.Build); // 3rd dot segment, genuinely present
        Assert.Equal("Client", ed.InstallationType);
        Assert.Equal("en-US", Assert.Single(ed.Languages));
        Assert.Equal("en-US", ed.DefaultLanguage);
    }

    [Fact]
    public void ParseImageDetails_Parses_Multiple_Languages() // Req 10
    {
        var ed = DismWimInfoParser.ParseImageDetails(DetailMultiLanguage);

        Assert.NotNull(ed);
        Assert.Equal(new[] { "en-US", "fr-FR", "de-DE" }, ed!.Languages);
        Assert.Equal("en-US", ed.DefaultLanguage);
    }

    [Fact]
    public void ParseImageDetails_Ignores_Unknown_Fields() // Req 11
    {
        var ed = DismWimInfoParser.ParseImageDetails(DetailReorderedUnknown);

        Assert.NotNull(ed);
        Assert.Equal(2, ed!.Index);
        Assert.Equal("Windows 11 Pro", ed.Name);
        Assert.Equal("x64", ed.Architecture);
        Assert.Equal("26100", ed.Build);
    }

    [Fact]
    public void ParseImageDetails_Tolerates_Reordered_Fields() // Req 12
    {
        var ed = DismWimInfoParser.ParseImageDetails(DetailReorderedUnknown);

        Assert.NotNull(ed);
        Assert.Equal("Windows 11 Pro", ed!.Name);
        Assert.Equal("x64", ed.Architecture);
        Assert.Equal("26100", ed.Build);
        Assert.Equal("Client", ed.InstallationType);
    }

    [Fact]
    public void ParseImageList_Empty_Output_Returns_Empty() // supports Req 13
    {
        Assert.Empty(DismWimInfoParser.ParseImageList(""));
        Assert.Empty(DismWimInfoParser.ParseImageList("lorem ipsum\nnot a wim"));
    }

    [Fact]
    public void ParseImageDetails_Empty_Output_Returns_Null()
    {
        Assert.Null(DismWimInfoParser.ParseImageDetails(""));
        Assert.Null(DismWimInfoParser.ParseImageDetails("no indexes here"));
    }
}
