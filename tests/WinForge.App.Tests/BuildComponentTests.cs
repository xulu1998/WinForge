using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Build;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Phase 10 — direct unit tests for the build sub-services (no full pipeline):
/// <see cref="OscdimgArgumentBuilder"/> dual-boot command,
/// <see cref="OscdimgIsoBuilder"/> ADK detection / missing boot files,
/// <see cref="IsoMediaPreparer"/> WIM and ESD source handling,
/// <see cref="DismWimExporter"/> export invocation, and
/// <see cref="BuildVerifier"/> independent verification.
/// </summary>
public sealed class BuildComponentTests
{
    // ---- OscdimgArgumentBuilder (pure) ----

    [Fact]
    public void OscdimgArgumentBuilder_Build_Produces_DualBootCommand()
    {
        var etfs = @"C:\media\boot\etfsboot.com";
        var efisys = @"C:\media\efi\microsoft\boot\efisys.bin";
        var args = OscdimgArgumentBuilder.Build(@"C:\media", etfs, efisys, @"C:\out\i.iso");

        var expectedBoot = $"-bootdata:2#p0,e,b\"{etfs}\"#pEF,e,b\"{efisys}\"";
        Assert.Contains(expectedBoot, args);
        Assert.Contains("\"C:\\media\"", args);
        Assert.Contains("\"C:\\out\\i.iso\"", args);
        Assert.Contains("-u2", args);
        Assert.Contains("-udfver102", args);
    }

    // ---- OscdimgIsoBuilder ----

    [Fact]
    public async Task OscdimgIsoBuilder_Builds_With_DualBootArgs()
    {
        var fs = new RecordingFileSystem();
        fs.SeedFile(@"C:\media\boot\etfsboot.com");
        fs.SeedFile(@"C:\media\efi\microsoft\boot\efisys.bin");
        var runner = new FakeProcessRunner();
        var request = new IsoBuildRequest
        {
            MediaRoot = @"C:\media",
            OutputIsoPath = @"C:\out\i.iso",
            BootFileEtfs = @"C:\media\boot\etfsboot.com",
            BootFileEfisys = @"C:\media\efi\microsoft\boot\efisys.bin"
        };
        runner.Responder = _ => { fs.SeedFile(request.OutputIsoPath, 100); return new ProcessResult { ExitCode = 0, StandardOutput = "ok" }; };

        var builder = new OscdimgIsoBuilder(new FakeAdkToolLocator(), runner, fs, new InMemoryLoggerService());
        var result = await builder.BuildAsync(request);

        Assert.True(result.Success);
        Assert.Equal(@"C:\fake\adk\oscdimg.exe", runner.Requests[0].FileName);
        Assert.Contains("-bootdata:2#p0,e,b", runner.Requests[0].Arguments);
        Assert.Contains("etfsboot.com", runner.Requests[0].Arguments);
        Assert.Contains("efisys.bin", runner.Requests[0].Arguments);
    }

    [Fact]
    public async Task OscdimgIsoBuilder_ToolMissing_Returns_ToolMissing()
    {
        var request = new IsoBuildRequest
        {
            MediaRoot = @"C:\media",
            OutputIsoPath = @"C:\out\i.iso",
            BootFileEtfs = @"C:\media\boot\etfsboot.com",
            BootFileEfisys = @"C:\media\efi\microsoft\boot\efisys.bin"
        };
        var runner = new FakeProcessRunner();
        var builder = new OscdimgIsoBuilder(new MissingAdkToolLocator(), runner, new RecordingFileSystem(), new InMemoryLoggerService());

        var result = await builder.BuildAsync(request);

        Assert.False(result.Success);
        Assert.True(result.ToolMissing);
        Assert.Empty(runner.Requests); // never invoke a missing tool
    }

    [Fact]
    public async Task OscdimgIsoBuilder_Missing_Bios_BootFile_Fails()
    {
        var fs = new RecordingFileSystem();
        fs.SeedFile(@"C:\media\efi\microsoft\boot\efisys.bin"); // etfs deliberately absent
        var request = new IsoBuildRequest
        {
            MediaRoot = @"C:\media",
            OutputIsoPath = @"C:\out\i.iso",
            BootFileEtfs = @"C:\media\boot\etfsboot.com",
            BootFileEfisys = @"C:\media\efi\microsoft\boot\efisys.bin"
        };
        var runner = new FakeProcessRunner();
        var builder = new OscdimgIsoBuilder(new FakeAdkToolLocator(), runner, fs, new InMemoryLoggerService());

        var result = await builder.BuildAsync(request);

        Assert.False(result.Success);
        Assert.Contains("etfsboot.com", result.ErrorMessage);
        Assert.Empty(runner.Requests);
    }

