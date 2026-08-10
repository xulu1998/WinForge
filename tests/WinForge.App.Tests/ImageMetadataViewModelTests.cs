using System.Threading;
using System.Threading.Tasks;
using WinForge.App.Services;
using WinForge.App.ViewModels;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.WimEngine;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Edition selection behaviour (Step 2.2). Selecting an edition on the Image
/// page must persist to <see cref="IAppState.SelectedEdition"/>, and the Home
/// page must reflect that selection. Selection is status-only — no image
/// mounting/extraction occurs.
/// </summary>
public class ImageMetadataViewModelTests
{
    private static IsoInspectionResult WithMetadata()
    {
        var meta = new WindowsImageMetadataResult
        {
            ImageType = WindowsImageType.Wim,
            Status = WindowsImageMetadataStatus.Completed,
            Editions =
            {
                new WindowsEditionInfo { Index = 1, Name = "Windows 11 Home", Architecture = "x64" },
                new WindowsEditionInfo { Index = 2, Name = "Windows 11 Pro", Architecture = "x64" }
            }
        };

        return new IsoInspectionResult
        {
            IsoPath = @"C:\images\win.iso",
            FileName = "win.iso",
            Exists = true,
            ExtensionValid = true,
            IsReadable = true,
            HasSourcesDirectory = true,
            HasBootDirectory = true,
            HasInstallWim = true,
            InstallImageType = InstallImageType.Wim,
            DetectedType = IsoDetectedType.WindowsIsoCandidate,
            Status = IsoInspectionStatus.Completed,
            ImageMetadata = meta
        };
    }

    [Fact]
    public async Task Selecting_Edition_Updates_AppState()
    {
        var path = @"C:\images\win.iso";
        var state = new AppState();
        state.SourceImagePath = path;
        var inspection = new FakeInspection { Next = WithMetadata() };
        var vm = new ImageViewModel(
            state,
            new InMemoryLoggerService(),
            inspection,
            new FakeFilePicker(),
            new ImageWorkspaceFactory(),
            new WimService(),
            new FakeImageServicingService());

        await vm.InspectCurrentAsync();

        // A fresh inspection clears any prior selection.
        Assert.Null(state.SelectedEdition);
        Assert.Equal(2, vm.Editions.Count);

        var pro = vm.Editions[1];
        vm.SelectedEdition = pro;

        Assert.Same(pro, state.SelectedEdition);
        Assert.Equal("Windows 11 Pro", state.SelectedEdition!.Name);
    }

    [Fact]
    public void Home_Reflects_Selected_Edition()
    {
        var state = new AppState();
        var home = new HomeViewModel(state, new FakeNavigationService());

        Assert.Equal("Not selected", home.EditionDisplay);

        state.SelectedEdition = new WindowsEditionInfo { Index = 2, Name = "Windows 11 Pro" };
        Assert.Equal("Windows 11 Pro", home.EditionDisplay);
    }

    private sealed class FakeFilePicker : IFilePicker
    {
        public string? NextPath { get; set; }
        public string? PickIsoFile() => NextPath;
        public string? PickFolder() => null;
    }

    private sealed class FakeInspection : IIsoInspectionService
    {
        public IsoInspectionResult? Next { get; set; }
        public Task<IsoInspectionResult> InspectAsync(string isoPath, CancellationToken cancellationToken = default)
            => Task.FromResult(Next ?? IsoInspectionResult.NotInspected(isoPath));
    }

    private sealed class FakeNavigationService : INavigationService
    {
        public PageKey CurrentPage { get; private set; }
        public event System.EventHandler<PageKey>? CurrentPageChanged;
        public void NavigateTo(PageKey page)
        {
            CurrentPage = page;
            CurrentPageChanged?.Invoke(this, page);
        }
    }
}
