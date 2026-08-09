using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
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
/// <see cref="IProcessRunner"/> that records every invocation and returns staged
/// DISM output. Verifies the two-stage flow mandated by the Step 2.2 fix:
/// one enumeration query (no /Index) followed by one per-index detail query
/// (/Index:n) for EACH enumerated index; WIM/ESD detection; friendly handling of
/// non-zero exits, missing tooling, partial detail failure, and cancellation.
/// A real temp image file is used so the service's existence check passes and the
/// fake process runner is actually invoked.
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

    // Enumeration output (no /Index): Index / Name / Description / Size only.
    private const string EnumTwo = @"
Details for image : C:\sources\install.wim

Index : 1
Name : Windows 11 Home
Description : Windows 11 Home
Size : 15,297,491,328 bytes

Index : 2
Name : Windows 11 Pro
Description : Windows 11 Pro
Size : 15,314,268,160 bytes
";

    private const string EnumNonSequential = @"
Index : 1
Name : Windows 11 Home
Description : Windows 11 Home
Size : 15,297,491,328 bytes

Index : 6
Name : Windows 11 Pro
Description : Windows 11 Pro
Size : 15,314,268,160 bytes
";

    private const string DetailHome = @"
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
Default Language : en-US
";

    private const string DetailPro = @"
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
Default Language : en-US
";

    private static int? IndexFromArgs(string args)
    {
        var m = Regex.Match(args, @"/Index:(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : (int?)null;
    }

    [Fact]
    public async Task Wim_TwoStage_Queries_Each_Index_And_Merges_Detail() // Req 5 (WIM)
    {
        var path = MakeImageFile("install.wim");
        var runner = new FakeProcessRunner
        {
            Responder = req =>
            {
                var idx = IndexFromArgs(req.Arguments);
                if (idx is null)
                {
                    return new ProcessResult { ExitCode = 0, StandardOutput = EnumTwo };
                }

                return new ProcessResult { ExitCode = 0, StandardOutput = idx == 1 ? DetailHome : DetailPro };
            }
        };
        var service = new WindowsImageMetadataService(runner, new InMemoryLoggerService());

        var result = await service.InspectAsync(path);

        Assert.Equal(WindowsImageType.Wim, result.ImageType);
        Assert.Equal(WindowsImageMetadataStatus.Completed, result.Status);
        Assert.Equal(2, result.Editions.Count);

        // Exactly one enumeration call + one detail call per index.
        Assert.Equal(3, runner.Requests.Count);
        Assert.DoesNotContain("/Index", runner.Requests[0].Arguments); // enumeration first
        Assert.Contains("/Index:1", runner.Requests[1].Arguments);
        Assert.Contains("/Index:2", runner.Requests[2].Arguments);

        foreach (var req in runner.Requests)
        {
            Assert.Equal("dism.exe", req.FileName);
            Assert.Contains("/English", req.Arguments);
            Assert.Contains("/Get-WimInfo", req.Arguments);
            Assert.Contains("/ImageFile:", req.Arguments);
        }

        // Detail is merged onto the enumerated edition.
        var home = result.Editions[0];
        Assert.Equal(WindowsEditionDetailStatus.Queried, home.DetailStatus);
        Assert.Equal("x64", home.Architecture);
        Assert.Equal("26100", home.Build);

        var pro = result.Editions[1];
        Assert.Equal(WindowsEditionDetailStatus.Queried, pro.DetailStatus);
        Assert.Equal("Windows 11 Pro", pro.Name);
    }

    [Fact]
    public async Task Esd_TwoStage_Uses_Same_Flow() // Req 17
    {
        var path = MakeImageFile("install.esd");
        var runner = new FakeProcessRunner
        {
            Responder = req =>
            {
                var idx = IndexFromArgs(req.Arguments);
                if (idx is null)
                {
                    return new ProcessResult { ExitCode = 0, StandardOutput = EnumTwo };
                }

                return new ProcessResult { ExitCode = 0, StandardOutput = idx == 1 ? DetailHome : DetailPro };
            }
        };
        var service = new WindowsImageMetadataService(runner, new InMemoryLoggerService());

        var result = await service.InspectAsync(path);

        Assert.Equal(WindowsImageType.Esd, result.ImageType);
        Assert.Equal(WindowsImageMetadataStatus.Completed, result.Status);
        Assert.Equal(2, result.Editions.Count);
        Assert.Equal(3, runner.Requests.Count); // 1 enum + 2 detail
        Assert.DoesNotContain("/Index", runner.Requests[0].Arguments);
        Assert.Contains("/Index:1", runner.Requests[1].Arguments);
        Assert.Contains("/Index:2", runner.Requests[2].Arguments);
    }

    [Fact]
    public async Task NonSequential_Indexes_Each_Get_Own_Detail_Query() // Req 3 + 5
    {
        var path = MakeImageFile("install.wim");
        var runner = new FakeProcessRunner
        {
            Responder = req =>
            {
                var idx = IndexFromArgs(req.Arguments);
                if (idx is null)
                {
                    return new ProcessResult { ExitCode = 0, StandardOutput = EnumNonSequential };
                }

                // Indexes are 1 and 6 — never 2..5.
                return new ProcessResult { ExitCode = 0, StandardOutput = idx == 1 ? DetailHome : DetailPro };
            }
        };
        var service = new WindowsImageMetadataService(runner, new InMemoryLoggerService());

        var result = await service.InspectAsync(path);

        Assert.Equal(WindowsImageMetadataStatus.Completed, result.Status);
        Assert.Equal(new[] { 1, 6 }, result.Editions.ConvertAll(e => e.Index).ToArray());

        // The detail queries target the actual (non-sequential) indexes only.
        var detailArgs = runner.Requests
            .Where(r => IndexFromArgs(r.Arguments).HasValue)
            .Select(r => IndexFromArgs(r.Arguments)!.Value)
            .ToList();
        Assert.Equal(new[] { 1, 6 }, detailArgs.ToArray());
        Assert.DoesNotContain(detailArgs, i => i is >= 2 and <= 5);
    }

    [Fact]
    public async Task Enumeration_Failure_Returns_Failed_And_Skips_Detail_Queries() // Req 13
    {
        var path = MakeImageFile("install.wim");
        var runner = new FakeProcessRunner
        {
            Responder = req => new ProcessResult { ExitCode = 1, StandardOutput = "", StandardError = "0x80070057" }
        };
        var service = new WindowsImageMetadataService(runner, new InMemoryLoggerService());

        var result = await service.InspectAsync(path);

        Assert.Equal(WindowsImageMetadataStatus.Failed, result.Status);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        // No detail queries are attempted once enumeration fails.
        Assert.Single(runner.Requests);
        Assert.DoesNotContain("/Index", runner.Requests[0].Arguments);
    }

    [Fact]
    public async Task Enumeration_NonZero_Stays_Friendly_Without_Leaking_Stderr()
    {
        var path = MakeImageFile("install.wim");
        var runner = new FakeProcessRunner
        {
            Responder = req => new ProcessResult { ExitCode = 1, StandardOutput = "", StandardError = "0x80070057 HRESULT boom" }
        };
        var service = new WindowsImageMetadataService(runner, new InMemoryLoggerService());

        var result = await service.InspectAsync(path);

        Assert.Equal(WindowsImageMetadataStatus.Failed, result.Status);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.DoesNotContain("HRESULT", result.ErrorMessage!);
        Assert.DoesNotContain("boom", result.ErrorMessage!);
    }

    [Fact]
    public async Task One_Index_Detail_Failure_Preserves_Edition_And_Nulls_Detail() // Req 14
    {
        var path = MakeImageFile("install.wim");
        var runner = new FakeProcessRunner
        {
            Responder = req =>
            {
                var idx = IndexFromArgs(req.Arguments);
                if (idx is null)
                {
                    return new ProcessResult { ExitCode = 0, StandardOutput = EnumTwo };
                }

                // Index 1 fails; Index 2 succeeds.
                if (idx == 1)
                {
                    return new ProcessResult { ExitCode = 1, StandardOutput = "", StandardError = "detail failed" };
                }

                return new ProcessResult { ExitCode = 0, StandardOutput = DetailPro };
            }
        };
        var service = new WindowsImageMetadataService(runner, new InMemoryLoggerService());

        var result = await service.InspectAsync(path);

        // Enumeration succeeded, so the overall result is still Completed.
        Assert.Equal(WindowsImageMetadataStatus.Completed, result.Status);
        Assert.Equal(2, result.Editions.Count);
        Assert.Equal(3, runner.Requests.Count);

        var failed = result.Editions[0];
        Assert.Equal(1, failed.Index);
        Assert.Equal(WindowsEditionDetailStatus.Failed, failed.DetailStatus);
        Assert.Null(failed.Architecture); // detail not fabricated
        Assert.Null(failed.Version);
        Assert.False(string.IsNullOrEmpty(failed.DetailErrorMessage));

        var ok = result.Editions[1];
        Assert.Equal(WindowsEditionDetailStatus.Queried, ok.DetailStatus);
        Assert.Equal("x64", ok.Architecture);
    }

    [Fact]
    public async Task Cancellation_During_Detail_Query_Propagates() // Req 15
    {
        var path = MakeImageFile("install.wim");
        var runner = new FakeProcessRunner
        {
            Responder = req =>
            {
                // Enumeration succeeds; the first detail query is cancelled.
                if (IndexFromArgs(req.Arguments) is null)
                {
                    return new ProcessResult { ExitCode = 0, StandardOutput = EnumTwo };
                }

                throw new OperationCanceledException();
            }
        };
        var service = new WindowsImageMetadataService(runner, new InMemoryLoggerService());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.InspectAsync(path, new CancellationToken(true)));
    }

    [Fact]
    public async Task Missing_Tooling_Returns_Friendly_Failure()
    {
        var path = MakeImageFile("install.wim");
        var runner = new FakeProcessRunner { Throw = new InvalidOperationException("dism.exe could not be started") };
        var service = new WindowsImageMetadataService(runner, new InMemoryLoggerService());

        var result = await service.InspectAsync(path);

        Assert.Equal(WindowsImageMetadataStatus.Failed, result.Status);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    private string MakeImageFile(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public Exception? Throw { get; set; }
        public Func<ProcessRequest, ProcessResult>? Responder { get; set; }
        public ProcessResult Default { get; set; } = new ProcessResult { ExitCode = 0, StandardOutput = string.Empty };

        private readonly List<ProcessRequest> _requests = new();
        public IReadOnlyList<ProcessRequest> Requests => _requests;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            _requests.Add(request);
            if (Throw is not null)
            {
                return Task.FromException<ProcessResult>(Throw);
            }

            if (Responder is not null)
            {
                return Task.FromResult(Responder(request));
            }

            return Task.FromResult(Default);
        }
    }
}
