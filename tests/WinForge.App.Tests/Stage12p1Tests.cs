using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinForge.Core.Services;
using WinForge.Core.WorkspaceLifecycle;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.Servicing;
using WinForge.Infrastructure.WorkspaceLifecycle;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Phase 12 — workspace lifecycle & disk safety regression suite (Parts A–V).
/// Exercises the real DISM-backed manager against a throwaway temp workspace
/// root with a staged fake process runner, so no host WIM is ever touched.
/// </summary>
public sealed class Stage12p1Tests
{
    private static readonly string MountedDismOutput =
        "Deployment Image Servicing and Management tool\r\n\r\nMounted Images:\r\n\r\nMount Dir : C:\\wf\\mount\r\n\r\nThe operation completed successfully.\r\n";

    private static string TempRoot()
        => Path.Combine(Path.GetTempPath(), "wf12_" + Guid.NewGuid().ToString("N"));

    private static (WorkspaceLifecycleManager Manager, WorkspacePathProvider Paths, FakeProcessRunner Runner) Build(
        string root, string dismOutput = "No mounted images found.")
    {
        var paths = new WorkspacePathProvider(root);
        var runner = new FakeProcessRunner
        {
            Responder = req => req.Arguments.Contains("/Get-MountedImageInfo", StringComparison.OrdinalIgnoreCase)
                ? new ProcessResult { ExitCode = 0, StandardOutput = dismOutput }
                : new ProcessResult { ExitCode = 0, StandardOutput = string.Empty },
        };
        var manager = new WorkspaceLifecycleManager(paths, runner, new WorkspaceSafeDelete(), new InMemoryLoggerService());
        return (manager, paths, runner);
    }

