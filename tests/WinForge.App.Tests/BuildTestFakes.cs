using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinForge.App.Services;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.App.Tests;

/// <summary>
/// In-memory <see cref="IFileSystem"/> for Build-pipeline tests. It emulates a
/// directory tree (so <see cref="IIsoMediaPreparer"/> can be exercised without a
/// real ISO), supports file sizes, <see cref="MoveFile"/> renames, and records
/// every created/deleted directory and deleted file so cleanup assertions are
/// possible.
/// </summary>
internal sealed class RecordingFileSystem : IFileSystem
{
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _sizes = new(StringComparer.OrdinalIgnoreCase);

    public readonly List<string> CreatedDirectories = new();
    public readonly List<string> DeletedDirectories = new();
    public readonly List<string> DeletedFiles = new();

    private string _temp = Path.Combine(Path.GetTempPath(), "WinForgeTest");

    public void SeedFile(string path, long size = 0)
    {
        _files.Add(Norm(path));
        if (size > 0)
        {
            _sizes[Norm(path)] = size;
        }
    }

    public void SeedDir(string path) => _dirs.Add(Norm(path));

    public void SetTemp(string path) => _temp = path;

    private static string Norm(string p) => (p ?? string.Empty).Replace('/', '\\').TrimEnd('\\');

    public bool DirectoryExists(string path)
    {
        var p = Norm(path);
        if (_dirs.Contains(p))
        {
            return true;
        }

        return _files.Any(f => IsUnder(f, p)) || _dirs.Any(d => IsUnder(d, p));
    }

    public bool FileExists(string path) => _files.Contains(Norm(path));

    public void CreateDirectory(string path)
    {
        _dirs.Add(Norm(path));
        CreatedDirectories.Add(Norm(path));
    }

    public void CopyFile(string source, string destination, bool overwrite) => _files.Add(Norm(destination));

    public void MoveFile(string source, string destination)
    {
        var ns = Norm(source);
        var nd = Norm(destination);
        _files.Remove(ns);
        _files.Add(nd);
        if (_sizes.Remove(ns, out var sz))
        {
            _sizes[nd] = sz;
        }
    }

    public long GetFileSize(string path) => _sizes.TryGetValue(Norm(path), out var sz) ? sz : 0;

    public void DeleteFile(string path)
    {
        var n = Norm(path);
        _files.Remove(n);
        _sizes.Remove(n);
        DeletedFiles.Add(n);
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        var n = Norm(path);
        _dirs.Remove(n);
        DeletedDirectories.Add(n);
    }

    public string ReadAllText(string path) => string.Empty;

    public void WriteAllText(string path, string contents) => _files.Add(Norm(path));

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, SearchOption option)
    {
        var p = Norm(directory);
        return _files.Where(f => Norm(Path.GetDirectoryName(f) ?? string.Empty) == p).ToList();
    }

    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        var p = Norm(directory);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in _files)
        {
            foreach (var ancestor in Ancestors(f))
            {
                if (Norm(Path.GetDirectoryName(ancestor) ?? string.Empty) == p)
                {
                    result.Add(ancestor);
                }
            }
        }

        foreach (var d in _dirs)
        {
            if (Norm(Path.GetDirectoryName(d) ?? string.Empty) == p)
            {
                result.Add(d);
            }
        }

        return result;
    }

    public string GetTempPath() => _temp;

    public string PathCombine(params string[] segments) => Path.Combine(segments);

    private static IEnumerable<string> Ancestors(string path)
    {
        var parent = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(parent))
        {
            var n = Norm(parent);
            yield return n;
            parent = Path.GetDirectoryName(parent);
        }
    }

    private static bool IsUnder(string candidate, string parent)
    {
        var p = Norm(parent);
        return candidate.Length > p.Length
               && candidate.StartsWith(p, StringComparison.OrdinalIgnoreCase)
               && candidate[p.Length] == '\\';
    }
}

