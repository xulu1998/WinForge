using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.ImageMetadata;
using WinForge.Infrastructure.Logging;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// <see cref="WindowsImageMetadataService"/> behaviour driven by a fake
/// <see cref="IProcessRunner"/>. Verifies WIM/ESD detection, friendly handling of
/// non-zero exits and missing tooling, and that cancellation propagates instead
/// of being swallowed. A real temp image file is used so the service's existence
/// check passes and the fake process runner is actually invoked.
/// </summary>
public class ImageMetadataServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(AppContext.BaseDirectory, "wf_meta_" + Guid.NewGuid().ToString("N"));

    public ImageMetadataServiceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* best effort */ }
    }

    private const string TwoEditionOutput = @"
Details for image : D:\sources\install.wim

Index : 1
Name : Windows 11 Home
Description : Windows 11 Home
Architecture : x64
Version : 10.0.26100.1742
Edition : Home
Edition Id :
Installation : Client
Languages :
        en-US (Default)

Index : 2
Name : Windows 11 Pro
Description : Windows 11 Pro
Architecture : x64
Version : 10.0.26100.1742
Edition : Professional
Edition Id :
Installation : Client
Languages :
        en-US (Default)
";

    [Fact]
    public async Task Install_Wim_Is_Recognized_And_Parsed()
    {
        var path = MakeImageFile("install.wim");
        var runner = new FakeProcessRunner
        {
            Next = new ProcessResult { ExitCode = 0, StandardOutput = TwoEditionOutput }
        };
        var service = new WindowsImageMetadataService(runner, new InMemoryLoggerService());

        var result = await service.InspectAsync(path);

        Assert.Equal(WindowsImageType.Wim, result.ImageType);
        Assert.Equal(WindowsImageMetadataStatus.Completed, result.Status);
        Assert.Equal(2, result.Editions.Count);
        Assert.Equal("dism.exe", runner.LastRequest?.FileName);
        Assert.Contains("/English", runner.LastRequest?.Arguments ?? string.Empty);
        Assert.Contains("/Get-WimInfo", runner.LastRequest?.Arguments ?? string.Empty);
    }

    [Fact]
    public async Task Install_Esd_Is_Recognized_And_Parsed()
    {
        var path = MakeImageFile("install.esd");
        var runner = new FakeProcessRunner
        {
            Next = new ProcessResult { ExitCode = 0, StandardOutput = TwoEditionOutput }
        };
        var service = new WindowsImageMetadataService(runner, new InMemoryLoggerService());

        var result = await service.InspectAsync(path);

        Assert.Equal(WindowsImageType.Esd, result.ImageType);
        Assert.Equal(WindowsImageMetadataStatus.Completed, result.Status);
        Assert.Equal(2, result.Editions.Count);
    }

    [Fact]
    public async Task NonZero_Exit_Returns_Friendly_Failure_Without_Leaking_Stderr()
    {
        var path = MakeImageFile("install.wim");
        var runner = new FakeProcessRunner
        {
            Next = new ProcessResult { ExitCode = 1, StandardOutput = "", StandardError = "0x80070057 HRESULT boom" }
        };
        var service = new WindowsImageMetadataService(runner, new InMemoryLoggerService());

        var result = await service.InspectAsync(path);

        Assert.Equal(WindowsImageMetadataStatus.Failed, result.Status);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        // Raw DISM/ HRESULT detail must never reach the user-facing message.
        Assert.DoesNotContain("HRESULT", result.ErrorMessage!);
        Assert.DoesNotContain("boom", result.ErrorMessage!);
    }

    [Fact]
    public async Task Missing_Tooling_Returns_Friendly_Failure()
    {
        var path = MakeImageFile("install.wim");
        var runner = new FakeProcessRunner
        {
            Throw = new InvalidOperationException("dism.exe could not be started")
        };
        var service = new WindowsImageMetadataService(runner, new InMemoryLoggerService());

        var result = await service.InspectAsync(path);

        Assert.Equal(WindowsImageMetadataStatus.Failed, result.Status);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [Fact]
    public async Task Cancellation_Propagates_As_OperationCanceledException()
    {
        var path = MakeImageFile("install.wim");
        var runner = new FakeProcessRunner { Throw = new OperationCanceledException() };
        var service = new WindowsImageMetadataService(runner, new InMemoryLoggerService());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.InspectAsync(path, new CancellationToken(true)));
    }

    private string MakeImageFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public ProcessResult? Next { get; set; }
        public Exception? Throw { get; set; }
        public ProcessRequest? LastRequest { get; private set; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (Throw is not null)
            {
                return Task.FromException<ProcessResult>(Throw);
            }

            return Task.FromResult(Next ?? new ProcessResult { ExitCode = 0, StandardOutput = string.Empty });
        }
    }
}