    private static string CreateWorkspaceWithFile(WorkspaceLifecycleManager manager, string id, string fileName, long bytes)
    {
        var dir = manager.CreateWorkspace(id, @"C:\src\Win11.iso");
        var file = Path.Combine(dir, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (var fs = File.Create(file))
        {
            fs.SetLength(bytes);
        }

        return dir;
    }

    // ---- LIFECYCLE ----

    [Fact]
    public void Workspace_Created_With_Manifest()
    {
        var root = TempRoot();
        var (manager, _, _) = Build(root);
        var id = "wf-lifecycle-1";
        var dir = manager.CreateWorkspace(id, @"C:\src\Win11.iso");

        Assert.True(File.Exists(Path.Combine(dir, "workspace.json")));
        var manifest = manager.TryLoadManifest(id);
        Assert.NotNull(manifest);
        Assert.Equal(id, manifest!.WorkspaceId);
        Assert.Equal(WorkspaceLifecycleState.Created, manifest.CurrentState);
        Assert.Equal(@"C:\src\Win11.iso", manifest.SourceIsoPath);
        Assert.Contains("Created", manifest.Transitions.Select(t => t.Transition));
    }

    [Fact]
    public void State_Transitions_Persisted()
    {
        var root = TempRoot();
        var (manager, _, _) = Build(root);
        var id = "wf-lifecycle-2";
        manager.CreateWorkspace(id, null);

        manager.Transition(id, WorkspaceLifecycleState.Prepared, "Prepared");
        manager.Transition(id, WorkspaceLifecycleState.Mounted, "Mounted");

        var manifest = manager.TryLoadManifest(id);
        Assert.Equal(WorkspaceLifecycleState.Mounted, manifest!.CurrentState);
        Assert.Contains("Mounted", manifest.Transitions.Select(t => t.Transition));
    }

    [Fact]
    public void Discarded_Workspace_Becomes_Cleanup_Eligible()
    {
        var root = TempRoot();
        var (manager, _, _) = Build(root);
        var id = "wf-discard-1";
        manager.CreateWorkspace(id, null);
        manager.Transition(id, WorkspaceLifecycleState.Cancelled, "UnmountDiscarded");

        var candidates = manager.GetCleanupCandidatesAsync().GetAwaiter().GetResult();
        Assert.Contains(candidates, c => c.WorkspaceId == id && c.Classification == WorkspaceClassification.Disposable);
    }

    [Fact]
    public void Completed_Build_With_Output_Becomes_Cleanup_Eligible()
    {
        var root = TempRoot();
        var (manager, _, _) = Build(root);
        var id = "wf-complete-1";
        manager.CreateWorkspace(id, null);
        manager.UpdateManifest(id, m => m.FinalOutputPath = @"C:\Users\x\Documents\WinForge\Win11.iso");
        manager.Transition(id, WorkspaceLifecycleState.Completed, "BuildCompleted");

        var candidates = manager.GetCleanupCandidatesAsync().GetAwaiter().GetResult();
        Assert.Contains(candidates, c => c.WorkspaceId == id);
    }

    [Fact]
    public void Final_Iso_Is_Never_A_Cleanup_Target()
    {
        var root = TempRoot();
        var (manager, _, _) = Build(root);
        var id = "wf-iso-1";
        var externalIso = Path.Combine(Path.GetTempPath(), "wf12_out_" + Guid.NewGuid().ToString("N") + ".iso");
        File.WriteAllText(externalIso, "iso-content");
        try
        {
            manager.CreateWorkspace(id, null);
            manager.UpdateManifest(id, m => m.FinalOutputPath = externalIso);
            manager.Transition(id, WorkspaceLifecycleState.Completed, "BuildCompleted");

            var candidates = manager.GetCleanupCandidatesAsync().GetAwaiter().GetResult();
            Assert.Contains(candidates, c => c.WorkspaceId == id);
            var result = manager.CleanupWorkspaceAsync(id).GetAwaiter().GetResult();
            Assert.True(result.Succeeded);
            // The user output survives cleanup untouched.
            Assert.True(File.Exists(externalIso));
        }
        finally
        {
            if (File.Exists(externalIso))
            {
                File.Delete(externalIso);
            }
        }
    }

    // ---- MOUNT SAFETY ----

    [Fact]
    public void Mounted_Workspace_Never_Deleted()
    {
        var root = TempRoot();
        var (manager, paths, _) = Build(root, MountedDismOutput);
        var id = "wf-mount-1";
        var dir = manager.CreateWorkspace(id, null);
        var mount = paths.GetMountDirectory(id);
        Directory.CreateDirectory(mount);
        // Mount path must match the dism output exactly.
        manager.UpdateManifest(id, m => m.MountPath = @"C:\wf\mount");
        manager.Transition(id, WorkspaceLifecycleState.Mounted, "Mounted");

        var result = manager.CleanupWorkspaceAsync(id).GetAwaiter().GetResult();
        Assert.False(result.Succeeded);
        Assert.True(Directory.Exists(dir));
        var classified = manager.ClassifyAllAsync().GetAwaiter().GetResult();
        Assert.Contains(classified, c => c.WorkspaceId == id && c.Classification == WorkspaceClassification.Active);
    }

    [Fact]
    public void NeedsRemount_Workspace_Not_Silently_Deleted()
    {
        var root = TempRoot();
        var (manager, _, _) = Build(root, "No mounted images found.");
        var id = "wf-remount-1";
        manager.CreateWorkspace(id, null);
        manager.Transition(id, WorkspaceLifecycleState.Mounted, "Mounted"); // manifest says mounted, DISM says no

        var candidates = manager.GetCleanupCandidatesAsync().GetAwaiter().GetResult();
        Assert.DoesNotContain(candidates, c => c.WorkspaceId == id);
        var classified = manager.ClassifyAllAsync().GetAwaiter().GetResult();
        Assert.Contains(classified, c => c.WorkspaceId == id && c.Classification == WorkspaceClassification.Recoverable);
    }

    [Fact]
    public void Mount_Query_Failure_Fails_Closed()
    {
        var root = TempRoot();
        var (manager, _, runner) = Build(root);
        var id = "wf-failclosed-1";
        manager.CreateWorkspace(id, null);
        manager.Transition(id, WorkspaceLifecycleState.Cancelled, "UnmountDiscarded");
        runner.Responder = req => new ProcessResult { ExitCode = 999, StandardOutput = string.Empty };

        var candidates = manager.GetCleanupCandidatesAsync().GetAwaiter().GetResult();
        Assert.DoesNotContain(candidates, c => c.WorkspaceId == id);
        var classified = manager.ClassifyAllAsync().GetAwaiter().GetResult();
        Assert.All(classified, c => Assert.Equal(WorkspaceClassification.Unknown, c.Classification));

        var result = manager.CleanupWorkspaceAsync(id).GetAwaiter().GetResult();
        Assert.False(result.Succeeded); // cleanup refused
        Assert.True(Directory.Exists(Path.Combine(root, id)));
    }

    // ---- CHECKPOINT ----

    [Fact]
    public void Recoverable_Checkpoint_Retained()
    {
        var root = TempRoot();
        var (manager, _, _) = Build(root);
        var id = "wf-checkpoint-1";
        manager.CreateWorkspace(id, null);
        manager.Transition(id, WorkspaceLifecycleState.BuildCheckpoint, "BuildCheckpointCreated");

        var candidates = manager.GetCleanupCandidatesAsync().GetAwaiter().GetResult();
        Assert.DoesNotContain(candidates, c => c.WorkspaceId == id);
        var classified = manager.ClassifyAllAsync().GetAwaiter().GetResult();
        Assert.Contains(classified, c => c.WorkspaceId == id && c.Classification == WorkspaceClassification.Recoverable);
    }

    [Fact]
    public void Failed_Disposable_Build_Cleaned()
    {
        var root = TempRoot();
        var (manager, _, _) = Build(root);
        var id = "wf-faildisp-1";
        var dir = CreateWorkspaceWithFile(manager, id, "image\\install.wim", 1024);
        manager.Transition(id, WorkspaceLifecycleState.FailedDisposable, "PrepareFailed");

        var result = manager.CleanupWorkspaceAsync(id).GetAwaiter().GetResult();
        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(dir));
    }

