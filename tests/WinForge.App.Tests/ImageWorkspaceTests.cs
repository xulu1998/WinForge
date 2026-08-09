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
/// Step 3.1 — durable WIM workspace & image selection foundation.
///
/// Covers: building a durable workspace from a Phase 2 inspection + selected
/// edition (WIM and ESD), that the source/relative paths never reference a
/// temporary mounted drive, that essential identifiers are preserved, that the
/// workspace is not ready / invalid under the documented failure modes, that
/// changing editions and re-selecting an ISO keep the workspace correct, and
/// that the read-only IWimService validates and resolves a selected context.
/// </summary>
public class ImageWorkspaceTests
{
    private static readonly string IsoPath = @"F:\ISOs\Win11_25H2_Chinese_Simplified_x64_v2.iso";

    private static IsoInspectionResult BuildInspection(InstallImageType imageType, params WindowsEditionInfo[] editions)
    {
        var meta = new WindowsImageMetadataResult
        {
            ImageType = imageType == InstallImageType.Esd ? WindowsImageType.Esd : WindowsImageType.Wim,
            Status = WindowsImageMetadataStatus.Completed,
        };
        foreach (var e in editions)
        {
            meta.Editions.Add(e);
        }

        return new IsoInspectionResult
        {
            IsoPath = IsoPath,
            FileName = "Win11.iso",
            Exists = true,
            ExtensionValid = true,
            IsReadable = true,
            HasSourcesDirectory = true,
            HasBootDirectory = true,
            HasInstallWim = imageType == InstallImageType.Wim,
            HasInstallEsd = imageType == InstallImageType.Esd,
            InstallImageType = imageType,
            DetectedType = IsoDetectedType.WindowsIsoCandidate,
            Status = IsoInspectionStatus.Completed,
            ImageMetadata = meta
        };
    }

    private static WindowsEditionInfo Edition(int index, string name, string? build = "26200") => new()
    {
        Index = index,
        Name = name,
        Architecture = "x64",
        Version = "10.0.26200.0",
        Build = build,
        Languages = { "zh-CN" }
    };

    // 1–9: building a durable workspace from a valid inspection + selected edition.

    [Fact]
    public void BuildWorkspace_ValidWim_ReturnsReadyWorkspace()
    {
        var inspection = BuildInspection(InstallImageType.Wim,
            Edition(1, "Windows 11 家庭版"),
            Edition(2, "Windows 11 专业版"));
        var factory = new ImageWorkspaceFactory();

        var result = factory.BuildWorkspace(inspection, inspection.ImageMetadata!.Editions[1]);

        Assert.True(result.IsReady);
        Assert.NotNull(result.Workspace);
        var ws = result.Workspace!;
        Assert.Equal(2, ws.SelectedIndex);
        Assert.Equal("Windows 11 专业版", ws.SelectedEditionName);
        Assert.Equal("x64", ws.Architecture);
        Assert.Equal("26200", ws.Build);
        Assert.Equal("10.0.26200.0", ws.Version);
        Assert.Equal("zh-CN", Assert.Single(ws.Languages));
    }

    [Fact]
    public void BuildWorkspace_ValidEsd_ReturnsReadyWorkspace_WithEsdRelativePath()
    {
        var inspection = BuildInspection(InstallImageType.Esd,
            Edition(1, "Windows 11 Home"),
            Edition(4, "Windows 11 Pro"));
        var factory = new ImageWorkspaceFactory();

        var result = factory.BuildWorkspace(inspection, inspection.ImageMetadata!.Editions[1]);

        Assert.True(result.IsReady);
        Assert.Equal(WindowsImageType.Esd, result.Workspace!.ImageType);
    }

    [Fact]
    public void BuildWorkspace_SourceIsOriginalIsoPath_NotMountDrive()
    {
        var inspection = BuildInspection(InstallImageType.Wim, Edition(1, "Windows 11 家庭版"));
        var factory = new ImageWorkspaceFactory();

        var ws = factory.BuildWorkspace(inspection, inspection.ImageMetadata!.Editions[0]).Workspace!;

        // The durable source is the original ISO path, never a temporary G:\ mount.
        Assert.Equal(IsoPath, ws.SourceIsoPath);
        Assert.DoesNotContain("G:", ws.SourceIsoPath!);
    }

    [Fact]
    public void BuildWorkspace_RelativePath_IsSourcesInstallWim()
    {
        var inspection = BuildInspection(InstallImageType.Wim, Edition(1, "Windows 11 家庭版"));
        var factory = new ImageWorkspaceFactory();

        Assert.Equal("sources\\install.wim",
            factory.BuildWorkspace(inspection, inspection.ImageMetadata!.Editions[0]).Workspace!.ImageRelativePath);
    }

