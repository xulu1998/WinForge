using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.Build;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// <see cref="IsoMediaPreparer"/> behavior: the SOURCE ISO is read-only and its
/// files (e.g. <c>autorun.inf</c>) carry ReadOnly attributes. After copy the build
/// tree must be writable, the source must be untouched, the dual-boot tree must be
/// present, and a dirty prior media tree (ReadOnly files) must be cleaned
/// deterministically on retry. Uses <see cref="RecordingFileSystem"/> (attribute
/// aware) behind a fake mount service — no real ISO.
/// </summary>
public sealed class IsoMediaPreparerTests
{
    private const string IsoRoot = @"C:\iso";
    private const string BuildMedia = @"C:\build\media";
    private const string FinalWim = @"C:\work\install.wim";

    private sealed class FakeIsoMountService : IIsoMountService
    {
        public string MountRoot { get; set; } = IsoRoot;
        public Task<string> MountReadOnlyAsync(string isoPath, CancellationToken ct = default)
            => Task.FromResult(MountRoot);
        public Task DismountAsync(string isoPath, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static RecordingFileSystem SeedSource(RecordingFileSystem fs)
    {
        fs.SeedFile(@"C:\src.iso", 100); // the ISO "file" (existence check)
        fs.SeedFile(FinalWim, 200);      // the exported final WIM to embed

        // Mounted media tree with a ReadOnly autorun.inf (the real-desktop culprit).
        fs.SeedFile(Path.Combine(IsoRoot, "autorun.inf"), 10, FileAttributes.ReadOnly);
        fs.SeedDir(Path.Combine(IsoRoot, "boot"));
        fs.SeedFile(Path.Combine(IsoRoot, "boot", "etfsboot.com"), 10);
        fs.SeedDir(Path.Combine(IsoRoot, "efi", "microsoft", "boot"));
        fs.SeedFile(Path.Combine(IsoRoot, "efi", "microsoft", "boot", "efisys.bin"), 10);
        fs.SeedDir(Path.Combine(IsoRoot, "sources"));
        fs.SeedFile(Path.Combine(IsoRoot, "sources", "install.wim"), 100);
        return fs;
    }

    private static MediaPrepareRequest MakeRequest(RecordingFileSystem fs, string? buildMedia = null)
    {
        return new MediaPrepareRequest
        {
            SourceIsoPath = @"C:\src.iso",
            BuildMediaRoot = buildMedia ?? BuildMedia,
            SourceImageRelativePath = @"sources\install.wim",
            SourceImageType = WindowsImageType.Wim,
            FinalInstallWimPath = FinalWim
        };
    }

    [Fact]
    public async Task PrepareAsync_CopiesTree_And_ClearsReadOnlyOnDestination()
    {
        var fs = SeedSource(new RecordingFileSystem());
        var preparer = new IsoMediaPreparer(new FakeIsoMountService(), fs, new InMemoryLoggerService());

        var result = await preparer.PrepareAsync(MakeRequest(fs));

        Assert.True(result.Success);
        // The copied autorun.inf is writable (ReadOnly cleared on the destination).
        Assert.False((fs.GetAttributes(Path.Combine(BuildMedia, "autorun.inf")) & FileAttributes.ReadOnly) != 0);
    }

    [Fact]
    public async Task PrepareAsync_LeavesSourceUntouched()
    {
        var fs = SeedSource(new RecordingFileSystem());
        var preparer = new IsoMediaPreparer(new FakeIsoMountService(), fs, new InMemoryLoggerService());

        await preparer.PrepareAsync(MakeRequest(fs));

        // The mounted source autorun.inf must keep its ReadOnly attribute.
        Assert.True((fs.GetAttributes(Path.Combine(IsoRoot, "autorun.inf")) & FileAttributes.ReadOnly) != 0);
        // The source final WIM is never deleted by media preparation.
        Assert.True(fs.FileExists(FinalWim));
    }

    [Fact]
    public async Task PrepareAsync_SuccessfulTree_Contains_BootEfiSources()
    {
        var fs = SeedSource(new RecordingFileSystem());
        var preparer = new IsoMediaPreparer(new FakeIsoMountService(), fs, new InMemoryLoggerService());

        var result = await preparer.PrepareAsync(MakeRequest(fs));

        Assert.True(result.Success);
        Assert.True(result.BootFilesPresent);
        Assert.True(fs.FileExists(Path.Combine(BuildMedia, "boot", "etfsboot.com")));
        Assert.True(fs.FileExists(Path.Combine(BuildMedia, "efi", "microsoft", "boot", "efisys.bin")));
        Assert.True(fs.FileExists(Path.Combine(BuildMedia, "sources", "install.wim")));
    }

    [Fact]
    public async Task PrepareAsync_DeterministicCleanup_Of_ReadOnlyDirtyTree()
    {
        // Simulate a previous failed attempt that left a ReadOnly media tree.
        var fs = SeedSource(new RecordingFileSystem());
        fs.SeedFile(Path.Combine(BuildMedia, "autorun.inf"), 10, FileAttributes.ReadOnly);
        fs.SeedDir(BuildMedia);
        fs.SeedDir(Path.Combine(BuildMedia, "sources"));
        fs.SeedFile(Path.Combine(BuildMedia, "sources", "install.wim"), 100, FileAttributes.ReadOnly);

        var preparer = new IsoMediaPreparer(new FakeIsoMountService(), fs, new InMemoryLoggerService());

        // Must not fail on the stale ReadOnly tree; it is cleaned deterministically.
        var result = await preparer.PrepareAsync(MakeRequest(fs));

        Assert.True(result.Success);
        Assert.False((fs.GetAttributes(Path.Combine(BuildMedia, "autorun.inf")) & FileAttributes.ReadOnly) != 0);
        Assert.True(fs.FileExists(Path.Combine(BuildMedia, "sources", "install.wim")));
    }

    [Fact]
    public async Task PrepareAsync_MissingSourceIso_Fails()
    {
        var fs = new RecordingFileSystem(); // no source ISO seeded
        fs.SeedFile(FinalWim, 200);
        var preparer = new IsoMediaPreparer(new FakeIsoMountService(), fs, new InMemoryLoggerService());

        var result = await preparer.PrepareAsync(MakeRequest(fs));

        Assert.False(result.Success);
    }
}
