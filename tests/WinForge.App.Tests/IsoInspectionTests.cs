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
/// Inspection logic (Core/Infrastructure boundary). Uses a fake
/// <see cref="IIsoMountService"/> so no real Windows ISO or PowerShell is
/// required — the structure check runs against a plain temp directory the test
/// populates.
/// </summary>
public class IsoInspectionTests : IDisposable
{
    // Use a writable location co-located with the test output (the sandbox build
    // tree on F:/tmp) rather than %TEMP%, which the test host cannot write to here.
    private readonly string _tempDir = Path.Combine(AppContext.BaseDirectory, "wf_iso_" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryLoggerService _logger = new();

    public IsoInspectionTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* best effort */ }
    }

    private WindowsIsoInspectionService CreateService(
        FakeIsoMountService mount,
        FakeImageMetadataService? metadata = null) =>
        new(mount, metadata ?? new FakeImageMetadataService(), _logger);

    private string MakeIsoFile()
    {
        var file = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".iso");
        File.WriteAllBytes(file, new byte[2048]);
        return file;
    }

    private string MakeStructure(bool sources, bool boot, bool installWim, bool installEsd, bool bootWim)
    {
        var root = Path.Combine(_tempDir, "mount_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (sources)
        {
            var s = Path.Combine(root, "sources");
            Directory.CreateDirectory(s);
            if (installWim) File.WriteAllBytes(Path.Combine(s, "install.wim"), new byte[16]);
            if (installEsd) File.WriteAllBytes(Path.Combine(s, "install.esd"), new byte[16]);
            if (bootWim) File.WriteAllBytes(Path.Combine(s, "boot.wim"), new byte[16]);
        }
        if (boot) Directory.CreateDirectory(Path.Combine(root, "boot"));
        return root;
    }

    [Fact]
    public async Task Iso_Extension_Accepted_For_WindowsStructure()
    {
        var iso = MakeIsoFile();
        var mount = new FakeIsoMountService { MountRoot = MakeStructure(true, true, true, false, false) };
        var result = await CreateService(mount).InspectAsync(iso);

        Assert.True(result.ExtensionValid);
        Assert.True(result.Exists);
        Assert.Equal(IsoDetectedType.WindowsIsoCandidate, result.DetectedType);
        Assert.Equal(IsoInspectionStatus.Completed, result.Status);
        Assert.True(mount.DismountCalled);
    }

    [Fact]
    public async Task NonIso_Rejected()
    {
        var txt = Path.Combine(_tempDir, "notaniso.txt");
        File.WriteAllText(txt, "hi");
        var mount = new FakeIsoMountService { MountRoot = MakeStructure(true, true, true, false, false) };
        var result = await CreateService(mount).InspectAsync(txt);

        Assert.False(result.ExtensionValid);
        Assert.Equal(IsoDetectedType.Unknown, result.DetectedType);
        Assert.False(mount.MountCalled);
        Assert.False(mount.DismountCalled);
    }

    [Fact]
    public async Task Nonexistent_File_Handled_Gracefully()
    {
        var missing = Path.Combine(_tempDir, "ghost.iso");
        var mount = new FakeIsoMountService { MountRoot = MakeStructure(true, true, true, false, false) };
        var result = await CreateService(mount).InspectAsync(missing);

        Assert.False(result.Exists);
        Assert.Equal(IsoDetectedType.Unknown, result.DetectedType);
        Assert.Equal(IsoInspectionStatus.Completed, result.Status);
        Assert.False(mount.MountCalled);
    }

    [Fact]
    public async Task Invalid_Iso_MountFailure_Handled_Gracefully()
    {
        var iso = MakeIsoFile();
        var mount = new FakeIsoMountService { MountException = new InvalidOperationException("corrupt image") };
        var result = await CreateService(mount).InspectAsync(iso);

        Assert.Equal(IsoInspectionStatus.Failed, result.Status);
        Assert.Equal(IsoDetectedType.Unknown, result.DetectedType);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [Fact]
    public async Task Windows_Structure_Detected_As_Candidate()
    {
        var iso = MakeIsoFile();
        var mount = new FakeIsoMountService { MountRoot = MakeStructure(true, true, true, false, false) };
        var result = await CreateService(mount).InspectAsync(iso);

        Assert.True(result.HasSourcesDirectory);
        Assert.True(result.HasBootDirectory);
        Assert.True(result.HasInstallWim);
        Assert.Equal(InstallImageType.Wim, result.InstallImageType);
        Assert.Equal(IsoDetectedType.WindowsIsoCandidate, result.DetectedType);
    }

    [Fact]
    public async Task Missing_Sources_Directory_Is_Unknown()
    {
        var iso = MakeIsoFile();
        var mount = new FakeIsoMountService { MountRoot = MakeStructure(false, true, false, false, false) };
        var result = await CreateService(mount).InspectAsync(iso);

        Assert.False(result.HasSourcesDirectory);
        Assert.Equal(IsoDetectedType.Unknown, result.DetectedType);
    }

    [Fact]
    public async Task Missing_Install_Image_Is_Unknown()
    {
        var iso = MakeIsoFile();
        var mount = new FakeIsoMountService { MountRoot = MakeStructure(true, true, false, false, false) };
        var result = await CreateService(mount).InspectAsync(iso);

        Assert.False(result.HasInstallWim);
        Assert.False(result.HasInstallEsd);
        Assert.Equal(InstallImageType.Unknown, result.InstallImageType);
        Assert.Equal(IsoDetectedType.Unknown, result.DetectedType);
    }

    [Fact]
    public async Task Install_Wim_Detected()
    {
        var iso = MakeIsoFile();
        var mount = new FakeIsoMountService { MountRoot = MakeStructure(true, true, true, false, false) };
        var result = await CreateService(mount).InspectAsync(iso);

        Assert.True(result.HasInstallWim);
        Assert.Equal(InstallImageType.Wim, result.InstallImageType);
    }

    [Fact]
    public async Task Install_Esd_Detected()
    {
        var iso = MakeIsoFile();
        var mount = new FakeIsoMountService { MountRoot = MakeStructure(true, true, false, true, false) };
        var result = await CreateService(mount).InspectAsync(iso);

        Assert.True(result.HasInstallEsd);
        Assert.Equal(InstallImageType.Esd, result.InstallImageType);
    }

    [Fact]
    public async Task Cancellation_After_Mount_Still_Attempts_Dismount()
    {
        var iso = MakeIsoFile();
        // Token already cancelled: the mount is cancelled (it may have partially
        // mounted). Cleanup must still be attempted and must use a non-cancelled
        // token; the cancellation must not be swallowed by successful cleanup.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var mount = new FakeIsoMountService
        {
            MountRoot = MakeStructure(true, true, true, false, false),
            ThrowIfCancelled = true
        };

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateService(mount).InspectAsync(iso, cts.Token));

        Assert.True(mount.MountCalled);
        Assert.True(mount.DismountCalled);
        Assert.Equal(CancellationToken.None, mount.DismountToken);
    }

    [Fact]
    public async Task Cleanup_Dismount_Uses_NonCancellable_Token()
    {
        var iso = MakeIsoFile();
        var mount = new FakeIsoMountService { MountRoot = MakeStructure(true, true, true, false, false) };
        var result = await CreateService(mount).InspectAsync(iso);

        Assert.Equal(IsoInspectionStatus.Completed, result.Status);
        Assert.True(mount.DismountCalled);
        // The dismount must run on a token that cleanup itself cannot cancel.
        Assert.Equal(CancellationToken.None, mount.DismountToken);
    }

    [Fact]
    public async Task Inspection_Failure_After_Mount_Still_Dismounts()
    {
        var iso = MakeIsoFile();
        // Mount returns (possibly empty) root but inspection cannot proceed — a
        // post-mount failure. The mount was still attempted, so cleanup runs.
        var mount = new FakeIsoMountService { MountRoot = string.Empty };
        var result = await CreateService(mount).InspectAsync(iso);

        Assert.Equal(IsoInspectionStatus.Failed, result.Status);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.True(mount.MountCalled);
        Assert.True(mount.DismountCalled);
        Assert.Equal(CancellationToken.None, mount.DismountToken);
    }

    [Fact]
    public async Task Successful_Inspection_Dismounts_Exactly_Once()
    {
        var iso = MakeIsoFile();
        var mount = new FakeIsoMountService { MountRoot = MakeStructure(true, true, true, false, false) };
        var result = await CreateService(mount).InspectAsync(iso);

        Assert.Equal(IsoInspectionStatus.Completed, result.Status);
        Assert.Equal(IsoDetectedType.WindowsIsoCandidate, result.DetectedType);
        Assert.Equal(1, mount.DismountCount);
        Assert.Equal(CancellationToken.None, mount.DismountToken);
    }

    private sealed class FakeIsoMountService : IIsoMountService
    {
        public string? MountRoot { get; set; }
        public Exception? MountException { get; set; }
        public bool ThrowIfCancelled { get; set; }
        public bool MountCalled { get; private set; }
        public int DismountCount { get; private set; }
        public bool DismountCalled => DismountCount > 0;
        public CancellationToken MountToken { get; private set; }
        public CancellationToken DismountToken { get; private set; }

        public Task<string> MountReadOnlyAsync(string isoPath, CancellationToken cancellationToken = default)
        {
            MountCalled = true;
            MountToken = cancellationToken;
            if (ThrowIfCancelled && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }

            return MountException is null
                ? Task.FromResult(MountRoot ?? string.Empty)
                : Task.FromException<string>(MountException);
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
                ImagePath = imagePath,
                ImageType = WindowsImageType.Wim,
                Status = WindowsImageMetadataStatus.Completed,
                Editions = new()
            });
        }
    }
}
