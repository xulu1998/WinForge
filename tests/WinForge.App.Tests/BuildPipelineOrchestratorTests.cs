using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Build;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Phase 10 — Build / ISO Export orchestrator tests. Drives
/// <see cref="ImageBuildService"/> behind configurable fakes to assert the 24
/// behavioral requirements: gating, commit/export semantics, single-edition
/// strategy, WIM/ESD source handling, ADK detection, dual-boot media flow,
/// missing boot files, output conflict, atomic partial rename, cancellation,
/// state transitions, and "never report false success".
/// </summary>
public sealed class BuildPipelineOrchestratorTests
{
    private const string BuildWs = @"C:\build";
    private const string OutDir = @"C:\out";
    private const string IsoName = "WinForge_Pro_20260810-1200";
    private const string FinalIso = OutDir + @"\WinForge_Pro_20260810-1200.iso";
    private const string WorkingImage = @"C:\work\install.wim";

    private static BuildRequest MakeRequest(
        RecordingFileSystem fs,
        WindowsImageType sourceType = WindowsImageType.Wim,
        BuildOverwritePolicy policy = BuildOverwritePolicy.GenerateUniqueName,
        string? outputDirectory = null,
        string? outputFileName = null)
    {
        fs.SeedFile(@"C:\src.iso", 100);
        fs.SeedFile(WorkingImage, 200);
        fs.SeedDir(outputDirectory ?? OutDir);
        return new BuildRequest
        {
            SourceIsoPath = @"C:\src.iso",
            SourceImageRelativePath = sourceType == WindowsImageType.Esd ? @"sources\install.esd" : @"sources\install.wim",
            SourceImageType = sourceType,
            WorkingImagePath = WorkingImage,
            MountDirectory = @"C:\work\mount",
            WorkingIndex = 1,
            SourceEditionName = "Windows 11 Pro",
            FinalEditionName = "Windows 11 Pro",
            OutputDirectory = outputDirectory ?? OutDir,
            OutputFileName = outputFileName ?? IsoName,
            Mode = BuildMode.SingleCustomizedEdition,
            OverwritePolicy = policy,
            BuildWorkspaceDirectory = BuildWs
        };
    }

    private static ImageBuildService MakeService(
        ConfigurableServicingService svc,
        ConfigurableWimExporter exporter,
        ConfigurableMediaPreparer media,
        ConfigurableIsoBuilder iso,
        ConfigurableVerifier verifier,
        IAdkToolLocator adk,
        RecordingFileSystem fs)
        => new(svc, exporter, media, iso, verifier, adk, fs, new InMemoryLoggerService());

    [Fact]
    public async Task Build_Success_Path_Produces_Completed_Iso_And_Cleans_Workspace()
    {
        var fs = new RecordingFileSystem();
        var svc = new ConfigurableServicingService();
        var exporter = new ConfigurableWimExporter();
        var media = new ConfigurableMediaPreparer();
        var iso = new ConfigurableIsoBuilder(fs);
        var verifier = new ConfigurableVerifier();
        var service = MakeService(svc, exporter, media, iso, verifier, new FakeAdkToolLocator(), fs);

        var result = await service.BuildAsync(MakeRequest(fs));

        Assert.True(result.Success);
        Assert.Equal(BuildState.Completed, result.FinalState);
        Assert.Equal(FinalIso, result.OutputPath);
        Assert.Equal(1_234_567, result.OutputSizeBytes);
        // Single-edition export: only the working index, written to install.wim.
        Assert.Equal(1, exporter.LastRequest!.SourceIndex);
        Assert.Equal(BuildWs + @"\install.wim", exporter.LastRequest.DestinationImagePath);
        Assert.Equal(BuildWs + @"\install.wim", media.LastRequest!.FinalInstallWimPath);
        // Dual-boot boot files handed to the ISO builder.
        Assert.Equal(BuildWs + @"\media\boot\etfsboot.com", iso.LastRequest!.BootFileEtfs);
        Assert.Equal(BuildWs + @"\media\efi\microsoft\boot\efisys.bin", iso.LastRequest.BootFileEfisys);
        // Verifier invoked with the expected single index and edition.
        Assert.Equal(1, verifier.LastRequest!.ExpectedIndex);
        Assert.Equal("Windows 11 Pro", verifier.LastRequest.ExpectedEditionName);
        // Output present, source working image untouched, temp workspace cleaned.
        Assert.True(fs.FileExists(FinalIso));
        Assert.True(fs.FileExists(WorkingImage));
        Assert.Contains(BuildWs, fs.DeletedDirectories);
    }