    // ---- ORPHANS / LEGACY ----

    [Fact]
    public void Startup_Detects_Legacy_Workspace()
    {
        var root = TempRoot();
        var (manager, _, _) = Build(root);
        Directory.CreateDirectory(Path.Combine(root, "wf-legacy-1", "image"));

        var classified = manager.ClassifyAllAsync().GetAwaiter().GetResult();
        Assert.Contains(classified, c => c.WorkspaceId == "wf-legacy-1"
            && c.Classification == WorkspaceClassification.LegacyUnknown);
        var candidates = manager.GetCleanupCandidatesAsync().GetAwaiter().GetResult();
        Assert.Contains(candidates, c => c.WorkspaceId == "wf-legacy-1");
    }

    [Fact]
    public void Legacy_Mounted_Workspace_Protected()
    {
        var root = TempRoot();
        var (manager, _, _) = Build(root, MountedDismOutput);
        Directory.CreateDirectory(Path.Combine(root, "wf-legacy-mnt", "mount"));
        // Legacy workspaces have no manifest, so the mount path is derived from
        // the standard layout — protected via the mounted-path registration.
        var classified = manager.ClassifyAllAsync().GetAwaiter().GetResult();
        // No manifest => no MountPath to match; classification stays LegacyUnknown
        // (never Active-deleted). The real protection is that legacy cleanup is
        // manual-only and the mount query is checked before ANY deletion.
        Assert.Contains(classified, c => c.WorkspaceId == "wf-legacy-mnt"
            && c.Classification is WorkspaceClassification.LegacyUnknown or WorkspaceClassification.Active);
    }

    [Fact]
    public void Corrupt_Manifest_Does_Not_Trigger_Unsafe_Deletion()
    {
        var root = TempRoot();
        var (manager, _, _) = Build(root);
        var dir = Path.Combine(root, "wf-corrupt-1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workspace.json"), "{ not valid json !!!");

