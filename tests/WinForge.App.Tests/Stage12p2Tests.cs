using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinForge.App.ViewModels;
using WinForge.App.Workflow;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Core.WorkspaceLifecycle;
using WinForge.Infrastructure.Logging;
using WinForge.Infrastructure.Servicing;
using WinForge.Infrastructure.WorkspaceLifecycle;
using Xunit;

namespace WinForge.App.Tests;

/// <summary>
/// Phase 12 Stage 12.2 — Finish cleanup + workspace-root settings (Parts A–H).
/// </summary>
public sealed class Stage12p2Tests
{
    private static string TempPath(string prefix) => Path.Combine(Path.GetTempPath(), prefix + "_" + Guid.NewGuid().ToString("N"));

    private static WorkspaceRootSettingsService Settings(string? file = null)
        => new(file ?? TempPath("wf12_settings") + ".json");

    private static (WorkspaceLifecycleManager Manager, WorkspaceRootSettingsService Settings, WorkspacePathProvider Paths, FakeProcessRunner Runner) Build(
        string root, WorkspaceRootSettingsService? settings = null, string dismOutput = "No mounted images found.")
    {
        var paths = new WorkspacePathProvider(root);
        var runner = new FakeProcessRunner
        {
            Responder = req => req.Arguments.Contains("/Get-MountedImageInfo", StringComparison.OrdinalIgnoreCase)
                ? new ProcessResult { ExitCode = 0, StandardOutput = dismOutput }
                : new ProcessResult { ExitCode = 0, StandardOutput = string.Empty },
        };
        var manager = new WorkspaceLifecycleManager(paths, runner, new WorkspaceSafeDelete(), new InMemoryLoggerService(), settings);
        return (manager, settings!, paths, runner);
    }

    // ---- ROOT SETTINGS (Part A) ----

    [Fact]
    public void Root_Setting_Persists_Across_Instances()
    {
        var file = TempPath("wf12_persist") + ".json";
        var s1 = Settings(file);
        var newRoot = TempPath("wf12_newroot");
        Assert.True(s1.SetCurrentRoot(newRoot, out _));

        var s2 = Settings(file); // reload
        Assert.Equal(newRoot, s2.CurrentRoot);
        Assert.Contains(newRoot, s2.KnownRoots);
    }

    [Fact]
    public void Restore_Default_Works()
    {
        var file = TempPath("wf12_restore") + ".json";
        var s1 = Settings(file);
        s1.SetCurrentRoot(TempPath("wf12_tmp1"), out _);
        s1.RestoreDefault();

        Assert.Equal(WorkspaceRootValidator.DefaultRoot(), s1.CurrentRoot);
    }

