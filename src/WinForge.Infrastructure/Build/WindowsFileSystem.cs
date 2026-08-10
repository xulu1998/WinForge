using System.Collections.Generic;
using System.IO;
using WinForge.Core.Services;

namespace WinForge.Infrastructure.Build;

/// <summary>
/// Windows implementation of <see cref="IFileSystem"/> backed by
/// <see cref="System.IO"/>. This is the ONLY place build/media code touches the
/// real filesystem; tests supply a fake <see cref="IFileSystem"/> instead.
///
/// Media-copy contract: the SOURCE ISO is mounted read-only and its files
/// (e.g. <c>autorun.inf</c>) carry ReadOnly (and often System/Hidden) attributes.
/// When those are copied into the WinForge-owned build tree, the destination must
/// be writable so WinForge can later replace the payload (install.wim) and clean
/// up the tree. Windows' <see cref="File.Copy"/> preserves the ReadOnly attribute
/// and cannot overwrite a ReadOnly destination, which is exactly the real-desktop
/// failure "Access to the path 'autorun.inf' is denied." So every copy/delete here
/// normalizes attributes on the BUILD copy only — the source is never touched.
/// </summary>
public sealed class WindowsFileSystem : IFileSystem
{
    // Attributes that block WinForge from overwriting/replacing/deleting its own
    // build copy. These are always cleared on the destination; the source is left
    // untouched.
    private const FileAttributes BlockingAttributes =
        FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden;

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
    {
        // A previous (possibly failed) copy may have left a ReadOnly destination
        // (media files like autorun.inf carry ReadOnly from the mounted source ISO).
        // File.Copy cannot overwrite a ReadOnly destination, so clear it first.
        if (overwrite && File.Exists(destination))
        {
            ClearBlockingAttributes(destination);
        }

        File.Copy(source, destination, overwrite);

        // The copied build artifact must be writable for payload replacement and
        // cleanup. File.Copy preserves the source's ReadOnly/System/Hidden, so
        // normalize the destination copy.
        ClearBlockingAttributes(destination);
    }

    public void MoveFile(string source, string destination)
    {
        if (File.Exists(destination))
        {
            ClearBlockingAttributes(destination);
        }

        File.Move(source, destination, true);
    }

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
            ClearBlockingAttributes(path);
            File.Delete(path);
        }
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        if (Directory.Exists(path))
        {
            if (recursive)
            {
                DeleteTreeHandlingReadOnlyAttributes(path);
            }
            else
            {
                ClearBlockingAttributes(path);
                Directory.Delete(path, false);
            }
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

    public FileAttributes GetAttributes(string path)
    {
        if (File.Exists(path))
        {
            return File.GetAttributes(path);
        }

        if (Directory.Exists(path))
        {
            return File.GetAttributes(path);
        }

        return FileAttributes.Normal;
    }

    public void SetAttributes(string path, FileAttributes attributes)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            File.SetAttributes(path, attributes);
        }
    }

    /// <summary>
    /// Recursively clears ReadOnly (and System/Hidden) from every file and directory
    /// under <paramref name="path"/>, including the root itself, so a subsequent
    /// <see cref="Directory.Delete(string, bool)"/> with <c>recursive: true</c> never
    /// fails on a protected build artifact. Best-effort: a single inaccessible entry
    /// does not abort the whole cleanup.
    /// </summary>
    private static void DeleteTreeHandlingReadOnlyAttributes(string path)
    {
        try
        {
            var rootAttrs = File.GetAttributes(path);
            if ((rootAttrs & BlockingAttributes) != 0)
            {
                File.SetAttributes(path, rootAttrs & ~BlockingAttributes);
            }
        }
        catch
        {
            /* best effort */
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                ClearBlockingAttributes(file);
            }
        }
        catch
        {
            /* best effort */
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            {
                ClearBlockingAttributes(dir);
            }
        }
        catch
        {
            /* best effort */
        }

        Directory.Delete(path, recursive: true);
    }

    private static void ClearBlockingAttributes(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & BlockingAttributes) != 0)
            {
                File.SetAttributes(path, attrs & ~BlockingAttributes);
            }
        }
        catch
        {
            /* best effort: File.Copy/Delete will surface a precise error if it still fails */
        }
    }
}
