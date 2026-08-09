using WinForge.Core.Models;
using Xunit;

namespace WinForge.Core.Tests;

/// <summary>
/// Defaults and basic shape of the Step 2.2 metadata result model. The model
/// keeps raw, nullable values; the UI decides "Not detected" vs "Mixed".
/// </summary>
public class WindowsImageMetadataResultTests
{
    [Fact]
    public void WindowsImageMetadataResult_Defaults_AreReasonable()
    {
        var result = new WindowsImageMetadataResult();

        Assert.Null(result.ImagePath);
        Assert.Equal(WindowsImageType.Unknown, result.ImageType);
        Assert.Equal(WindowsImageMetadataStatus.NotInspected, result.Status);
        Assert.Null(result.Version);
        Assert.Null(result.Build);
        Assert.Null(result.Architecture);
        Assert.Null(result.Languages);
        Assert.NotNull(result.Editions);
        Assert.Empty(result.Editions);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void Edition_Keeps_Nullable_Fields_When_Not_Reported()
    {
        var edition = new WindowsEditionInfo { Index = 1, Name = "Windows 11 Home" };

        Assert.Equal(1, edition.Index);
        Assert.Equal("Windows 11 Home", edition.Name);
        Assert.Null(edition.Description);
        Assert.Null(edition.Architecture);
        Assert.Null(edition.Version);
        Assert.Null(edition.Build);
        Assert.Null(edition.EditionId);
        Assert.Null(edition.InstallationType);
        Assert.NotNull(edition.Languages);
        Assert.Empty(edition.Languages);
        Assert.Null(edition.DefaultLanguage);
        Assert.Equal(WindowsEditionDetailStatus.NotQueried, edition.DetailStatus);
        Assert.Null(edition.DetailErrorMessage);
    }
}