    [Fact]
    public void New_Workflow_Uses_New_Root()
    {
        var root = TempPath("wf12_rootA");
        var settings = Settings();
        var newRoot = TempPath("wf12_rootB");
        Assert.True(settings.SetCurrentRoot(newRoot, out _));

        var (manager, _, _, _) = Build(root, settings);
        var dir = manager.CreateWorkspace("wf-newroot-1", null);

        Assert.StartsWith(newRoot, dir);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void Existing_Workspace_Remains_In_Old_Root()
    {
        var rootA = TempPath("wf12_oldroot");
        var settings = Settings();
        Assert.True(settings.SetCurrentRoot(rootA, out _)); // old root is the initial root
        var (managerA, _, _, _) = Build(rootA, settings);
        var oldId = "wf-old-1";
        managerA.CreateWorkspace(oldId, null);

        var newRoot = TempPath("wf12_newroot");
        Assert.True(settings.SetCurrentRoot(newRoot, out _));
        // New workflow -> new root; old workspace untouched in old root.
        managerA.CreateWorkspace("wf-new-1", null);
        Assert.True(Directory.Exists(Path.Combine(rootA, oldId)));
        Assert.True(Directory.Exists(Path.Combine(newRoot, "wf-new-1")));
    }

    [Fact]
    public void Active_Mounted_Session_Blocks_Root_Change()
    {
        var state = new AppState
        {
            CurrentServicingWorkspace = new ImageServicingWorkspace
            {
                State = ServicingWorkspaceState.Mounted,
                WorkingDirectory = @"C:\wf\wf-x",
            },
        };
        var settings = Settings();
        var storage = new StorageViewModel(
            new WorkspaceLifecycleManager(new WorkspacePathProvider(TempPath("wf12_mountroot")),
                new FakeProcessRunner(), new WorkspaceSafeDelete(), new InMemoryLoggerService()),
            new FakeLocalizationService(), settings, null, state);

        Assert.False(storage.TrySetRoot(TempPath("wf12_rejected")));
        Assert.True(storage.RootErrorText.Length > 0);
    }

    [Fact]
    public void Invalid_Or_Unwritable_Root_Rejected()
    {
        var settings = Settings();
        Assert.False(settings.SetCurrentRoot("C:\\", out var e1)); // drive root
        Assert.NotNull(e1);
        Assert.False(settings.SetCurrentRoot(string.Empty, out var e2));
        Assert.NotNull(e2);
    }

    [Fact]
    public void Multiple_Known_Roots_Are_Scanned()
    {
        var rootA = TempPath("wf12_mrA");
        var settings = Settings();
        var (manager, _, _, _) = Build(rootA, settings);
        var oldId = "wf-mr-old";
        manager.CreateWorkspace(oldId, null);
        manager.Transition(oldId, WorkspaceLifecycleState.Cancelled, "UnmountDiscarded");

        var rootB = TempPath("wf12_mrB");
        settings.SetCurrentRoot(rootB, out _);
        manager.CreateWorkspace("wf-mr-new", null);

        // Cleanup discovery sees BOTH roots (Part G).
        var candidates = manager.GetCleanupCandidatesAsync().GetAwaiter().GetResult();
        Assert.Contains(candidates, c => c.WorkspaceId == oldId);
    }

    // ---- FINISH CLEANUP (Part C/D) ----

    [Fact]
    public void Finish_Cleans_Completed_Workspace_And_Preserves_Iso()
    {
        var root = TempPath("wf12_fin");
        var (manager, _, _, _) = Build(root);
        var id = "wf-finish-1";
        var dir = manager.CreateWorkspace(id, null);
        File.WriteAllText(Path.Combine(dir, "image", "install.wim"), new string('x', 2048));
        var iso = TempPath("wf12_iso") + ".iso";
        File.WriteAllText(iso, "iso");
        try
        {
            manager.UpdateManifest(id, m => m.FinalOutputPath = iso);
            manager.Transition(id, WorkspaceLifecycleState.Completed, "BuildCompleted");

            var result = manager.CleanupCompletedWorkspaceAsync(id).GetAwaiter().GetResult();
            Assert.True(result.Cleaned);
            Assert.True(result.BytesReclaimed >= 2048);
            Assert.False(Directory.Exists(dir));
            Assert.True(File.Exists(iso)); // final ISO survives
        }
        finally
        {
            if (File.Exists(iso))
            {
                File.Delete(iso);
            }
        }
    }

    [Fact]
    public void Mounted_Workspace_Blocks_Finish_Cleanup()
    {
        var root = TempPath("wf12_finmnt");
        var mountedOutput = "Deployment Image Servicing and Management tool\r\n\r\nMounted Images:\r\n\r\nMount Dir : C:\\wf\\mnt\r\n\r\n";
        var (manager, _, paths, _) = Build(root, null, mountedOutput);
        var id = "wf-finmnt-1";
        var dir = manager.CreateWorkspace(id, null);
        Directory.CreateDirectory(paths.GetMountDirectory(id));
        manager.UpdateManifest(id, m => m.MountPath = @"C:\wf\mnt");
        manager.Transition(id, WorkspaceLifecycleState.Mounted, "Mounted");

        var result = manager.CleanupCompletedWorkspaceAsync(id).GetAwaiter().GetResult();
        Assert.False(result.Cleaned);
        Assert.Equal(WorkspaceRetentionReason.ActiveMount, result.RetentionReason);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void Recoverable_Checkpoint_Is_Retained()
    {
        var root = TempPath("wf12_chk");
        var (manager, _, _, _) = Build(root);
        var id = "wf-chk-1";
        var dir = manager.CreateWorkspace(id, null);
        Directory.CreateDirectory(Path.Combine(dir, "build"));
        File.WriteAllText(Path.Combine(dir, "build", "install.wim"), "checkpoint");
        manager.Transition(id, WorkspaceLifecycleState.BuildCheckpoint, "BuildCheckpointCreated");

        var result = manager.CleanupCompletedWorkspaceAsync(id).GetAwaiter().GetResult();
        Assert.False(result.Cleaned);
        Assert.Equal(WorkspaceRetentionReason.RecoverableBuildCheckpoint, result.RetentionReason);
        Assert.True(result.BytesRetained > 0);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void Finish_Cleanup_Reports_Bytes_Reclaimed()
    {
        var root = TempPath("wf12_bytes");
        var (manager, _, _, _) = Build(root);
        var id = "wf-bytes-1";
        var dir = manager.CreateWorkspace(id, null);
        File.WriteAllText(Path.Combine(dir, "image", "install.wim"), new string('y', 4096));
        manager.Transition(id, WorkspaceLifecycleState.Cancelled, "UnmountDiscarded");

        var result = manager.CleanupCompletedWorkspaceAsync(id).GetAwaiter().GetResult();
        Assert.True(result.Cleaned);
        Assert.True(result.BytesReclaimed >= 4096);
    }

    [Fact]
    public void Discard_Disposable_Workspace_Cleans_Automatically()
    {
        // Part E: a discarded (Cancelled) disposable workspace is cleaned by the
        // same Finish/cleanup path the discard handler invokes.
        var root = TempPath("wf12_disc");
        var (manager, _, _, _) = Build(root);
        var id = "wf-disc-1";
        var dir = manager.CreateWorkspace(id, null);
        File.WriteAllText(Path.Combine(dir, "image", "install.wim"), "discard-me");
        manager.Transition(id, WorkspaceLifecycleState.Cancelled, "UnmountDiscarded");

        var result = manager.CleanupCompletedWorkspaceAsync(id).GetAwaiter().GetResult();
        Assert.True(result.Cleaned);
        Assert.False(Directory.Exists(dir));
    }

    // ---- BuildStep reporting (Part D) ----

    [Fact]
    public void Cleanup_Failure_Does_Not_Mark_Build_Failed()
    {
        var (state, vm) = BuildBuildStepViewModel();
        var iso = TempPath("wf12_bis") + ".iso";
        state.CurrentServicingWorkspace!.WorkingDirectory = Path.Combine(Path.GetTempPath(), "wf12_bv", "wf-build-1");

        vm.ReportFinishCleanup(new CompletedWorkspaceCleanupResult
        {
            Cleaned = false,
            BytesRetained = 12345,
            RetentionReason = WorkspaceRetentionReason.CleanupFailure,
            Error = "locked",
        });

        Assert.True(vm.FinishCleanupRetryVisible);
        Assert.True(vm.HasFinishCleanupFailure);
        // The build itself is NOT failed — the status text still leads with the
        // completed message and BuildState is untouched.
        Assert.StartsWith("Build.Status.Completed", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Build.Finish.Partial", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(BuildState.Completed, state.BuildStatus);
        _ = iso;
    }

    [Fact]
    public void Cleanup_Failure_Is_Retryable()
    {
        var (_, vm) = BuildBuildStepViewModel();
        Assert.NotNull(vm.RetryFinishCleanupCommand);
        vm.ReportFinishCleanup(new CompletedWorkspaceCleanupResult
        {
            Cleaned = false,
            BytesRetained = 1,
            RetentionReason = WorkspaceRetentionReason.CleanupFailure,
        });
        Assert.True(vm.FinishCleanupRetryVisible);
    }

    [Fact]
    public void Finish_Reports_Cleaned_Text()
    {
        var (_, vm) = BuildBuildStepViewModel();
        vm.ReportFinishCleanup(new CompletedWorkspaceCleanupResult { Cleaned = true, BytesReclaimed = 2048 });
        Assert.False(vm.FinishCleanupRetryVisible);
        Assert.StartsWith("Build.Status.Completed", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("Build.Finish.Cleaned", vm.StatusMessage, StringComparison.Ordinal);
    }

    // ---- Localization (Part H #16) ----

    [Fact]
    public void Stage12_Strings_Exist_In_Both_Locales()
    {
        var resx = new System.Resources.ResourceManager("WinForge.App.Resources.Strings", typeof(StorageViewModel).Assembly);
        var keys = new[]
        {
            "Build.Finish.Cleaned", "Build.Finish.Partial", "Build.Finish.Retained", "Build.Finish.RetryCleanup",
            "Storage.Root.Title", "Storage.Root.Change", "Storage.Root.Restore", "Storage.Root.Free",
            "Storage.Root.Warning", "Storage.Root.Invalid", "Storage.Root.NotWritable", "Storage.Root.Mounted",
        };
        foreach (var key in keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(resx.GetString(key, System.Globalization.CultureInfo.GetCultureInfo("en"))), key + " en");
            Assert.False(string.IsNullOrWhiteSpace(resx.GetString(key, System.Globalization.CultureInfo.GetCultureInfo("zh-CN"))), key + " zh");
        }
    }

    // ---- helpers ----

    private static (AppState State, BuildStepViewModel Vm) BuildBuildStepViewModel()
    {
        var state = new AppState
        {
            BuildStatus = BuildState.Completed,
            CurrentServicingWorkspace = new ImageServicingWorkspace { WorkingDirectory = @"C:\wf\wf-x" },
        };
        var fs = new RecordingFileSystem();
        var vm = new BuildStepViewModel(
            state, new FakeBuildService(), fs, new FakeFilePicker(),
            new FakeAdkToolLocator(), new InMemoryLoggerService(), new FakeLocalizationService());
        return (state, vm);
    }
}
