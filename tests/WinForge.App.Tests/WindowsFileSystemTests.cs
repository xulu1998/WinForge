using System;
using System.IO;
using WinForge.Infrastructure.Build;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Real-filesystem tests for <see cref="WindowsFileSystem"/> reproducing the
/// real-desktop Phase 10 defect: a mounted Windows ISO carries ReadOnly (and
/// System/Hidden) attributes on files such as <c>autorun.inf</c>. The build media
/// tree must be a writable copy, and cleanup/overwrite must tolerate protected
/// files. These run against a real temp directory, not a fake.
/// </summary>
public sealed class WindowsFileSystemTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WinForgeFsTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Write(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    /// Recursively strips ReadOnly/System/Hidden from a temp tree so the test's own
    /// cleanup can delete it. A mounted-ISO file (e.g. a ReadOnly autorun.inf) cannot
    /// be removed by <see cref="Directory.Delete(string, bool)"/> while protected.
    /// </summary>
    private static void ForceDelete(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best effort */ }
        }

        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(dir, FileAttributes.Normal); } catch { /* best effort */ }
        }

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void CopyFile_ClearsReadOnlyOnDestination_WhenSourceIsReadOnly()
    {
        var root = NewTempDir();
        try
        {
            var src = Path.Combine(root, "autorun.inf");
            var dst = Path.Combine(root, "out", "autorun.inf");
            Write(src);
            Directory.CreateDirectory(Path.Combine(root, "out"));
            // Mounted source ISO files are ReadOnly.
            File.SetAttributes(src, FileAttributes.ReadOnly);

            var fs = new WindowsFileSystem();
            fs.CopyFile(src, dst, overwrite: false);

            // Destination copy must be writable so WinForge can replace/clean it.
            Assert.False((File.GetAttributes(dst) & FileAttributes.ReadOnly) != 0);
            // Source is never modified.
            Assert.True((File.GetAttributes(src) & FileAttributes.ReadOnly) != 0);
        }
        finally
        {
            ForceDelete(root);
        }
    }

    [Fact]
    public void CopyFile_OverwritesAnExistingReadOnlyDestination()
    {
        // Exact repro of the real-desktop failure: a previous run left a ReadOnly
        // copy of autorun.inf in the media tree; a retry must overwrite it.
        var root = NewTempDir();
        try
        {
            var src = Path.Combine(root, "autorun.inf");
            var dst = Path.Combine(root, "out", "autorun.inf");
            Write(src);
            Directory.CreateDirectory(Path.Combine(root, "out"));
            File.SetAttributes(src, FileAttributes.ReadOnly);

            var fs = new WindowsFileSystem();
            fs.CopyFile(src, dst, overwrite: false);
            // Simulate the legacy behavior: the prior copy remained ReadOnly.
            File.SetAttributes(dst, FileAttributes.ReadOnly);

            // Must NOT throw "Access to the path 'autorun.inf' is denied."
            var ex = Record.Exception(() => fs.CopyFile(src, dst, overwrite: true));
            Assert.Null(ex);
            Assert.False((File.GetAttributes(dst) & FileAttributes.ReadOnly) != 0);
        }
        finally
        {
            ForceDelete(root);
        }
    }

    [Fact]
    public void CopyFile_ClearsSystemAndHiddenOnDestination()
    {
        var root = NewTempDir();
        try
        {
            var src = Path.Combine(root, "autorun.inf");
            var dst = Path.Combine(root, "out", "autorun.inf");
            Write(src);
            Directory.CreateDirectory(Path.Combine(root, "out"));
            File.SetAttributes(src, FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden);

            var fs = new WindowsFileSystem();
            fs.CopyFile(src, dst, overwrite: false);

            var dstAttrs = File.GetAttributes(dst);
            Assert.False((dstAttrs & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0);
            // Source attributes are untouched.
            Assert.True((File.GetAttributes(src) & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0);
        }
        finally
        {
            ForceDelete(root);
        }
    }

    [Fact]
    public void DeleteFile_RemovesAReadOnlyFile()
    {
        var root = NewTempDir();
        try
        {
            var path = Path.Combine(root, "autorun.inf");
            Write(path);
            File.SetAttributes(path, FileAttributes.ReadOnly);

            var fs = new WindowsFileSystem();
            var ex = Record.Exception(() => fs.DeleteFile(path));
            Assert.Null(ex);
            Assert.False(File.Exists(path));
        }
        finally
        {
            ForceDelete(root);
        }
    }

    [Fact]
    public void DeleteDirectory_Recursive_RemovesTreeWithReadOnlyFiles()
    {
        // A failed previous media tree can contain many ReadOnly files.
        var root = NewTempDir();
        try
        {
            Write(Path.Combine(root, "media", "autorun.inf"));
            File.SetAttributes(Path.Combine(root, "media", "autorun.inf"), FileAttributes.ReadOnly);
            Write(Path.Combine(root, "media", "boot", "etfsboot.com"));
            File.SetAttributes(Path.Combine(root, "media", "boot", "etfsboot.com"), FileAttributes.ReadOnly);
            Write(Path.Combine(root, "media", "sources", "install.wim"));
            File.SetAttributes(Path.Combine(root, "media", "sources", "install.wim"), FileAttributes.ReadOnly);
            File.SetAttributes(Path.Combine(root, "media", "sources"), FileAttributes.ReadOnly);

            var fs = new WindowsFileSystem();
            var ex = Record.Exception(() => fs.DeleteDirectory(Path.Combine(root, "media"), recursive: true));
            Assert.Null(ex);
            Assert.False(Directory.Exists(Path.Combine(root, "media")));
        }
        finally
        {
            ForceDelete(root);
        }
    }
}