    [Fact]
    public void BuildWorkspace_EsdRelativePath_IsSourcesInstallEsd()
    {
        var inspection = BuildInspection(InstallImageType.Esd, Edition(1, "Windows 11 Home"));
        var factory = new ImageWorkspaceFactory();

        Assert.Equal("sources\\install.esd",
            factory.BuildWorkspace(inspection, inspection.ImageMetadata!.Editions[0]).Workspace!.ImageRelativePath);
    }

    [Fact]
    public void BuildWorkspace_Never_Persists_TemporaryMountDriveRoot()
    {
        // Even if the inspection's ISO path were a mounted drive (it never is in
        // Phase 2 — IsoPath is the original ISO), the relative path is derived,
        // not copied. Assert the built workspace holds no drive letter in the
        // relative path.
        var inspection = BuildInspection(InstallImageType.Wim, Edition(1, "Windows 11 家庭版"));
        var factory = new ImageWorkspaceFactory();

        var ws = factory.BuildWorkspace(inspection, inspection.ImageMetadata!.Editions[0]).Workspace!;

        Assert.False(ws.ImageRelativePath!.StartsWith("G:", System.StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("sources", ws.ImageRelativePath, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildWorkspace_Preserves_Localized_EditionName()
    {
        var inspection = BuildInspection(InstallImageType.Wim,
            Edition(1, "Windows 11 家庭版"),
            Edition(6, "Windows 11 专业工作站版"));
        var factory = new ImageWorkspaceFactory();

        var ws = factory.BuildWorkspace(inspection, inspection.ImageMetadata!.Editions[1]).Workspace!;

        Assert.Equal(6, ws.SelectedIndex);
        Assert.Equal("Windows 11 专业工作站版", ws.SelectedEditionName);
    }

    [Fact]
    public void BuildWorkspace_Preserves_Architecture_Version_Build()
    {
        var inspection = BuildInspection(InstallImageType.Wim,
            Edition(1, "Windows 11 家庭版", build: "26200"));
        var factory = new ImageWorkspaceFactory();

        var ws = factory.BuildWorkspace(inspection, inspection.ImageMetadata!.Editions[0]).Workspace!;

        Assert.Equal("x64", ws.Architecture);
        Assert.Equal("10.0.26200.0", ws.Version);
        Assert.Equal("26200", ws.Build);
    }

    // 10–12: workspace not ready / invalid.

    [Fact]
    public void BuildWorkspace_NoSelectedEdition_NotReady()
    {
        var inspection = BuildInspection(InstallImageType.Wim, Edition(1, "Windows 11 家庭版"));
        var factory = new ImageWorkspaceFactory();

        var result = factory.BuildWorkspace(inspection, null);

        Assert.Equal(ImageWorkspaceStatus.NotReady, result.Status);
        Assert.Null(result.Workspace);
    }

    [Fact]
    public void BuildWorkspace_FailedMetadata_NotReady()
    {
        var inspection = BuildInspection(InstallImageType.Wim, Edition(1, "Windows 11 家庭版"));
        inspection.ImageMetadata!.Status = WindowsImageMetadataStatus.Failed;
        var factory = new ImageWorkspaceFactory();

        var result = factory.BuildWorkspace(inspection, inspection.ImageMetadata.Editions[0]);

        Assert.Equal(ImageWorkspaceStatus.NotReady, result.Status);
        Assert.Null(result.Workspace);
    }

    [Fact]
    public void BuildWorkspace_SelectedIndexNotInMetadata_Invalid()
    {
        var inspection = BuildInspection(InstallImageType.Wim, Edition(1, "Windows 11 家庭版"));
        var factory = new ImageWorkspaceFactory();
        var stale = Edition(99, "Ghost edition");

        var result = factory.BuildWorkspace(inspection, stale);

        Assert.Equal(ImageWorkspaceStatus.Invalid, result.Status);
        Assert.Null(result.Workspace);
    }

    [Fact]
    public void BuildWorkspace_MissingIsoPath_NotReady()
    {
        var inspection = BuildInspection(InstallImageType.Wim, Edition(1, "Windows 11 家庭版"));
        inspection.IsoPath = null;
        var factory = new ImageWorkspaceFactory();

        Assert.Equal(ImageWorkspaceStatus.NotReady, factory.BuildWorkspace(inspection, inspection.ImageMetadata!.Editions[0]).Status);
    }

    [Fact]
    public void BuildWorkspace_UnknownImageType_NotReady()
    {
        var inspection = BuildInspection(InstallImageType.Unknown, Edition(1, "Windows 11 家庭版"));
        var factory = new ImageWorkspaceFactory();

        Assert.Equal(ImageWorkspaceStatus.NotReady, factory.BuildWorkspace(inspection, inspection.ImageMetadata!.Editions[0]).Status);
    }

    // IWimService (read-only) behaviour.

    [Fact]
    public void WimService_ValidateWorkspace_ReadyWorkspace_ReturnsReady()
    {
        var inspection = BuildInspection(InstallImageType.Wim, Edition(2, "Windows 11 专业版"));
        var ws = new ImageWorkspaceFactory().BuildWorkspace(inspection, inspection.ImageMetadata!.Editions[0]).Workspace!;
        var service = new WimService();

        Assert.Equal(ImageWorkspaceStatus.Ready, service.ValidateWorkspace(ws));
    }

    [Fact]
    public void WimService_ValidateWorkspace_MissingSource_Invalid()
    {
        var service = new WimService();
        var ws = new ImageWorkspace { SourceIsoPath = null, ImageRelativePath = "sources\\install.wim", ImageType = WindowsImageType.Wim, SelectedIndex = 1 };
        Assert.Equal(ImageWorkspaceStatus.Invalid, service.ValidateWorkspace(ws));
    }

    [Fact]
    public void WimService_ValidateWorkspace_BadRelativePath_Invalid()
    {
        var service = new WimService();
        var ws = new ImageWorkspace { SourceIsoPath = IsoPath, ImageRelativePath = "elsewhere\\x.wim", ImageType = WindowsImageType.Wim, SelectedIndex = 1 };
        Assert.Equal(ImageWorkspaceStatus.Invalid, service.ValidateWorkspace(ws));
    }

    [Fact]
    public void WimService_ValidateWorkspace_UnknownImageType_Invalid()
    {
        var service = new WimService();
        var ws = new ImageWorkspace { SourceIsoPath = IsoPath, ImageRelativePath = "sources\\install.wim", ImageType = WindowsImageType.Unknown, SelectedIndex = 1 };
        Assert.Equal(ImageWorkspaceStatus.Invalid, service.ValidateWorkspace(ws));
    }

    [Fact]
    public void WimService_ValidateWorkspace_NoSelectedIndex_NotReady()
    {
        var service = new WimService();
        var ws = new ImageWorkspace { SourceIsoPath = IsoPath, ImageRelativePath = "sources\\install.wim", ImageType = WindowsImageType.Wim, SelectedIndex = 0 };
        Assert.Equal(ImageWorkspaceStatus.NotReady, service.ValidateWorkspace(ws));
    }

    [Fact]
    public void WimService_ResolveSelectedImage_Ready_ReturnsContext()
    {
        var inspection = BuildInspection(InstallImageType.Wim, Edition(2, "Windows 11 专业版"));
        var ws = new ImageWorkspaceFactory().BuildWorkspace(inspection, inspection.ImageMetadata!.Editions[0]).Workspace!;
        var service = new WimService();

        var ctx = service.ResolveSelectedImage(ws);

        Assert.NotNull(ctx);
        Assert.Equal(IsoPath, ctx!.SourceIsoPath);
        Assert.Equal("sources\\install.wim", ctx.ImageRelativePath);
        Assert.Equal(WindowsImageType.Wim, ctx.ImageType);
        Assert.Equal(2, ctx.SelectedIndex);
    }

    [Fact]
    public void WimService_ResolveSelectedImage_NotReady_ReturnsNull()
    {
        var service = new WimService();
        var ws = new ImageWorkspace { SourceIsoPath = IsoPath, ImageRelativePath = "sources\\install.wim", ImageType = WindowsImageType.Wim, SelectedIndex = 0 };
        Assert.Null(service.ResolveSelectedImage(ws));
    }

    // ViewModel integration: durable workspace reacts to selection and ISO change.

    private static ImageViewModel BuildVm(AppState state, IIsoInspectionService inspection)
    {
        return new ImageViewModel(state, new InMemoryLoggerService(), inspection,
            new FakeFilePicker(), new ImageWorkspaceFactory(), new WimService(),
            new FakeImageServicingService());
    }

    [Fact]
    public async Task ViewModel_Selecting_Edition_Creates_DurableWorkspace()
    {
        var inspection = new FakeInspection
        {
            Next = BuildInspection(InstallImageType.Wim,
                Edition(1, "Windows 11 家庭版"),
                Edition(4, "Windows 11 专业版"))
        };
        var state = new AppState { SourceImagePath = IsoPath };
        var vm = BuildVm(state, inspection);

        await vm.InspectCurrentAsync();
        vm.SelectedEdition = vm.Editions[1]; // index 4

        Assert.NotNull(vm.Workspace);
        Assert.Equal("Ready", vm.WorkspaceStatusDisplay);
        Assert.Equal(4, vm.Workspace!.SelectedIndex);
        Assert.Equal("Windows 11 专业版", vm.WorkspaceEditionDisplay);
        Assert.Equal("install.wim", vm.WorkspaceImageDisplay);
        Assert.Equal("26200", vm.WorkspaceBuildDisplay);
        Assert.Equal("Win11_25H2_Chinese_Simplified_x64_v2.iso", vm.WorkspaceSourceDisplay);
        Assert.Same(vm.Workspace, state.CurrentImageWorkspace);
    }

    [Fact]
    public async Task ViewModel_Changing_Edition_Updates_SelectedIndex()
    {
        var inspection = new FakeInspection
        {
            Next = BuildInspection(InstallImageType.Wim,
                Edition(1, "Windows 11 家庭版"),
                Edition(4, "Windows 11 专业版"),
                Edition(6, "Windows 11 专业工作站版"))
        };
        var state = new AppState { SourceImagePath = IsoPath };
        var vm = BuildVm(state, inspection);

        await vm.InspectCurrentAsync();
        vm.SelectedEdition = vm.Editions[1]; // index 4
        Assert.Equal(4, vm.Workspace!.SelectedIndex);

        vm.SelectedEdition = vm.Editions[2]; // index 6
        Assert.Equal(6, vm.Workspace!.SelectedIndex);
        Assert.Equal("Windows 11 专业工作站版", vm.WorkspaceEditionDisplay);
    }

    [Fact]
    public async Task ViewModel_Selecting_NewIso_Clears_PreviousWorkspace()
    {
        var isoA = BuildInspection(InstallImageType.Wim, Edition(1, "Windows 11 家庭版"), Edition(4, "Windows 11 专业版"));
        var isoB = BuildInspection(InstallImageType.Wim, Edition(1, "Windows 11 Home"));
        isoB.IsoPath = @"F:\ISOs\Other.iso";

        var inspection = new FakeInspection { Next = isoA };
        var state = new AppState { SourceImagePath = IsoPath };
        var vm = BuildVm(state, inspection);

        await vm.InspectCurrentAsync();
        vm.SelectedEdition = vm.Editions[1];
        Assert.NotNull(vm.Workspace);

        // Selecting a different ISO: inspect the new source. A fresh inspection
        // must clear any prior workspace before the new one is ready, so no stale
        // index from the previous ISO survives.
        inspection.Next = isoB;
        state.SourceImagePath = @"F:\ISOs\Other.iso";
        await vm.InspectCurrentAsync();

        Assert.Null(vm.Workspace);
        Assert.Null(state.CurrentImageWorkspace);
        Assert.Null(state.SelectedEdition);
    }

    [Fact]
    public async Task ViewModel_Failed_Inspection_Leaves_No_StaleWorkspace()
    {
        var inspection = new FakeInspection { Next = IsoInspectionResult.Failed(IsoPath, "Mount failed.") };
        var state = new AppState { SourceImagePath = IsoPath };
        var vm = BuildVm(state, inspection);

        await vm.InspectCurrentAsync();

        Assert.Null(vm.Workspace);
        Assert.Equal("Select an edition", vm.WorkspaceStatusDisplay);
    }

    [Fact]
    public async Task Home_SelectedEdition_Remains_Consistent_With_Workspace()
    {
        var inspection = new FakeInspection
        {
            Next = BuildInspection(InstallImageType.Wim,
                Edition(1, "Windows 11 家庭版"),
                Edition(4, "Windows 11 专业版"))
        };
        var state = new AppState { SourceImagePath = IsoPath };
        var vm = BuildVm(state, inspection);
        var home = new HomeViewModel(state, new FakeNavigationService());

        await vm.InspectCurrentAsync();
        vm.SelectedEdition = vm.Editions[1];

        Assert.Equal("Windows 11 专业版", home.EditionDisplay);
        Assert.NotNull(vm.Workspace);
    }

    private sealed class FakeFilePicker : IFilePicker
    {
        public string? NextPath { get; set; }
        public string? PickIsoFile() => NextPath;
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

    private sealed class FakeInspection : IIsoInspectionService
    {
        public IsoInspectionResult? Next { get; set; }
        public Task<IsoInspectionResult> InspectAsync(string isoPath, CancellationToken cancellationToken = default)
            => Task.FromResult(Next ?? IsoInspectionResult.NotInspected(isoPath));
    }
}
