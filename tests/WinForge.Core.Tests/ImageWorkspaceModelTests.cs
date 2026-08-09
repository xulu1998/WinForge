using WinForge.Core.Models;
using Xunit;

namespace WinForge.Core.Tests;

/// <summary>
/// Durable workspace model behaviour. The relative install-image path must be
/// normalized consistently and must never contain a temporary mounted drive
/// letter.
/// </summary>
public class ImageWorkspaceModelTests
{
    [Theory]
    [InlineData("sources/install.wim", "sources\\install.wim")]
    [InlineData("sources\\install.wim", "sources\\install.wim")]
    [InlineData("sources//install.esd", "sources\\install.esd")]
    [InlineData("\\sources\\install.wim\\", "sources\\install.wim")]
    public void NormalizeRelativePath_Collapses_Separators(string input, string expected)
    {
        Assert.Equal(expected, ImageWorkspace.NormalizeRelativePath(input));
    }

    [Fact]
    public void NormalizeRelativePath_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ImageWorkspace.NormalizeRelativePath(null));
        Assert.Equal(string.Empty, ImageWorkspace.NormalizeRelativePath("   "));
    }

    [Fact]
    public void ImageWorkspace_RelativePath_Retains_FullNormalizedPath()
    {
        // Case 1: the durable model stores the FULL normalized relative path
        // (sources\install.wim / sources\install.esd), never just the filename
        // and never a temporary mounted-drive root such as D:\sources\install.wim.
        var workspace = new ImageWorkspace
        {
            ImageRelativePath = ImageWorkspace.NormalizeRelativePath("sources/install.wim")
        };

        Assert.Equal("sources\\install.wim", workspace.ImageRelativePath);
        Assert.DoesNotContain(":", workspace.ImageRelativePath);
        Assert.StartsWith("sources", workspace.ImageRelativePath);

        // The UI is allowed to present only the filename while the workspace
        // retains the full relative path.
        Assert.Equal("install.wim", System.IO.Path.GetFileName(workspace.ImageRelativePath));
    }
}
