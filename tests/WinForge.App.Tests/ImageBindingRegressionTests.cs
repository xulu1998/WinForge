using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using WinForge.App.ViewModels;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Regression guard for the Phase 1 WPF binding bug where the read-only
/// <see cref="ImageViewModel.FileDisplay"/> was bound to a <see cref="System.Windows.Controls.TextBox"/>
/// <c>Text</c> property. <c>TextBox.Text</c> defaults to <c>TwoWay</c>, so WPF tried to
/// write back into a getter-only property and threw at runtime. We assert both halves of
/// the fix without needing a display device: (1) the property is genuinely read-only, and
/// (2) the XAML explicitly declares <c>Mode=OneWay</c>.
/// </summary>
public sealed class ImageBindingRegressionTests
{
    [Fact]
    public void FileDisplay_IsReadOnly_And_BoundOneWay_InXaml()
    {
        // 1. The property must remain read-only. Adding a setter would reintroduce the
        //    TwoWay write-back crash, so the test fails loudly if that ever happens.
        var property = typeof(ImageViewModel).GetProperty("FileDisplay");
        Assert.NotNull(property);
        Assert.True(
            property!.GetSetMethod(nonPublic: true) is null,
            "ImageViewModel.FileDisplay must stay read-only; a setter reintroduces the TwoWay binding crash.");

        // 2. The Image page XAML must bind it OneWay, not the default TwoWay.
        var xamlPath = FindImageViewXaml();
        Assert.True(
            File.Exists(xamlPath),
            $"Could not locate ImageView.xaml (searched up from {Assembly.GetExecutingAssembly().Location}).");

        var document = XDocument.Load(xamlPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var textBox = document
            .Descendants(presentation + "TextBox")
            .FirstOrDefault(tb => (tb.Attribute("Text")?.Value ?? string.Empty).Contains("FileDisplay"));

        Assert.NotNull(textBox);

        var textBinding = textBox!.Attribute("Text")!.Value;
        Assert.True(
            textBinding.Contains("FileDisplay"),
            "Expected the FileDisplay TextBox to bind the FileDisplay property.");
        Assert.True(
            textBinding.Contains("Mode=OneWay"),
            "Image page TextBox must bind FileDisplay with Mode=OneWay; the default TwoWay mode crashes on a read-only property.");
    }

    private static string FindImageViewXaml()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, "src", "WinForge.App", "Views", "ImageView.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return string.Empty;
    }
}