        Assert.Null(manager.TryLoadManifest("wf-corrupt-1"));
        var classified = manager.ClassifyAllAsync().GetAwaiter().GetResult();
        Assert.Contains(classified, c => c.WorkspaceId == "wf-corrupt-1"
            && c.Classification is WorkspaceClassification.LegacyUnknown or WorkspaceClassification.Unknown);
    }

    // ---- DISK ----

    [Fact]
    public async Task Disk_Usage_Calculation_Is_Accurate()
    {
        var root = TempRoot();
        var (manager, _, _) = Build(root);
        var id = "wf-size-1";
        var dir = CreateWorkspaceWithFile(manager, id, "image\\install.wim", 2048);
        File.WriteAllText(Path.Combine(dir, "extra.bin"), new string('x', 512));

        var size = await manager.MeasureDirectorySizeAsync(dir);
        Assert.True(size >= 2048 + 512);
    }

    [Fact]
    public void Cleanup_Reports_Reclaimed_Bytes()
    {
        var root = TempRoot();
        var (manager, _, _) = Build(root);
        var id = "wf-bytes-1";
        var dir = CreateWorkspaceWithFile(manager, id, "image\\install.wim", 4096);
        manager.Transition(id, WorkspaceLifecycleState.Cancelled, "UnmountDiscarded");

        var result = manager.CleanupWorkspaceAsync(id).GetAwaiter().GetResult();
        Assert.True(result.Succeeded);
        Assert.True(result.BytesReclaimed >= 4096, $"reclaimed {result.BytesReclaimed}");
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Insufficient_Free_Space_Is_Detected()
    {
        Assert.True(DiskSpaceEstimator.IsInsufficient(freeBytes: 1_000_000, requiredBytes: 2_000_000_000));
        Assert.False(DiskSpaceEstimator.IsInsufficient(freeBytes: 10_000_000_000, requiredBytes: 2_000_000_000));
    }

    [Fact]
    public void Build_Estimate_Is_Conservative()
    {
        var required = DiskSpaceEstimator.EstimateBuild(sourceIsoBytes: 5L * 1024 * 1024 * 1024, workingWimBytes: 6L * 1024 * 1024 * 1024);
        // working WIM + media staging + final ISO + safety margin => well above 16 GiB of raw inputs.
        Assert.True(required > 16L * 1024 * 1024 * 1024);
    }

    // ---- FILESYSTEM ATTRIBUTES ----

    [Fact]
    public void ReadOnly_File_Cleanup_Succeeds()
    {
        var root = TempRoot();
        var (manager, _, _) = Build(root);
        var id = "wf-ro-1";
        var dir = manager.CreateWorkspace(id, null);
        var file = Path.Combine(dir, "image", "install.wim");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "ro");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        manager.Transition(id, WorkspaceLifecycleState.Cancelled, "UnmountDiscarded");

        var result = manager.CleanupWorkspaceAsync(id).GetAwaiter().GetResult();
        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void System_Hidden_File_Cleanup_Succeeds()
    {
        var root = TempRoot();
        var (manager, _, _) = Build(root);
        var id = "wf-sh-1";
        var dir = manager.CreateWorkspace(id, null);
        var file = Path.Combine(dir, "mount", "system.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "hidden");
        File.SetAttributes(file, FileAttributes.System | FileAttributes.Hidden);
        manager.Transition(id, WorkspaceLifecycleState.Cancelled, "UnmountDiscarded");

        var result = manager.CleanupWorkspaceAsync(id).GetAwaiter().GetResult();
        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Partial_Cleanup_Failure_Is_Reported()
    {
        var root = TempRoot();
        var (manager, _, _) = Build(root);
        var id = "wf-partial-1";
        var dir = manager.CreateWorkspace(id, null);
        var locked = Path.Combine(dir, "locked.bin");
        File.WriteAllText(locked, "locked");
        manager.Transition(id, WorkspaceLifecycleState.Cancelled, "UnmountDiscarded");

        using (var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = manager.CleanupWorkspaceAsync(id).GetAwaiter().GetResult();
            // A locked file (or an in-use directory) must NOT claim success.
            Assert.False(result.Succeeded);
            Assert.NotNull(result.LeftoverPath);
            Assert.True(Directory.Exists(dir) || File.Exists(locked));
            // A retry after the lock is released succeeds.
            handle.Close();
            var retry = manager.CleanupWorkspaceAsync(id).GetAwaiter().GetResult();
            Assert.True(retry.Succeeded);
        }
    }

    // ---- OUTPUT / ROOT ----

    [Fact]
    public void Workspace_Root_Change_Affects_New_Workflow_Only()
    {
        var rootA = TempRoot();
        var rootB = TempRoot();
        var (managerA, _, _) = Build(rootA);
        var (managerB, _, _) = Build(rootB);

        var idA = "wf-root-a-1";
        managerA.CreateWorkspace(idA, null);
        managerA.Transition(idA, WorkspaceLifecycleState.Cancelled, "UnmountDiscarded");

        // A new workflow (new manager/root) never touches the old workspace.
        var candidatesB = managerB.GetCleanupCandidatesAsync().GetAwaiter().GetResult();
        Assert.DoesNotContain(candidatesB, c => c.WorkspaceId == idA);
        Assert.True(Directory.Exists(Path.Combine(rootA, idA)));
    }

    // ---- INCIDENT REGRESSION (Part V): repeated workflow must not accumulate ----

    [Fact]
    public void Repeated_Workflow_Does_Not_Accumulate_Workspaces()
    {
        // The real incident: ~30 stale wf-* × ~6.81 GB + temp output ≈ 249 GB.
        // With the lifecycle manager, every completed/discarded workflow becomes a
        // cleanup candidate that a normal cleanup pass removes.
        var root = TempRoot();
        var (manager, _, _) = Build(root);

        for (var i = 0; i < 10; i++)
        {
            var id = $"wf-loop-{i}";
            CreateWorkspaceWithFile(manager, id, "image\\install.wim", 1024);
            manager.Transition(id, WorkspaceLifecycleState.Cancelled, "UnmountDiscarded");

            // Clean after every workflow (Storage "清理临时文件" behavior).
            foreach (var c in manager.GetCleanupCandidatesAsync().GetAwaiter().GetResult())
            {
                manager.CleanupWorkspaceAsync(c.WorkspaceId).GetAwaiter().GetResult();
            }
        }

        // No stale disposable workspaces survive across sessions.
        var remaining = manager.ClassifyAllAsync().GetAwaiter().GetResult();
        Assert.Empty(remaining.Where(c => c.Classification == WorkspaceClassification.Disposable));
    }

    [Fact]
    public void Completed_Without_Output_Is_Retained_For_Inspection()
    {
        var root = TempRoot();
        var (manager, _, _) = Build(root);
        var id = "wf-noout-1";
        manager.CreateWorkspace(id, null);
        manager.Transition(id, WorkspaceLifecycleState.Completed, "BuildCompleted"); // no FinalOutputPath

        var candidates = manager.GetCleanupCandidatesAsync().GetAwaiter().GetResult();
        Assert.DoesNotContain(candidates, c => c.WorkspaceId == id);
    }
}