    // ---- IsoMediaPreparer (WIM and ESD sources) ----

    [Fact]
    public async Task IsoMediaPreparer_WimSource_Replaces_InstallWim_And_Validates_BootFiles()
    {
        var fs = new RecordingFileSystem();
        fs.SeedFile(@"C:\src.iso"); // source ISO must exist for preflight
        fs.SeedFile(@"E:\sources\install.wim");
        fs.SeedFile(@"E:\boot\etfsboot.com");
        fs.SeedFile(@"E:\efi\microsoft\boot\efisys.bin");
        fs.SeedFile(@"E:\setup.exe");
        fs.SeedFile(@"C:\work\install.wim", 500);

        var preparer = new IsoMediaPreparer(new FakeIsoMountService { MountRoot = @"E:\" }, fs, new InMemoryLoggerService());
        var request = new MediaPrepareRequest
        {
            SourceIsoPath = @"C:\src.iso",
            BuildMediaRoot = @"C:\build\media",
            SourceImageRelativePath = @"sources\install.wim",
            SourceImageType = WindowsImageType.Wim,
            FinalInstallWimPath = @"C:\work\install.wim"
        };

        var result = await preparer.PrepareAsync(request);

        Assert.True(result.Success);
        Assert.True(result.BootFilesPresent);
        Assert.Equal(@"C:\build\media\sources\install.wim", result.InstallImagePath);
        Assert.True(fs.FileExists(@"C:\build\media\sources\install.wim"));
    }

    [Fact]
    public async Task IsoMediaPreparer_EsdSource_Removes_InstallEsd_And_Writes_InstallWim()
    {
        var fs = new RecordingFileSystem();
        fs.SeedFile(@"C:\src.iso"); // source ISO must exist for preflight
        fs.SeedFile(@"E:\sources\install.esd"); // ESD source: no install.wim at source
        fs.SeedFile(@"E:\boot\etfsboot.com");
        fs.SeedFile(@"E:\efi\microsoft\boot\efisys.bin");
        fs.SeedFile(@"C:\work\install.wim", 500);

        var preparer = new IsoMediaPreparer(new FakeIsoMountService { MountRoot = @"E:\" }, fs, new InMemoryLoggerService());
        var request = new MediaPrepareRequest
        {
            SourceIsoPath = @"C:\src.iso",
            BuildMediaRoot = @"C:\build\media",
            SourceImageRelativePath = @"sources\install.esd",
            SourceImageType = WindowsImageType.Esd,
            FinalInstallWimPath = @"C:\work\install.wim"
        };

        var result = await preparer.PrepareAsync(request);

        Assert.True(result.Success);
        Assert.True(result.BootFilesPresent);
        // The ESD payload is replaced by a WIM in the media tree.
        Assert.True(fs.FileExists(@"C:\build\media\sources\install.wim"));
        Assert.Contains(@"C:\build\media\sources\install.esd", fs.DeletedFiles);
        Assert.False(fs.FileExists(@"C:\build\media\sources\install.esd"));
    }

    // ---- DismWimExporter ----

    [Fact]
    public async Task DismWimExporter_Exports_With_ExportImage_Args()
    {
        var fs = new RecordingFileSystem();
        fs.SeedFile(@"C:\work\install.wim", 200);
        var runner = new FakeProcessRunner();
        runner.Responder = _ =>
        {
            fs.SeedFile(@"C:\build\install.wim", 200); // DISM would create the destination
            return new ProcessResult { ExitCode = 0 };
        };
        var exporter = new DismWimExporter(runner, fs, new InMemoryLoggerService());
        var request = new WimExportRequest
        {
            SourceImagePath = @"C:\work\install.wim",
            SourceIndex = 1,
            DestinationImagePath = @"C:\build\install.wim"
        };

        var result = await exporter.ExportAsync(request);

        Assert.True(result.Success);
        Assert.Contains("/Export-Image", runner.Requests[0].Arguments);
        Assert.Contains("/SourceIndex:1", runner.Requests[0].Arguments);
        Assert.Contains(@"""C:\build\install.wim""", runner.Requests[0].Arguments);
    }

    [Fact]
    public async Task DismWimExporter_NonZero_Exit_Fails()
    {
        var fs = new RecordingFileSystem();
        fs.SeedFile(@"C:\work\install.wim", 200);
        var runner = new FakeProcessRunner { Default = new ProcessResult { ExitCode = 5 } };
        var exporter = new DismWimExporter(runner, fs, new InMemoryLoggerService());
        var request = new WimExportRequest
        {
            SourceImagePath = @"C:\work\install.wim",
            SourceIndex = 1,
            DestinationImagePath = @"C:\build\install.wim"
        };

        var result = await exporter.ExportAsync(request);

        Assert.False(result.Success);
        Assert.Equal(5, result.ExitCode);
    }

    [Fact]
    public async Task DismWimExporter_Missing_Source_Does_Not_Run_Dism()
    {
        var fs = new RecordingFileSystem(); // working image NOT seeded
        var runner = new FakeProcessRunner();
        var exporter = new DismWimExporter(runner, fs, new InMemoryLoggerService());
        var request = new WimExportRequest
        {
            SourceImagePath = @"C:\work\install.wim",
            SourceIndex = 1,
            DestinationImagePath = @"C:\build\install.wim"
        };

        var result = await exporter.ExportAsync(request);

        Assert.False(result.Success);
        Assert.Empty(runner.Requests);
    }

    // ---- BuildVerifier (independent verification) ----

    [Fact]
    public async Task BuildVerifier_Missing_Output_Iso_Fails()
    {
        var fs = new RecordingFileSystem();
        fs.SeedFile(@"C:\build\media\sources\install.wim", 300);
        var runner = new FakeProcessRunner { Default = new ProcessResult { ExitCode = 0 } };
        var verifier = new BuildVerifier(fs, runner, new FakeIsoMountService(), new InMemoryLoggerService());
        var request = new BuildVerificationRequest
        {
            OutputIsoPath = @"C:\out\i.iso",
            ExpectedInstallWimPath = @"C:\build\media\sources\install.wim",
            ExpectedIndex = 1
        };

        var result = await verifier.VerifyAsync(request);

        Assert.False(result.Success);
        Assert.Contains("output ISO is missing", result.ErrorMessage);
    }

    [Fact]
    public async Task BuildVerifier_Missing_InstallWim_Fails()
    {
        var fs = new RecordingFileSystem();
        fs.SeedFile(@"C:\out\i.iso", 100);
        var runner = new FakeProcessRunner { Default = new ProcessResult { ExitCode = 0 } };
        var verifier = new BuildVerifier(fs, runner, new FakeIsoMountService(), new InMemoryLoggerService());
        var request = new BuildVerificationRequest
        {
            OutputIsoPath = @"C:\out\i.iso",
            ExpectedInstallWimPath = @"C:\build\media\sources\install.wim",
            ExpectedIndex = 1
        };

        var result = await verifier.VerifyAsync(request);

        Assert.False(result.Success);
        Assert.Contains("install.wim is missing", result.ErrorMessage);
    }

    [Fact]
    public async Task BuildVerifier_Mounted_Image_Present_Fails()
    {
        var fs = new RecordingFileSystem();
        fs.SeedFile(@"C:\out\i.iso", 100);
        fs.SeedFile(@"C:\build\media\sources\install.wim", 300);
        var runner = new FakeProcessRunner();
        runner.Responder = r => r.Arguments.Contains("/Get-MountedImageInfo")
            ? new ProcessResult { ExitCode = 0, StandardOutput = "Mount Dir : C:\\x" }
            : new ProcessResult { ExitCode = 0, StandardOutput = string.Empty };
        var verifier = new BuildVerifier(fs, runner, new FakeIsoMountService(), new InMemoryLoggerService());
        var request = new BuildVerificationRequest
        {
            OutputIsoPath = @"C:\out\i.iso",
            ExpectedInstallWimPath = @"C:\build\media\sources\install.wim",
            ExpectedIndex = 1
        };

        var result = await verifier.VerifyAsync(request);

        Assert.False(result.Success);
        Assert.Contains("still mounted", result.ErrorMessage);
    }
}