    [Fact]
    public async Task Build_Preflight_Fails_When_Adk_Missing_And_No_Substeps_Run()
    {
        var fs = new RecordingFileSystem();
        var exporter = new ConfigurableWimExporter();
        var media = new ConfigurableMediaPreparer();
        var iso = new ConfigurableIsoBuilder(fs);
        var service = MakeService(new(), exporter, media, iso, new(), new MissingAdkToolLocator(), fs);

        var result = await service.BuildAsync(MakeRequest(fs));

        Assert.Equal(BuildState.Preflight, result.FailedPhase);
        Assert.Equal(0, exporter.Calls);
        Assert.Equal(0, media.Calls);
        Assert.Equal(0, iso.Calls);
    }

    [Fact]
    public async Task Build_Preflight_Fails_When_Source_Iso_Missing()
    {
        var fs = new RecordingFileSystem();
        fs.SeedFile(WorkingImage, 200);
        fs.SeedDir(OutDir);
        var request = new BuildRequest
        {
            SourceIsoPath = @"C:\missing.iso",
            SourceImageRelativePath = @"sources\install.wim",
            SourceImageType = WindowsImageType.Wim,
            WorkingImagePath = WorkingImage,
            MountDirectory = @"C:\work\mount",
            OutputDirectory = OutDir,
            OutputFileName = IsoName,
            BuildWorkspaceDirectory = BuildWs
        };

        var service = MakeService(new(), new(), new(), new(fs), new(), new FakeAdkToolLocator(), fs);
        var result = await service.BuildAsync(request);

        Assert.Equal(BuildState.Preflight, result.FailedPhase);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Build_Preflight_Fails_When_Working_Image_Missing()
    {
        var fs = new RecordingFileSystem();
        fs.SeedFile(@"C:\src.iso", 100);
        fs.SeedDir(OutDir);
        var request = new BuildRequest
        {
            SourceIsoPath = @"C:\src.iso",
            SourceImageRelativePath = @"sources\install.wim",
            SourceImageType = WindowsImageType.Wim,
            WorkingImagePath = @"C:\missing\install.wim",
            MountDirectory = @"C:\work\mount",
            OutputDirectory = OutDir,
            OutputFileName = IsoName,
            BuildWorkspaceDirectory = BuildWs
        };

        var service = MakeService(new(), new(), new(), new(fs), new(), new FakeAdkToolLocator(), fs);
        var result = await service.BuildAsync(request);

        Assert.Equal(BuildState.Preflight, result.FailedPhase);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Build_Preflight_Fails_When_Required_Field_Empty()
    {
        var fs = new RecordingFileSystem();
        var request = MakeRequest(fs, outputDirectory: string.Empty);

        var service = MakeService(new(), new(), new(), new(fs), new(), new FakeAdkToolLocator(), fs);
        var result = await service.BuildAsync(request);

        Assert.Equal(BuildState.Preflight, result.FailedPhase);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Build_Commit_Failure_Stops_Pipeline_And_Keeps_Workspace_Recoverable()
    {
        var fs = new RecordingFileSystem();
        var svc = new ConfigurableServicingService { CommitSucceeds = false };
        var exporter = new ConfigurableWimExporter();
        var media = new ConfigurableMediaPreparer();
        var iso = new ConfigurableIsoBuilder(fs);
        var service = MakeService(svc, exporter, media, iso, new(), new FakeAdkToolLocator(), fs);

        var result = await service.BuildAsync(MakeRequest(fs));

        Assert.False(result.Success);
        Assert.Equal(BuildState.CommittingImage, result.FailedPhase);
        Assert.Equal(BuildState.Failed, result.FinalState);
        // No ISO export, media copy, or ISO build begins after a commit failure.
        Assert.Equal(0, exporter.Calls);
        Assert.Equal(0, media.Calls);
        Assert.Equal(0, iso.Calls);
        // The working image is left intact and recoverable.
        Assert.True(fs.FileExists(WorkingImage));
        Assert.Contains(BuildWs, fs.DeletedDirectories);
    }

    [Fact]
    public async Task Build_Export_Failure_Fails_At_ExportingImage()
    {
        var fs = new RecordingFileSystem();
        var exporter = new ConfigurableWimExporter { Succeeds = false, ExitCode = 2 };
        var iso = new ConfigurableIsoBuilder(fs);
        var service = MakeService(new(), exporter, new(), iso, new(), new FakeAdkToolLocator(), fs);

        var result = await service.BuildAsync(MakeRequest(fs));

        Assert.Equal(BuildState.ExportingImage, result.FailedPhase);
        Assert.False(result.Success);
        Assert.Equal(0, iso.Calls);
        Assert.Contains(BuildWs, fs.DeletedDirectories);
    }

    [Fact]
    public async Task Build_Media_Prepare_Failure_Fails_At_PreparingMedia()
    {
        var fs = new RecordingFileSystem();
        var media = new ConfigurableMediaPreparer { Succeeds = false };
        var iso = new ConfigurableIsoBuilder(fs);
        var service = MakeService(new(), new(), media, iso, new(), new FakeAdkToolLocator(), fs);

        var result = await service.BuildAsync(MakeRequest(fs));

        Assert.Equal(BuildState.PreparingMedia, result.FailedPhase);
        Assert.False(result.Success);
        Assert.Equal(0, iso.Calls);
    }

    [Fact]
    public async Task Build_Media_Missing_Boot_Files_Fails_At_PreparingMedia()
    {
        var fs = new RecordingFileSystem();
        var media = new ConfigurableMediaPreparer { Succeeds = true, BootFilesPresent = false };
        var iso = new ConfigurableIsoBuilder(fs);
        var service = MakeService(new(), new(), media, iso, new(), new FakeAdkToolLocator(), fs);

        var result = await service.BuildAsync(MakeRequest(fs));

        Assert.Equal(BuildState.PreparingMedia, result.FailedPhase);
        Assert.False(result.Success);
        Assert.Contains("etfsboot.com", result.ErrorMessage);
        Assert.Equal(0, iso.Calls);
    }

    [Fact]
    public async Task Build_Iso_Tool_Missing_Fails_At_BuildingIso_With_Adk_Message_And_No_False_Success()
    {
        var fs = new RecordingFileSystem();
        var iso = new ConfigurableIsoBuilder(fs) { ToolMissing = true };
        var verifier = new ConfigurableVerifier();
        var service = MakeService(new(), new(), new(), iso, verifier, new FakeAdkToolLocator(), fs);

        var result = await service.BuildAsync(MakeRequest(fs));

        Assert.False(result.Success);
        Assert.Equal(BuildState.BuildingIso, result.FailedPhase);
        Assert.Equal(BuildState.Failed, result.FinalState);
        Assert.Contains("ADK", result.ErrorMessage);
        Assert.Equal(0, verifier.Calls); // never verify a phantom ISO
    }

    [Fact]
    public async Task Build_Iso_Build_Failure_Fails_At_BuildingIso_With_ExitCode()
    {
        var fs = new RecordingFileSystem();
        var iso = new ConfigurableIsoBuilder(fs) { Succeeds = false, ExitCode = 7 };
        var service = MakeService(new(), new(), new(), iso, new(), new FakeAdkToolLocator(), fs);

        var result = await service.BuildAsync(MakeRequest(fs));

        Assert.Equal(BuildState.BuildingIso, result.FailedPhase);
        Assert.Equal(7, result.ToolExitCode);
        Assert.False(result.Success);
        Assert.Contains(BuildWs, fs.DeletedDirectories);
    }

    [Fact]
    public async Task Build_Verify_Failure_Fails_At_Verifying_Even_When_Iso_Succeeded()
    {
        var fs = new RecordingFileSystem();
        var iso = new ConfigurableIsoBuilder(fs);
        var verifier = new ConfigurableVerifier { Succeeds = false };
        var service = MakeService(new(), new(), new(), iso, verifier, new FakeAdkToolLocator(), fs);

        var result = await service.BuildAsync(MakeRequest(fs));

        Assert.False(result.Success); // never derive success from the ISO tool
        Assert.Equal(BuildState.Verifying, result.FailedPhase);
        Assert.Equal(BuildState.Failed, result.FinalState);
        Assert.Contains(BuildWs, fs.DeletedDirectories);
    }

    [Fact]
    public async Task Build_Cancellation_Returns_Cancelled_And_Cleans_Workspace()
    {
        var fs = new RecordingFileSystem();
        var svc = new ConfigurableServicingService { ThrowOnCancel = true };
        var exporter = new ConfigurableWimExporter();
        var iso = new ConfigurableIsoBuilder(fs);
        var service = MakeService(svc, exporter, new(), iso, new(), new FakeAdkToolLocator(), fs);

        var result = await service.BuildAsync(MakeRequest(fs), cancellationToken: new CancellationToken(true));

        Assert.False(result.Success);
        Assert.Equal(BuildState.Cancelled, result.FinalState);
        Assert.Equal(BuildState.CommittingImage, result.FailedPhase);
        Assert.Equal(0, exporter.Calls);
        Assert.Contains(BuildWs, fs.DeletedDirectories);
    }

    [Fact]
    public async Task Build_Output_Conflict_Fail_Policy_Returns_Null_Path_At_Preflight()
    {
        var fs = new RecordingFileSystem();
        fs.SeedFile(FinalIso, 999); // pre-existing output
        var service = MakeService(new(), new(), new(), new(fs), new(), new FakeAdkToolLocator(), fs);

        var result = await service.BuildAsync(MakeRequest(fs, policy: BuildOverwritePolicy.Fail));

        Assert.False(result.Success);
        Assert.Equal(BuildState.Preflight, result.FailedPhase);
        Assert.Null(result.OutputPath);
    }

    [Fact]
    public async Task Build_Output_Conflict_GenerateUniqueName_Produces_Unique_Path()
    {
        var fs = new RecordingFileSystem();
        fs.SeedFile(FinalIso, 999); // pre-existing output
        var iso = new ConfigurableIsoBuilder(fs);
        var service = MakeService(new(), new(), new(), iso, new(), new FakeAdkToolLocator(), fs);

        var result = await service.BuildAsync(MakeRequest(fs, policy: BuildOverwritePolicy.GenerateUniqueName));

        Assert.True(result.Success);
        Assert.EndsWith("(1).iso", result.OutputPath);
    }

    [Fact]
    public async Task Build_SingleEdition_Exports_Only_Selected_Index()
    {
        var fs = new RecordingFileSystem();
        var exporter = new ConfigurableWimExporter();
        var iso = new ConfigurableIsoBuilder(fs);
        var service = MakeService(new(), exporter, new(), iso, new(), new FakeAdkToolLocator(), fs);

        await service.BuildAsync(MakeRequest(fs));

        Assert.Equal(1, exporter.Calls);
        Assert.Equal(1, exporter.LastRequest!.SourceIndex);
    }

    [Fact]
    public async Task Build_Covers_Esd_Source_Through_MediaPreparer()
    {
        var fs = new RecordingFileSystem();
        var media = new ConfigurableMediaPreparer();
        var iso = new ConfigurableIsoBuilder(fs);
        var service = MakeService(new(), new(), media, iso, new(), new FakeAdkToolLocator(), fs);

        var result = await service.BuildAsync(MakeRequest(fs, sourceType: WindowsImageType.Esd));

        Assert.True(result.Success);
        Assert.Equal(WindowsImageType.Esd, media.LastRequest!.SourceImageType);
        Assert.Equal(@"sources\install.esd", media.LastRequest.SourceImageRelativePath);
    }
}
