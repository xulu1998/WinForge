using System.Collections.Generic;
using System.IO;

namespace WinForge.Core.Services;

/// <summary>
/// Platform-agnostic filesystem abstraction. Core declares this so Windows file
/// operations (used by the build media preparer, verifier, and cleanup) are fully
/// testable behind a fake. Infrastructure provides the real implementation; it
/// is the ONLY place that references <see cref="System.IO"/> for these operations.
/// </summary>
public interface IFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    void CreateDirectory(string path);
    void CopyFile(string source, string destination, bool overwrite);
    void MoveFile(string source, string destination);
    long GetFileSize(string path);
    void DeleteFile(string path);
    void DeleteDirectory(string path, bool recursive);
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);

    /// <summary>Enumerates files under <paramref name="directory"/> matching <paramref name="searchPattern"/>.</summary>
    IEnumerable<string> EnumerateFiles(string directory, string searchPattern, SearchOption option);

    /// <summary>Enumerates immediate subdirectories of <paramref name="directory"/>.</summary>
    IEnumerable<string> EnumerateDirectories(string directory);

    /// <summary>Path to the system temporary directory.</summary>
    string GetTempPath();

    /// <summary>Combines path segments using the platform directory separator.</summary>
    string PathCombine(params string[] segments);
}
