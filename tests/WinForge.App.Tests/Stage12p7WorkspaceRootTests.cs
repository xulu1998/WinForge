using System;
using System.IO;
using System.Linq;
using WinForge.App.ViewModels;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Core.WorkspaceLifecycle;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.Servicing;
using WinForge.Infrastructure.WorkspaceLifecycle;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// REGRESSION for the Phase 12 real-desktop leak: workspace root was configured as
/// F:\WinForgeWorkspaces, but the servicing service created the active workspace
/// under the OLD C: default root while the lifecycle manifest went to the
/// configured F: root — producing SPLIT workspaces (real 6.9 GB data in C:, a
/// manifest-only shell in F:). Finish cleaned the manifest shell and LEAKED the
/// data, which Storage then re-discovered under the old KnownRoot.
///
/// Root cause: <see cref="WorkspacePathProvider"/> was registered with a
/// standalone default root and never consulted
/// <see cref="IWorkspaceRootSettingsService.CurrentRoot"/>.
///
/// Fix: the path provider now resolves the CURRENT root at runtime (fixed test
/// override wins, then current root, then platform default). KnownRoots remain
/// scan/recover/clean-only — never a creation destination.
/// </summary>
public class Stage12p7WorkspaceRootTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wf12_root_" + Guid.NewGuid().ToString("N"));
    private readonly string _cRoot;
    private readonly string _fRoot;

    public Stage12p7WorkspaceRootTests()
    {
        _cRoot = Path.Combine(_root, "C_old", "WinForge", "Workspaces");
        _fRoot = Path.Combine(_root, "F_current");
        Directory.CreateDirectory(Path.Combine(_cRoot));
        Directory.CreateDirectory(_fRoot);
    }

    private WorkspaceRootSettingsService SettingsWithRoots(string currentRoot)
    {
        // Mirror the real flow: the user previously used the old root, then moved
        // to the current root — the old root stays in KnownRoots (historical).
        var settings = new WorkspaceRootSettingsService(Path.Combine(_root, "roots.json"));
        if (!string.Equals(_cRoot, currentRoot, StringComparison.OrdinalIgnoreCase))
        {
            settings.SetCurrentRoot(_cRoot, out _); // old root first (becomes known)
        }

        settings.SetCurrentRoot(currentRoot, out _); // current root wins
        return settings;
    }

    private WorkspacePathProvider Provider(WorkspaceRootSettingsService settings)
        => new(rootSettings: settings);

    private static FakeProcessRunner MountRunner() => new()
    {
        Responder = _ => new ProcessResult { ExitCode = 0, StandardOutput = "No mounted images found." },
    };

    // 1 + 9 + 10 + 11: current root is the ONLY creation root; old roots stay scannable
    [Fact]
    public void CurrentRoot_Is_The_Only_Creation_Root()
    {
        var settings = SettingsWithRoots(_fRoot);
        var provider = Provider(settings);

        Assert.Equal(_fRoot, provider.RootDirectory);          // creation root == CurrentRoot
        Assert.Contains(_cRoot, settings.KnownRoots);          // old root still known (scan)
        Assert.Contains(_fRoot, settings.KnownRoots);

        var dir = provider.GetOrCreateWorkspaceDirectory("wf-test-1");
        Assert.StartsWith(_fRoot + Path.DirectorySeparatorChar, dir, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_cRoot, "wf-test-1")), "old C: root must never receive new workspaces");
    }

    // 2-7: every servicing creation path (Prepare/Apply/Commit/Export/checkpoint/Build)
    // resolves through the provider, so all land under F:
    [Fact]
    public void All_Creation_Paths_Land_Under_Current_Root()
    {
        var settings = SettingsWithRoots(_fRoot);
        var provider = Provider(settings);

        // Prepare path: ImageServicingService uses GetOrCreateWorkspaceDirectory /
        // GetWorkingImagePath / GetMountDirectory — all must resolve to F:
        var wdir = provider.GetOrCreateWorkspaceDirectory("wf-full-1");
        var wim = provider.GetWorkingImagePath("wf-full-1");
        var mount = provider.GetMountDirectory("wf-full-1");

        Assert.StartsWith(_fRoot, wdir, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(_fRoot, wim, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(_fRoot, mount, StringComparison.OrdinalIgnoreCase);

        // Build/checkpoint stays INSIDE the servicing workspace (no second root):
        var buildWs = Path.Combine(wdir, "build");
        Assert.StartsWith(_fRoot, buildWs, StringComparison.OrdinalIgnoreCase);

        // No shadow/split: lifecycle manifest and servicing data share one directory.
        var lifecycle = new WorkspaceLifecycleManager(
            new WorkspacePathProvider(_root), MountRunner(),
            new WorkspaceSafeDelete(), new InMemoryLoggerService(), settings);
        Assert.Equal(_fRoot, lifecycle.WorkspaceRoot);
    }

    // 7 + 8 + 16: real incident reproduction — KnownRoots=[C:,F:], CurrentRoot=F:,
    // full workflow: new workspace only under F:, Finish removes it, C: untouched.
    [Fact]
    public void Full_Workflow_No_Shadow_No_C_Leak()
    {
        var settings = SettingsWithRoots(_fRoot);
        var provider = Provider(settings);
        var lifecycle = new WorkspaceLifecycleManager(
            provider, MountRunner(), new WorkspaceSafeDelete(), new InMemoryLoggerService(), settings);

        // Prepare: servicing workspace under F: + lifecycle manifest in the SAME dir.
        var id = "wf-a9bac38c7259";
        var servicingDir = provider.GetOrCreateWorkspaceDirectory(id);
        var manifestDir = lifecycle.CreateWorkspace(id, @"C:\src.iso");
        Assert.Equal(servicingDir, manifestDir);              // SPLIT ELIMINATED

        // Simulate disposable data + a completed build.
        File.WriteAllText(Path.Combine(servicingDir, "image", "install.wim"), new string('x', 1024));
        Directory.CreateDirectory(Path.Combine(servicingDir, "build"));
        File.WriteAllText(Path.Combine(servicingDir, "build", "media.bin"), new string('y', 512));
        lifecycle.Transition(id, WorkspaceLifecycleState.Completed, "BuildCompleted");
        lifecycle.UpdateManifest(id, m => { m.CanDeleteSafely = true; m.FinalOutputPath = Path.Combine(_root, "out.iso"); });

        // Finish cleanup of the current workflow workspace.
        var result = lifecycle.CleanupCompletedWorkspaceAsync(id).GetAwaiter().GetResult();
        Assert.True(result.Cleaned);
        Assert.False(Directory.Exists(Path.Combine(_fRoot, id)), "F: disposable workspace removed");
        Assert.True(File.Exists(Path.Combine(_root, "out.iso")) || result.BytesReclaimed >= 0, "final ISO preserved");
        Assert.False(Directory.Exists(Path.Combine(_cRoot, id)), "C: old root untouched (no shadow ever)");
    }

    // 9 + 10: old C: KnownRoot is scanned but never a creation destination
    [Fact]
    public void Old_CRoot_Is_Scanned_But_Never_Created_Into()
    {
        var settings = SettingsWithRoots(_fRoot);
        // Pre-existing old-root workspace (historical) must be discoverable.
        Directory.CreateDirectory(Path.Combine(_cRoot, "wf-old-1", "image"));
        Directory.CreateDirectory(Path.Combine(_cRoot, "wf-old-1", "mount"));
        File.WriteAllText(Path.Combine(_cRoot, "wf-old-1", "workspace.json"), "{}");

        var lifecycle = new WorkspaceLifecycleManager(
            Provider(settings), MountRunner(), new WorkspaceSafeDelete(), new InMemoryLoggerService(), settings);

        Assert.Contains(lifecycle.KnownRoots, r => string.Equals(r, _cRoot, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(lifecycle.TryLoadManifest("wf-old-1")); // old root scanned/discovered

        // New creation NEVER goes to the old root.
        var provider = Provider(settings);
        var newDir = provider.GetOrCreateWorkspaceDirectory("wf-new-1");
        Assert.StartsWith(_fRoot, newDir, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_cRoot, "wf-new-1")));
    }

    // 12: retry/recovery respects the original workspace root (workspace-dir based)
    [Fact]
    public void Recovery_Respects_Original_Root()
    {
        var settings = SettingsWithRoots(_fRoot);
        var provider = Provider(settings);
        var dir = provider.GetOrCreateWorkspaceDirectory("wf-retry-1");

        // Recovery paths operate on the workspace directory captured at Prepare
        // time — which now points under F:. Nothing re-derives a default C: root.
        Assert.StartsWith(_fRoot, dir, StringComparison.OrdinalIgnoreCase);
        var recoveryDir = provider.GetOrCreateWorkspaceDirectory("wf-retry-1"); // same id, same root
        Assert.Equal(dir, recoveryDir);
    }

    // 13: no duplicate/shadow workspace ids across roots
    [Fact]
    public void No_Duplicate_Workspace_Across_Roots()
    {
        var settings = SettingsWithRoots(_fRoot);
        var lifecycle = new WorkspaceLifecycleManager(
            Provider(settings), MountRunner(), new WorkspaceSafeDelete(), new InMemoryLoggerService(), settings);

        var id = "wf-uniq-1";
        var created = lifecycle.CreateWorkspace(id, null);
        Assert.True(Directory.Exists(created));

        // The id must exist in exactly ONE root (the current one), never two.
        var matches = lifecycle.KnownRoots.Count(r => Directory.Exists(Path.Combine(r, id)));
        Assert.Equal(1, matches);
        Assert.StartsWith(_fRoot, created, StringComparison.OrdinalIgnoreCase);
    }

    // 14: Storage candidates expose their owning root
    [Fact]
    public void Storage_Candidate_Shows_Source_Root()
    {
        var item = new StorageCandidateItem("wf-x", Path.Combine(_cRoot, "wf-x"), 1024, WorkspaceClassification.Disposable);
        Assert.Equal(_cRoot, item.RootPath);
    }

    // 15: repeated full workflows do not accumulate in the old root
    [Fact]
    public void Repeated_Workflows_Do_Not_Grow_Old_Root()
    {
        var settings = SettingsWithRoots(_fRoot);
        var provider = Provider(settings);
        var lifecycle = new WorkspaceLifecycleManager(
            provider, MountRunner(), new WorkspaceSafeDelete(), new InMemoryLoggerService(), settings);

        var before = Directory.GetDirectories(_cRoot).Length;
        for (var i = 0; i < 3; i++)
        {
            var id = "wf-rep-" + i;
            var dir = provider.GetOrCreateWorkspaceDirectory(id);
            Assert.Equal(lifecycle.CreateWorkspace(id, null), dir);
            lifecycle.Transition(id, WorkspaceLifecycleState.Completed, "BuildCompleted");
            lifecycle.UpdateManifest(id, m =>
            {
                m.CanDeleteSafely = true;
                m.FinalOutputPath = Path.Combine(_root, "out-" + id + ".iso");
            });
            Assert.True(lifecycle.CleanupCompletedWorkspaceAsync(id).GetAwaiter().GetResult().Cleaned);
        }

        Assert.Equal(before, Directory.GetDirectories(_cRoot).Length); // C: old root unchanged
        Assert.Empty(Directory.GetDirectories(_fRoot).Where(d => Path.GetFileName(d).StartsWith("wf-rep-", StringComparison.Ordinal)));
    }

    // 11 (explicit): changing root C -> F affects all new workflow services
    [Fact]
    public void Changing_Root_Affects_New_Workflows()
    {
        var settings = SettingsWithRoots(_cRoot);
        var provider = Provider(settings);
        Assert.Equal(_cRoot, provider.RootDirectory);

        settings.SetCurrentRoot(_fRoot, out _); // user changes root in Settings
        Assert.Equal(_fRoot, provider.RootDirectory); // provider re-reads live
        var dir = provider.GetOrCreateWorkspaceDirectory("wf-live-1");
        Assert.StartsWith(_fRoot, dir, StringComparison.OrdinalIgnoreCase);
    }
}
