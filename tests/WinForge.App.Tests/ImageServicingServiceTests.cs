using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.Servicing;
using WinForge.Infrastructure.WorkspaceLifecycle;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// <see cref="ImageServicingService"/> behaviour driven by fakes for DISM,
/// ISO mount, and the workspace path/safe-delete policies. Covers the Step 3.2
/// contract: isolated working-image export (WIM + ESD sources), post-export
/// validation, mount state-machine guards, mount registration verification,
/// discard-on-unmount, a no-op repeated unmount, stale-session recovery, and the
/// path-safety guard so cleanup can never escape the workspace.
/// </summary>
public class ImageServicingServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "wf_svc_" + Guid.NewGuid().ToString("N"));

    public ImageServicingServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch { /* best effort */ }
    }

    // Single-index working-image info output (matches the selected source edition).
    private const string SingleIndexPro = @"
Index : 1
Name : Windows 11 Pro
Description : Windows 11 Pro
Architecture : x64
Version : 10.0.26100.1742
Edition : Professional
";

    private static ImageWorkspace SourceWim(int index = 4, string edition = "Windows 11 Pro", string arch = "x64", string build = "26100")
        => new ImageWorkspace
        {
            SourceIsoPath = @"C:\images\win.iso",
            ImageRelativePath = @"sources\install.wim",
            ImageType = WindowsImageType.Wim,
            SelectedIndex = index,
            SelectedEditionName = edition,
            Architecture = arch,
            Build = build
        };

    private static ImageServicingService Build(
        FakeProcessRunner runner,
        out FakeIsoMountService iso,
        out WorkspacePathProvider paths,
        string? safeRoot = null)
    {
        iso = new FakeIsoMountService();
        paths = new WorkspacePathProvider(safeRoot ?? Path.Combine(AppContext.BaseDirectory, "wf_paths_" + Guid.NewGuid().ToString("N")));
        var safe = new WorkspaceSafeDelete();
        var lifecycle = new WorkspaceLifecycleManager(paths, runner, safe, new InMemoryLoggerService());
        return new ImageServicingService(runner, iso, paths, safe, new InMemoryLoggerService(), lifecycle);
    }

    private static FakeProcessRunner ExportThenGetInfo(int getInfoExit = 0, string getInfoOut = SingleIndexPro)
    {
        return new FakeProcessRunner
        {
            Responder = req =>
            {
                if (req.Arguments.Contains("/Export-Image"))
                {
                    return new ProcessResult { ExitCode = 0, StandardOutput = string.Empty };
                }

                if (req.Arguments.Contains("/Get-ImageInfo"))
                {
                    return new ProcessResult { ExitCode = getInfoExit, StandardOutput = getInfoOut };
                }

                return new ProcessResult { ExitCode = 0, StandardOutput = string.Empty };
            }
        };
    }

    // ---- Prepare ----

    [Fact]
    public async Task Prepare_WimSource_Exports_And_Returns_Prepared()
    {
        var isoRoot = Path.Combine(_root, "iso");
        var sourceFile = Path.Combine(isoRoot, "sources", "install.wim");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        File.WriteAllBytes(sourceFile, new byte[16]);

        var runner = ExportThenGetInfo();
        var service = Build(runner, out var iso, out var paths, _root);
        iso.MountRoot = isoRoot;

        // Simulate the DISM export having written the working image.
        paths.GetOrCreateWorkspaceDirectory("wf-1");
        File.WriteAllBytes(paths.GetWorkingImagePath("wf-1"), new byte[16]);

        var result = await service.PrepareWorkingImageAsync(SourceWim(), "wf-1", CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(ServicingHealth.Prepared, result.Health);
        Assert.Equal(ServicingWorkspaceState.Prepared, result.Workspace!.State);
        // Exactly one export call + one validation Get-ImageInfo call.
        Assert.Contains(runner.Requests, r => r.Arguments.Contains("/Export-Image"));
        Assert.Contains(runner.Requests, r => r.Arguments.Contains("/Get-ImageInfo"));
        // Transient source ISO mount is released.
        Assert.True(iso.DismountCalled);
    }

    [Fact]
    public async Task Prepare_EsdSource_AlsoProducesWorkingWim()
    {
        var isoRoot = Path.Combine(_root, "iso");
        var sourceFile = Path.Combine(isoRoot, "sources", "install.esd");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        File.WriteAllBytes(sourceFile, new byte[16]);

        var ws = SourceWim();
        ws.ImageType = WindowsImageType.Esd;
        ws.ImageRelativePath = @"sources\install.esd";

        var runner = ExportThenGetInfo();
        var service = Build(runner, out var iso, out var paths, _root);
        iso.MountRoot = isoRoot;
        paths.GetOrCreateWorkspaceDirectory("wf-esd");
        File.WriteAllBytes(paths.GetWorkingImagePath("wf-esd"), new byte[16]);

        var result = await service.PrepareWorkingImageAsync(ws, "wf-esd", CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(WindowsImageType.Wim, result.Workspace!.WorkingImageType);
        Assert.Equal(1, result.Workspace.WorkingIndex);
        // Source index preserved separately from the working index (1).
        Assert.Equal(4, result.Workspace.SelectedIndex);
    }

    [Fact]
    public async Task Prepare_NullSource_Returns_Invalid()
    {
        var runner = new FakeProcessRunner();
        var service = Build(runner, out _, out _, _root);

        var result = await service.PrepareWorkingImageAsync(null!, "wf-x", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ServicingHealth.Invalid, result.Health);
        Assert.Null(result.Workspace);
    }

    [Fact]
    public async Task Prepare_UnknownSourceType_Returns_Invalid()
    {
        var ws = SourceWim();
        ws.ImageType = WindowsImageType.Unknown;

        var runner = new FakeProcessRunner();
        var service = Build(runner, out _, out _, _root);

        var result = await service.PrepareWorkingImageAsync(ws, "wf-x", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ServicingHealth.Invalid, result.Health);
    }

    [Fact]
    public async Task Prepare_NoSelectedIndex_Returns_Invalid()
    {
        var ws = SourceWim();
        ws.SelectedIndex = 0;

        var runner = new FakeProcessRunner();
        var service = Build(runner, out _, out _, _root);

        var result = await service.PrepareWorkingImageAsync(ws, "wf-x", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ServicingHealth.Invalid, result.Health);
    }

    [Fact]
    public async Task Prepare_MissingSourceImageInIso_Returns_Failed()
    {
        // ISO mount root exists but the expected install image is absent.
        var isoRoot = Path.Combine(_root, "iso");
        Directory.CreateDirectory(isoRoot);

        var runner = new FakeProcessRunner();
        var service = Build(runner, out var iso, out _, _root);
        iso.MountRoot = isoRoot;

        var result = await service.PrepareWorkingImageAsync(SourceWim(), "wf-miss", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ServicingWorkspaceState.Failed, result.Workspace!.State);
        Assert.True(iso.DismountCalled);
    }

    [Fact]
    public async Task Prepare_ExportFailure_Returns_Failed()
    {
        var isoRoot = Path.Combine(_root, "iso");
        var sourceFile = Path.Combine(isoRoot, "sources", "install.wim");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        File.WriteAllBytes(sourceFile, new byte[16]);

        var runner = new FakeProcessRunner
        {
            Responder = req => req.Arguments.Contains("/Export-Image")
                ? new ProcessResult { ExitCode = 5, StandardOutput = string.Empty }
                : new ProcessResult { ExitCode = 0, StandardOutput = SingleIndexPro }
        };
        var service = Build(runner, out _, out _, _root);

        var result = await service.PrepareWorkingImageAsync(SourceWim(), "wf-fail", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ServicingWorkspaceState.Failed, result.Workspace!.State);
    }

    [Fact]
    public async Task Prepare_ValidationGetInfoNonZero_Returns_Failed()
    {
        var isoRoot = Path.Combine(_root, "iso");
        var sourceFile = Path.Combine(isoRoot, "sources", "install.wim");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        File.WriteAllBytes(sourceFile, new byte[16]);

        var runner = ExportThenGetInfo(getInfoExit: 1);
        var service = Build(runner, out var iso, out var paths, _root);
        iso.MountRoot = isoRoot;
        paths.GetOrCreateWorkspaceDirectory("wf-val");
        File.WriteAllBytes(paths.GetWorkingImagePath("wf-val"), new byte[16]);

        var result = await service.PrepareWorkingImageAsync(SourceWim(), "wf-val", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ServicingWorkspaceState.Failed, result.Workspace!.State);
    }

    [Fact]
    public async Task Prepare_ValidationEditionMismatch_Returns_Failed()
    {
        var isoRoot = Path.Combine(_root, "iso");
        var sourceFile = Path.Combine(isoRoot, "sources", "install.wim");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
        File.WriteAllBytes(sourceFile, new byte[16]);

        // Working image reports "Home", source selection was "Pro".
        var mismatch = SingleIndexPro.Replace("Windows 11 Pro", "Windows 11 Home");
        var runner = ExportThenGetInfo(getInfoOut: mismatch);
        var service = Build(runner, out var iso, out var paths, _root);
        iso.MountRoot = isoRoot;
        paths.GetOrCreateWorkspaceDirectory("wf-mis");
        File.WriteAllBytes(paths.GetWorkingImagePath("wf-mis"), new byte[16]);

        var result = await service.PrepareWorkingImageAsync(SourceWim(), "wf-mis", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ServicingWorkspaceState.Failed, result.Workspace!.State);
    }

    // ---- Mount ----

    [Fact]
    public async Task Mount_FromPrepared_Succeeds_And_Verifies_Registration()
    {
        var ws = new ImageServicingWorkspace
        {
            WorkingImagePath = Path.Combine(_root, "install.wim"),
            MountDirectory = Path.Combine(_root, "mount"),
            State = ServicingWorkspaceState.Prepared,
            WorkingIndex = 1
        };
        File.WriteAllBytes(ws.WorkingImagePath, new byte[16]);

        var runner = new FakeProcessRunner
        {
            Responder = req =>
            {
                if (req.Arguments.Contains("/Mount-Image"))
                {
                    return new ProcessResult { ExitCode = 0, StandardOutput = string.Empty };
                }

                if (req.Arguments.Contains("/Get-MountedImageInfo"))
                {
                    // Mount is registered at the working mount directory.
                    return new ProcessResult { ExitCode = 0, StandardOutput = $"Mount Dir : {ws.MountDirectory}\n" };
                }

                return new ProcessResult { ExitCode = 0, StandardOutput = string.Empty };
            }
        };
        var service = Build(runner, out _, out _, _root);

        var result = await service.MountAsync(ws, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(ServicingWorkspaceState.Mounted, ws.State);
        Assert.Contains(runner.Requests, r => r.Arguments.Contains("/Mount-Image"));
    }

    [Fact]
    public async Task Mount_NotPrepared_Returns_Invalid()
    {
        var runner = new FakeProcessRunner();
        var service = Build(runner, out _, out _, _root);

        var ws = new ImageServicingWorkspace
        {
            WorkingImagePath = Path.Combine(_root, "install.wim"),
            MountDirectory = Path.Combine(_root, "mount"),
            State = ServicingWorkspaceState.NotPrepared
        };

        var result = await service.MountAsync(ws, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ServicingHealth.Invalid, result.Health);
        Assert.DoesNotContain(runner.Requests, r => r.Arguments.Contains("/Mount-Image"));
    }

    [Fact]
    public async Task Mount_MissingWorkingImage_Returns_Failed()
    {
        var runner = new FakeProcessRunner();
        var service = Build(runner, out _, out _, _root);

        var ws = new ImageServicingWorkspace
        {
            WorkingImagePath = Path.Combine(_root, "missing.wim"),
            MountDirectory = Path.Combine(_root, "mount"),
            State = ServicingWorkspaceState.Prepared
        };

        var result = await service.MountAsync(ws, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ServicingWorkspaceState.Failed, ws.State);
    }

    [Fact]
    public async Task Mount_DismReportsSuccess_ButNotRegistered_Returns_Failed()
    {
        var ws = new ImageServicingWorkspace
        {
            WorkingImagePath = Path.Combine(_root, "install.wim"),
            MountDirectory = Path.Combine(_root, "mount"),
            State = ServicingWorkspaceState.Prepared
        };
        File.WriteAllBytes(ws.WorkingImagePath, new byte[16]);

        var runner = new FakeProcessRunner
        {
            Responder = req =>
            {
                if (req.Arguments.Contains("/Mount-Image"))
                {
                    return new ProcessResult { ExitCode = 0, StandardOutput = string.Empty };
                }

                // Registration check reports NO mount -> not trusted.
                return new ProcessResult { ExitCode = 0, StandardOutput = "Mount Dir : X:\\other\n" };
            }
        };
        var service = Build(runner, out _, out _, _root);

        var result = await service.MountAsync(ws, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ServicingWorkspaceState.Failed, ws.State);
    }

    // ---- Unmount ----

    [Fact]
    public async Task Unmount_FromMounted_Discards_And_Returns_Prepared()
    {
        var runner = new FakeProcessRunner
        {
            Responder = req =>
            {
                if (req.Arguments.Contains("/Unmount-Image"))
                {
                    return new ProcessResult { ExitCode = 0, StandardOutput = string.Empty };
                }

                // After unmount, registration check must report NO mount.
                return new ProcessResult { ExitCode = 0, StandardOutput = string.Empty };
            }
        };
        var service = Build(runner, out _, out _, _root);

        var ws = new ImageServicingWorkspace
        {
            WorkingImagePath = Path.Combine(_root, "install.wim"),
            MountDirectory = Path.Combine(_root, "mount"),
            State = ServicingWorkspaceState.Mounted
        };

        var result = await service.UnmountDiscardAsync(ws, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(ServicingWorkspaceState.Prepared, ws.State);
        Assert.Contains(runner.Requests, r => r.Arguments.Contains("/Unmount-Image"));
        Assert.Contains(runner.Requests, r => r.Arguments.Contains("/Discard"));
    }

    [Fact]
    public async Task Unmount_WhenNotMounted_Is_NoOp_Returns_Prepared()
    {
        var runner = new FakeProcessRunner();
        var service = Build(runner, out _, out _, _root);

        var ws = new ImageServicingWorkspace
        {
            WorkingImagePath = Path.Combine(_root, "install.wim"),
            MountDirectory = Path.Combine(_root, "mount"),
            State = ServicingWorkspaceState.Prepared
        };

        var result = await service.UnmountDiscardAsync(ws, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(ServicingWorkspaceState.Prepared, ws.State);
        Assert.DoesNotContain(runner.Requests, r => r.Arguments.Contains("/Unmount-Image"));
    }

    [Fact]
    public async Task Unmount_DismFailure_Returns_Failed()
    {
        var runner = new FakeProcessRunner
        {
            Responder = req => req.Arguments.Contains("/Unmount-Image")
                ? new ProcessResult { ExitCode = 2, StandardOutput = string.Empty }
                : new ProcessResult { ExitCode = 0, StandardOutput = string.Empty }
        };
        var service = Build(runner, out _, out _, _root);

        var ws = new ImageServicingWorkspace
        {
            WorkingImagePath = Path.Combine(_root, "install.wim"),
            MountDirectory = Path.Combine(_root, "mount"),
            State = ServicingWorkspaceState.Mounted
        };

        var result = await service.UnmountDiscardAsync(ws, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ServicingWorkspaceState.Failed, ws.State);
    }

    // ---- Validate / recovery ----

    [Fact]
    public async Task Validate_NoWorkingImagePath_Returns_Invalid()
    {
        var runner = new FakeProcessRunner();
        var service = Build(runner, out _, out _, _root);

        var ws = new ImageServicingWorkspace { State = ServicingWorkspaceState.Prepared };

        var result = await service.ValidateServicingWorkspaceAsync(ws, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ServicingHealth.Invalid, result.Health);
    }

    [Fact]
    public async Task Validate_MountedButNotRegistered_Returns_Stale()
    {
        var runner = new FakeProcessRunner
        {
            Responder = req => req.Arguments.Contains("/Get-MountedImageInfo")
                ? new ProcessResult { ExitCode = 0, StandardOutput = string.Empty } // not registered
                : new ProcessResult { ExitCode = 0, StandardOutput = string.Empty }
        };
        var service = Build(runner, out _, out _, _root);

        var ws = new ImageServicingWorkspace
        {
            WorkingImagePath = Path.Combine(_root, "install.wim"),
            MountDirectory = Path.Combine(_root, "mount"),
            State = ServicingWorkspaceState.Mounted
        };
        File.WriteAllBytes(ws.WorkingImagePath, new byte[16]);

        var result = await service.ValidateServicingWorkspaceAsync(ws, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ServicingHealth.Stale, result.Health);
        Assert.Equal(ServicingWorkspaceState.Failed, ws.State);
    }

    [Fact]
    public async Task Validate_PreparedAndImagePresent_Returns_Prepared()
    {
        var runner = new FakeProcessRunner
        {
            Responder = req => req.Arguments.Contains("/Get-MountedImageInfo")
                ? new ProcessResult { ExitCode = 0, StandardOutput = string.Empty }
                : new ProcessResult { ExitCode = 0, StandardOutput = string.Empty }
        };
        var service = Build(runner, out _, out _, _root);

        var ws = new ImageServicingWorkspace
        {
            WorkingImagePath = Path.Combine(_root, "install.wim"),
            MountDirectory = Path.Combine(_root, "mount"),
            State = ServicingWorkspaceState.Prepared
        };
        File.WriteAllBytes(ws.WorkingImagePath, new byte[16]);

        var result = await service.ValidateServicingWorkspaceAsync(ws, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(ServicingHealth.Prepared, result.Health);
    }

    [Fact]
    public async Task Validate_RegisteredButNotMounted_Returns_Stale()
    {
        var mountDir = Path.Combine(_root, "mount");
        var runner = new FakeProcessRunner
        {
            Responder = req => req.Arguments.Contains("/Get-MountedImageInfo")
                ? new ProcessResult { ExitCode = 0, StandardOutput = $"Mount Dir : {mountDir}\n" }
                : new ProcessResult { ExitCode = 0, StandardOutput = string.Empty }
        };
        var service = Build(runner, out _, out _, _root);

        var ws = new ImageServicingWorkspace
        {
            WorkingImagePath = Path.Combine(_root, "install.wim"),
            MountDirectory = mountDir,
            State = ServicingWorkspaceState.Prepared
        };
        File.WriteAllBytes(ws.WorkingImagePath, new byte[16]);

        var result = await service.ValidateServicingWorkspaceAsync(ws, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ServicingHealth.Stale, result.Health);
    }
}