/// <summary>Configurable <see cref="IImageServicingService"/> for pipeline tests.</summary>
internal sealed class ConfigurableServicingService : IImageServicingService
{
    public bool CommitSucceeds { get; set; } = true;
    public bool ThrowOnCancel { get; set; } = true;
    public ServicingHealth Health { get; set; } = ServicingHealth.Prepared;
    public int CommitCalls { get; private set; }

    public Task<ServicingResult> PrepareWorkingImageAsync(ImageWorkspace source, string workspaceId, CancellationToken ct = default)
        => Task.FromResult(ServicingResult.Ok(new ImageServicingWorkspace(), ServicingHealth.Prepared));

    public Task<ServicingResult> MountAsync(ImageServicingWorkspace workspace, CancellationToken ct = default)
        => Task.FromResult(ServicingResult.Ok(workspace, ServicingHealth.Mounted));

    public Task<ServicingResult> UnmountDiscardAsync(ImageServicingWorkspace workspace, CancellationToken ct = default)
        => Task.FromResult(ServicingResult.Ok(workspace, ServicingHealth.Prepared));

    public Task<ServicingResult> CommitUnmountAsync(ImageServicingWorkspace workspace, CancellationToken ct = default)
    {
        CommitCalls++;
        if (ct.IsCancellationRequested && ThrowOnCancel)
        {
            throw new OperationCanceledException();
        }

        return CommitSucceeds
            ? Task.FromResult(ServicingResult.Ok(workspace, Health))
            : Task.FromResult(ServicingResult.Fail(workspace, "Commit failed", ServicingHealth.Failed));
    }

    public Task<ServicingResult> ValidateServicingWorkspaceAsync(ImageServicingWorkspace workspace, CancellationToken ct = default)
        => Task.FromResult(ServicingResult.Ok(workspace, ServicingHealth.Prepared));
}

/// <summary>Configurable <see cref="IWimExporter"/> for pipeline tests.</summary>
internal sealed class ConfigurableWimExporter : IWimExporter
{
    public bool Succeeds { get; set; } = true;
    public int ExitCode { get; set; }
    public WimExportRequest? LastRequest { get; private set; }
    public int Calls { get; private set; }

    public Task<WimExportResult> ExportAsync(WimExportRequest request, CancellationToken ct = default)
    {
        LastRequest = request;
        Calls++;
        return Succeeds
            ? Task.FromResult(WimExportResult.Ok(request.DestinationImagePath, request.SourceIndex))
            : Task.FromResult(WimExportResult.Fail("export failed", ExitCode));
    }
}

/// <summary>Configurable <see cref="IIsoMediaPreparer"/> for pipeline tests.</summary>
internal sealed class ConfigurableMediaPreparer : IIsoMediaPreparer
{
    public bool Succeeds { get; set; } = true;
    public bool BootFilesPresent { get; set; } = true;
    public MediaPrepareRequest? LastRequest { get; private set; }
    public int Calls { get; private set; }
    public string MediaRoot { get; set; } = @"C:\build\media";
    public string InstallImagePath { get; set; } = @"C:\build\media\sources\install.wim";

    public Task<MediaPrepareResult> PrepareAsync(MediaPrepareRequest request, CancellationToken ct = default)
    {
        LastRequest = request;
        Calls++;
        return Succeeds
            ? Task.FromResult(MediaPrepareResult.Ok(MediaRoot, InstallImagePath, BootFilesPresent))
            : Task.FromResult(MediaPrepareResult.Fail("media prepare failed"));
    }
}

/// <summary>Configurable <see cref="IBootableIsoBuilder"/> for pipeline tests.</summary>
internal sealed class ConfigurableIsoBuilder : IBootableIsoBuilder
{
    public bool Succeeds { get; set; } = true;
    public bool ToolMissing { get; set; }
    public int ExitCode { get; set; }
    public long ReportedSize { get; set; } = 1_234_567;
    public IsoBuildRequest? LastRequest { get; private set; }
    public int Calls { get; private set; }

    private readonly RecordingFileSystem _fs;

    public ConfigurableIsoBuilder(RecordingFileSystem fs) => _fs = fs;

