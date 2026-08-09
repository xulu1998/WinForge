using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.IsoInspection;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Integration of Step 2.2 metadata inspection into the high-level ISO
/// inspection session. Confirms metadata is read while the ISO is mounted, the
/// result is attached to <see cref="IsoInspectionResult.ImageMetadata"/>, and the
/// guaranteed dismount (ADR-015) still runs when metadata fails or is cancelled.
/// </summary>
public class IsoPipelineMetadataTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(AppContext.BaseDirectory, "wf_pipe_" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryLoggerService _logger = new();

    public IsoPipelineMetadataTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* best effort */ }
    }

    private WindowsIsoInspectionService CreateService(FakeIsoMountService mount, FakeImageMetadataService metadata) =>
        new(mount, metadata, _logger);

    private string MakeIsoFile()
    {
        var file = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".iso");
        File.WriteAllBytes(file, new byte[2048]);
        return file;
    }

    private string MakeWindowsStructure()
    {
        var root = Path.Combine(_tempDir, "mount_" + Guid.NewGuid().ToString("N"));
        var sources = Path.Combine(root, "sources");
        Directory.CreateDirectory(sources);
        Directory.CreateDirectory(Path.Combine(root, "boot"));
        File.WriteAllBytes(Path.Combine(sources, "install.wim"), new byte[16]);
        return root;
    }

    private static WindowsImageMetadataResult TwoEditionResult() => new()
    {
        ImagePath = "install.wim",
        ImageType = WindowsImageType.Wim,
        Status = WindowsImageMetadataStatus.Completed,
        Editions =
        {
            new WindowsEditionInfo { Index = 1, Name = "Windows 11 Home", Architecture = "x64" },
            new WindowsEditionInfo { Index = 2, Name = "Windows 11 Pro", Architecture = "x64" }
        }
    };

    [Fact]
    public async Task Metadata_Success_Populates_Result_And_Dismounts()
    {
        var iso = MakeIsoFile();
        var mount = new FakeIsoMountService { MountRoot = MakeWindowsStructure() };
        var metadata = new FakeImageMetadataService { Next = TwoEditionResult() };

        var result = await CreateService(mount, metadata).InspectAsync(iso);

        Assert.Equal(IsoInspectionStatus.Completed, result.Status);
        Assert.Equal(IsoDetectedType.WindowsIsoCandidate, result.DetectedType);
        Assert.NotNull(result.ImageMetadata);
        Assert.Equal(2, result.ImageMetadata!.Editions.Count);
        Assert.True(mount.DismountCalled);
        Assert.Equal(CancellationToken.None, mount.DismountToken);
    }

    [Fact]
    public async Task Metadata_Returns_Failed_Result_Still_Dismounts_And_Fails_Inspection()
    {
        var iso = MakeIsoFile();
        var mount = new FakeIsoMountService { MountRoot = MakeWindowsStructure() };
        var metadata = new FakeImageMetadataService
        {
            Next = new WindowsImageMetadataResult
            {
                ImageType = WindowsImageType.Wim,
                Status = WindowsImageMetadataStatus.Failed,
                ErrorMessage = "The Windows image could not be read."
            }
        };

        var result = await CreateService(mount, metadata).InspectAsync(iso);

        Assert.Equal(IsoInspectionStatus.Failed, result.Status);
        Assert.Equal(IsoDetectedType.WindowsIsoCandidate, result.DetectedType);
        Assert.NotNull(result.ImageMetadata);
        Assert.True(mount.DismountCalled);
    }

    [Fact]
    public async Task Metadata_Throws_Still_Dismounts_And_Fails()
    {
        var iso = MakeIsoFile();
        var mount = new FakeIsoMountService { MountRoot = MakeWindowsStructure() };
        var metadata = new FakeImageMetadataService { Throw = new InvalidOperationException("dism crashed") };

        var result = await CreateService(mount, metadata).InspectAsync(iso);

        Assert.Equal(IsoInspectionStatus.Failed, result.Status);
        Assert.True(mount.DismountCalled);
        Assert.Equal(CancellationToken.None, mount.DismountToken);
    }

    [Fact]
    public async Task Metadata_Cancellation_Still_Dismounts_And_Propagates()
    {
        var iso = MakeIsoFile();
        var mount = new FakeIsoMountService { MountRoot = MakeWindowsStructure() };
        var metadata = new FakeImageMetadataService { Throw = new OperationCanceledException() };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateService(mount, metadata).InspectAsync(iso, new CancellationToken(true)));

        Assert.True(mount.DismountCalled);
        Assert.Equal(CancellationToken.None, mount.DismountToken);
    }

    private sealed class FakeIsoMountService : IIsoMountService
    {
        public string? MountRoot { get; set; }
        public bool MountCalled { get; private set; }
        public int DismountCount { get; private set; }
        public bool DismountCalled => DismountCount > 0;
        public CancellationToken DismountToken { get; private set; }

        public Task<string> MountReadOnlyAsync(string isoPath, CancellationToken cancellationToken = default)
        {
            MountCalled = true;
            return Task.FromResult(MountRoot ?? string.Empty);
        }

        public Task DismountAsync(string isoPath, CancellationToken cancellationToken = default)
        {
            DismountCount++;
            DismountToken = cancellationToken;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeImageMetadataService : IWindowsImageMetadataService
    {
        public WindowsImageMetadataResult? Next { get; set; }
        public Exception? Throw { get; set; }

        public Task<WindowsImageMetadataResult> InspectAsync(string imagePath, CancellationToken cancellationToken = default)
        {
            if (Throw is not null)
            {
                return Task.FromException<WindowsImageMetadataResult>(Throw);
            }

            return Task.FromResult(Next ?? new WindowsImageMetadataResult
            {
                ImageType = WindowsImageType.Wim,
                Status = WindowsImageMetadataStatus.Completed,
                Editions = new()
            });
        }
    }
}
