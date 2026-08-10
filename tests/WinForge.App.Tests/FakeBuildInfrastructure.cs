using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.App.Tests;

/// <summary>
/// Shared no-op fakes for the Build / ISO export pipeline so <see cref="BuildStepViewModel"/>
/// can be constructed and the workflow gating exercised in headless tests without a
/// real ISO, mount, or Windows ADK. They mirror the real services' contracts but
/// never touch the filesystem or external tools.
/// </summary>
internal sealed class FakeBuildService : IBuildService
{
    public Task<BuildResult> BuildAsync(
        BuildRequest request, IProgress<BuildProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.FromResult(BuildResult.Fail(
            BuildState.Preflight, "FakeBuildService: build is not exercised by this test.", System.Array.Empty<string>()));

    public Task<BuildRecoveryState?> DetectInterruptedBuildAsync(
        string buildWorkspaceDirectory, CancellationToken cancellationToken = default)
        => Task.FromResult<BuildRecoveryState?>(null);

    public Task<bool> CleanupInterruptedBuildAsync(
        string buildWorkspaceDirectory, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}

internal sealed class FakeFileSystem : IFileSystem
{
    public bool DirectoryExists(string path) => false;
    public bool FileExists(string path) => false;
    public void CreateDirectory(string path) { }
    public void CopyFile(string source, string destination, bool overwrite) { }
    public void MoveFile(string source, string destination) { }
    public long GetFileSize(string path) => 0;
    public void DeleteFile(string path) { }
    public void DeleteDirectory(string path, bool recursive) { }
    public string ReadAllText(string path) => string.Empty;
    public void WriteAllText(string path, string contents) { }
    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, SearchOption option)
        => System.Array.Empty<string>();
    public IEnumerable<string> EnumerateDirectories(string directory) => System.Array.Empty<string>();
    public string GetTempPath() => Path.GetTempPath();
    public string PathCombine(params string[] segments) => Path.Combine(segments);
    public System.IO.FileAttributes GetAttributes(string path) => System.IO.FileAttributes.Normal;
    public void SetAttributes(string path, System.IO.FileAttributes attributes) { }
}

internal sealed class FakeAdkToolLocator : IAdkToolLocator
{
    public string? FindOscdimg() => @"C:\fake\adk\oscdimg.exe";
    public bool IsAvailable() => true;
}

internal sealed class FakeLocalizationService : ILocalizationService
{
    public System.Globalization.CultureInfo CurrentCulture => System.Globalization.CultureInfo.GetCultureInfo("en");
    public event System.EventHandler? CultureChanged { add { } remove { } }
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public string this[string key] => key; // fall back to the key itself, like the real service
    public void SetCulture(System.Globalization.CultureInfo culture) { }
    public bool Contains(string key) => false;
}
