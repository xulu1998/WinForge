using System.Collections.Generic;
using System.IO;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Build;

/// <summary>
/// Windows implementation of <see cref="IFileSystem"/> backed by
/// <see cref="System.IO"/>. This is the ONLY place build/media code touches the
/// real filesystem; tests supply a fake <see cref="IFileSystem"/> instead.
/// </summary>
public sealed class WindowsFileSystem : IFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);

    public void CreateDirectory(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            Directory.CreateDirectory(path);
        }
    }

    public void CopyFile(string source, string destination, bool overwrite)
        => File.Copy(source, destination, overwrite);

    public void MoveFile(string source, string destination)
        => File.Move(source, destination, true);

    public long GetFileSize(string path)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        return new FileInfo(path).Length;
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
        }
    }

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, SearchOption option)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, searchPattern, option)
            : System.Array.Empty<string>();

    public IEnumerable<string> EnumerateDirectories(string directory)
        => Directory.Exists(directory)
            ? Directory.EnumerateDirectories(directory)
            : System.Array.Empty<string>();

    public string GetTempPath() => Path.GetTempPath();

    public string PathCombine(params string[] segments) => Path.Combine(segments);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);
}
