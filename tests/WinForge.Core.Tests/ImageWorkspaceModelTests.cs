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
}
