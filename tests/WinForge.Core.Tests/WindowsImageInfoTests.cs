using WinForge.Core.Models;
using Xunit;

namespace WinForge.Core.Tests;

public class WindowsImageInfoTests
{
    [Fact]
    public void WindowsImageInfo_Defaults_AreReasonable()
    {
        var info = new WindowsImageInfo();

        Assert.Null(info.SourcePath);
        Assert.Equal(WindowsImageType.Unknown, info.ImageType);
        Assert.Null(info.Architecture);
        Assert.Null(info.Version);
        Assert.Null(info.Build);
        Assert.NotNull(info.Editions);
        Assert.Empty(info.Editions);
        Assert.Equal(0, info.Size);
    }
}