    public Task<IsoBuildResult> BuildAsync(IsoBuildRequest request, CancellationToken ct = default)
    {
        LastRequest = request;
        Calls++;
        if (ToolMissing)
        {
            return Task.FromResult(IsoBuildResult.ToolNotFound());
        }

        if (!Succeeds)
        {
            return Task.FromResult(IsoBuildResult.Fail("iso build failed", ExitCode));
        }

        // Emulate the tool writing the .partial file.
        _fs.SeedFile(request.OutputIsoPath, ReportedSize);
        return Task.FromResult(IsoBuildResult.Ok(request.OutputIsoPath));
    }
}

/// <summary>Configurable <see cref="IBuildVerifier"/> for pipeline tests.</summary>
internal sealed class ConfigurableVerifier : IBuildVerifier
{
    public bool Succeeds { get; set; } = true;
    public BuildVerificationRequest? LastRequest { get; private set; }
    public int Calls { get; private set; }

    public Task<BuildVerificationResult> VerifyAsync(BuildVerificationRequest request, CancellationToken ct = default)
    {
        LastRequest = request;
        Calls++;
        return Succeeds
            ? Task.FromResult(BuildVerificationResult.Pass(1_234_567, true, true))
            : Task.FromResult(BuildVerificationResult.Fail(
                "verification failed", outputExists: true, outputSize: 1_234_567, installWimPresent: true));
    }
}

/// <summary><see cref="IAdkToolLocator"/> that reports the tool is missing.</summary>
internal sealed class MissingAdkToolLocator : IAdkToolLocator
{
    public string? FindOscdimg() => null;
    public bool IsAvailable() => false;
}

/// <summary><see cref="IBuildService"/> that always reports success (ViewModel tests).</summary>
internal sealed class SuccessBuildService : IBuildService
{
    public BuildRequest? LastRequest { get; private set; }

    public Task<BuildResult> BuildAsync(BuildRequest request, IProgress<BuildProgress>? progress = null, CancellationToken ct = default)
    {
        LastRequest = request;
        progress?.Report(BuildProgress.Of(BuildState.Completed, "Build completed", 100));
        return Task.FromResult(BuildResult.Ok(@"C:\out\WinForge_Pro_20260810-1200.iso", 1_234_567, new[] { "Build completed" }));
    }

    public Task<BuildRecoveryState?> DetectInterruptedBuildAsync(string dir, CancellationToken ct = default)
        => Task.FromResult<BuildRecoveryState?>(null);

    public Task<bool> CleanupInterruptedBuildAsync(string dir, CancellationToken ct = default)
        => Task.FromResult(true);
}

/// <summary>Folder/ISO picker fake returning null (user cancelled) for tests.</summary>
internal sealed class FakeFilePicker : IFilePicker
{
    public string? PickIsoFile() => null;
    public string? PickFolder() => null;
}

/// <summary>
/// <see cref="IBuildService"/> that blocks inside the build until cancelled, so the
/// ViewModel cancel path can be exercised deterministically.
/// </summary>
internal sealed class SlowCancellableBuildService : IBuildService
{
    public readonly TaskCompletionSource<bool> Started = new();
    public CancellationToken CapturedToken { get; private set; }

    public Task<BuildResult> BuildAsync(BuildRequest request, IProgress<BuildProgress>? progress = null, CancellationToken ct = default)
    {
        CapturedToken = ct;
        Started.TrySetResult(true);
        return Task.Run(async () =>
        {
            // Throws OperationCanceledException when the token is cancelled.
            await Task.Delay(Timeout.Infinite, ct);
            return BuildResult.Ok(@"C:\out\i.iso", 1, System.Array.Empty<string>());
        }, ct);
    }

    public Task<BuildRecoveryState?> DetectInterruptedBuildAsync(string dir, CancellationToken ct = default)
        => Task.FromResult<BuildRecoveryState?>(null);

    public Task<bool> CleanupInterruptedBuildAsync(string dir, CancellationToken ct = default)
        => Task.FromResult(true);
}

