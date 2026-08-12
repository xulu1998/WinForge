using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.Services;
using WinForge.Core.WorkspaceLifecycle;

namespace WinForge.Infrastructure.WorkspaceLifecycle;

/// <summary>
/// DISM-backed workspace lifecycle manager (Phase 12). Creates/persists the
/// workspace manifest, transitions lifecycle state, queries the ACTUAL DISM
/// mounted-image registration (the authoritative guard — a query failure fails
/// closed and no deletion decision is made), classifies workspaces at startup,
/// computes safe cleanup candidates, and deletes workspaces safely (attribute
/// stripping + partial-failure recording).
///
/// <para>Platform rule: this is the Windows/DISM implementation of the Core
/// contract. Core never calls DISM; the App only talks to this contract.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WorkspaceLifecycleManager : IWorkspaceLifecycleManager
{
    private readonly IWorkspacePathProvider _paths;
    private readonly IProcessRunner _processRunner;
    private readonly IWorkspaceSafeDelete _safeDelete;
    private readonly ILoggerService _logger;
    private readonly IWorkspaceRootSettingsService? _rootSettings;

    public WorkspaceLifecycleManager(
        IWorkspacePathProvider paths,
        IProcessRunner processRunner,
        IWorkspaceSafeDelete safeDelete,
        ILoggerService logger,
        IWorkspaceRootSettingsService? rootSettings = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _safeDelete = safeDelete ?? throw new ArgumentNullException(nameof(safeDelete));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rootSettings = rootSettings;
    }

    /// <summary>
    /// Current workspace root — the user-configurable root (Stage 12.2) when a
    /// settings service is wired, else the path provider default.
    /// </summary>
    public string WorkspaceRoot => _rootSettings?.CurrentRoot ?? _paths.RootDirectory;

    /// <summary>
    /// All roots that must be scanned for cleanup/orphans: the current root plus
    /// every persisted known (previous) root (Part G). Never scans arbitrary drives.
    /// </summary>
    public IReadOnlyList<string> KnownRoots
    {
        get
        {
            var roots = new List<string> { WorkspaceRoot };
            if (_rootSettings is not null)
            {
                foreach (var r in _rootSettings.KnownRoots)
                {
                    if (!roots.Contains(r, StringComparer.OrdinalIgnoreCase))
                    {
                        roots.Add(r);
                    }
                }
            }

            return roots;
        }
    }

    // ---- Manifest lifecycle ----

    public string CreateWorkspace(string workspaceId, string? sourceIsoPath)
    {
        var dir = Path.Combine(WorkspaceRoot, workspaceId);
        Directory.CreateDirectory(Path.Combine(dir, "image"));
        Directory.CreateDirectory(Path.Combine(dir, "mount"));
        var manifest = new WorkspaceManifest
        {
            WorkspaceId = workspaceId,
            CreatedAtUtc = DateTime.UtcNow,
            LastUsedAtUtc = DateTime.UtcNow,
            CurrentState = WorkspaceLifecycleState.Created,
            SourceIsoPath = string.IsNullOrWhiteSpace(sourceIsoPath) ? null : sourceIsoPath,
            WorkingWimPath = Path.Combine(dir, "image", "install.wim"),
            MountPath = Path.Combine(dir, "mount"),
            IsMountedKnown = false,
            CanDeleteSafely = false,
            WinForgeVersion = GetVersion(),
        };
        manifest.Transitions.Add(Log("Created"));
        WorkspaceManifestStore.TrySave(dir, manifest);
        _logger.Info($"Workspace {workspaceId}: Created at {dir}");
        return dir;
    }

    public WorkspaceManifest? TryLoadManifest(string workspaceId)
    {
        var dir = WorkspaceDirectoryOrNull(workspaceId);
        return dir is null ? null : WorkspaceManifestStore.TryLoad(dir);
    }

    public void Transition(string workspaceId, WorkspaceLifecycleState newState, string transitionName,
        long? bytesReclaimed = null)
    {
        var dir = WorkspaceDirectoryOrNull(workspaceId);
        if (dir is null)
        {
            return;
        }

        var manifest = WorkspaceManifestStore.TryLoad(dir) ?? new WorkspaceManifest { WorkspaceId = workspaceId };
        manifest.CurrentState = newState;
        manifest.LastUsedAtUtc = DateTime.UtcNow;
        manifest.CanDeleteSafely = CanDeleteSafelyFor(newState);
        manifest.Transitions.Add(Log(transitionName, bytesReclaimed));
        WorkspaceManifestStore.TrySave(dir, manifest);
        _logger.Info($"Workspace {workspaceId}: {transitionName} -> {newState}" +
                     (bytesReclaimed is null ? string.Empty : $" (reclaimed {bytesReclaimed} bytes)"));
    }

    public void UpdateManifest(string workspaceId, Action<WorkspaceManifest> mutate)
    {
        var dir = WorkspaceDirectoryOrNull(workspaceId);
        if (dir is null)
        {
            return;
        }

        var manifest = WorkspaceManifestStore.TryLoad(dir) ?? new WorkspaceManifest { WorkspaceId = workspaceId };
        mutate(manifest);
        manifest.LastUsedAtUtc = DateTime.UtcNow;
        WorkspaceManifestStore.TrySave(dir, manifest);
    }

    // ---- Mount safety (Part B) ----

    public async Task<MountStateQueryResult> QueryMountedStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var run = await _processRunner.RunAsync(new ProcessRequest
            {
                FileName = "dism.exe",
                Arguments = "/English /Get-MountedImageInfo",
            }, cancellationToken);

            if (run.ExitCode != 0)
            {
                return new MountStateQueryResult { QuerySucceeded = false, Error = $"dism exited {run.ExitCode}" };
            }

            var mounted = new List<string>();
            foreach (var line in run.StandardOutput.Replace("\r\n", "\n").Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Mount Dir :", StringComparison.OrdinalIgnoreCase))
                {
                    mounted.Add(trimmed.Substring("Mount Dir :".Length).Trim().TrimEnd('\\'));
                }
            }

            return new MountStateQueryResult { QuerySucceeded = true, MountedPaths = mounted };
        }
        catch (Exception ex)
        {
            _logger.Warning($"WorkspaceLifecycle: mount-state query failed: {ex.Message}");
            return new MountStateQueryResult { QuerySucceeded = false, Error = ex.Message };
        }
    }

    // ---- Startup classification (Part F) ----

    public async Task<IReadOnlyList<WorkspaceClassificationResult>> ClassifyAllAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<WorkspaceClassificationResult>();
        if (!Directory.Exists(WorkspaceRoot))
        {
            return results;
        }

        // Authoritative mount registration first (fail closed on query error).
        var mountQuery = await QueryMountedStateAsync(cancellationToken);
        if (!mountQuery.QuerySucceeded)
        {
            _logger.Warning("WorkspaceLifecycle: mount query failed — all workspaces classified Unknown (fail closed).");
            foreach (var dir in EnumerateWorkspaceDirectories())
            {
                results.Add(new WorkspaceClassificationResult
                {
                    WorkspaceId = dir.Name,
                    WorkspaceDirectory = dir.FullName,
                    Classification = WorkspaceClassification.Unknown,
                    RetentionReason = WorkspaceRetentionReason.None,
                    Reason = "Mount-state query failed — fail closed.",
                });
            }

            return results;
        }

        foreach (var dir in EnumerateWorkspaceDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(Classify(dir, mountQuery.MountedPaths));
        }

        return results;
    }

    public async Task<IReadOnlyList<WorkspaceClassificationResult>> GetCleanupCandidatesAsync(
        CancellationToken cancellationToken = default)
    {
        var all = await ClassifyAllAsync(cancellationToken);
        // LegacyUnknown workspaces are offered as cleanup candidates too (Part P);
        // the deletion path still refuses any DISM-registered mount (incl. mounts
        // living inside the workspace directory), so mounted legacy is protected.
        return all.Where(c => c.Classification is WorkspaceClassification.Disposable
            or WorkspaceClassification.LegacyUnknown).ToList();
    }

    // ---- Cleanup (Part C/G/O) ----

    public async Task<CleanupResult> CleanupWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        var dir = WorkspaceDirectoryOrNull(workspaceId);
        if (dir is null || !Directory.Exists(dir))
        {
            return new CleanupResult { Succeeded = true, BytesReclaimed = 0 }; // nothing to clean
        }

        // Part B: NEVER delete a workspace whose mount path is DISM-registered.
        var mountQuery = await QueryMountedStateAsync(cancellationToken);
        if (!mountQuery.QuerySucceeded)
        {
            return new CleanupResult { Succeeded = false, Error = "Mount-state query failed — cleanup refused (fail closed).", LeftoverPath = dir };
        }

        var manifest = WorkspaceManifestStore.TryLoad(dir);
        var mountPath = manifest?.MountPath?.TrimEnd('\\');
        var dirPrefix = dir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var hasActiveMount = mountQuery.MountedPaths.Any(m =>
            (mountPath is not null && string.Equals(m, mountPath, StringComparison.OrdinalIgnoreCase)) ||
            m.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase));
        if (hasActiveMount)
        {
            Transition(workspaceId, WorkspaceLifecycleState.Mounted, "CleanupRefused-ActiveMount");
            return new CleanupResult { Succeeded = false, Error = "Workspace is actively mounted — not deleted.", LeftoverPath = dir };
        }

        var sizeBefore = await MeasureDirectorySizeAsync(dir, cancellationToken);
        Transition(workspaceId, WorkspaceLifecycleState.Cleaning, "CleanupStarted");

        var leftover = DeleteDirectorySafely(dir, out var error);
        if (leftover is null)
        {
            _logger.Info($"Workspace {workspaceId}: CleanupCompleted (reclaimed {sizeBefore} bytes)");
            return new CleanupResult { Succeeded = true, BytesReclaimed = sizeBefore };
        }

        _logger.Warning($"Workspace {workspaceId}: CleanupFailed at {leftover}");
        UpdateManifest(workspaceId, m =>
        {
            m.RetentionReason = WorkspaceRetentionReason.CleanupFailure;
            m.CleanupFailurePath = leftover;
            m.CurrentState = WorkspaceLifecycleState.Cleaning;
        });
        return new CleanupResult { Succeeded = false, BytesReclaimed = sizeBefore, LeftoverPath = leftover, Error = error };
    }

    public async Task<CompletedWorkspaceCleanupResult> CleanupCompletedWorkspaceAsync(
        string workspaceId, CancellationToken cancellationToken = default)
    {
        var dir = WorkspaceDirectoryOrNull(workspaceId);
        if (dir is null || !Directory.Exists(dir))
        {
            return new CompletedWorkspaceCleanupResult { Cleaned = true }; // nothing to clean
        }

        // Authoritative mount check (Part B/C) — never clean an active mount.
        var mountQuery = await QueryMountedStateAsync(cancellationToken);
        if (!mountQuery.QuerySucceeded)
        {
            var unknown = await MeasureDirectorySizeAsync(dir, cancellationToken);
            return new CompletedWorkspaceCleanupResult
            {
                BytesRetained = unknown,
                RetentionReason = WorkspaceRetentionReason.None,
                Error = "Mount-state query failed — cleanup refused (fail closed).",
            };
        }

        var manifest = WorkspaceManifestStore.TryLoad(dir);
        var mountPath = manifest?.MountPath?.TrimEnd('\\');
        var dirPrefix = dir.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var hasActiveMount = mountQuery.MountedPaths.Any(m =>
            (mountPath is not null && string.Equals(m, mountPath, StringComparison.OrdinalIgnoreCase)) ||
            m.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase));
        if (hasActiveMount)
        {
            var mounted = await MeasureDirectorySizeAsync(dir, cancellationToken);
            return new CompletedWorkspaceCleanupResult
            {
                BytesRetained = mounted,
                RetentionReason = WorkspaceRetentionReason.ActiveMount,
                Error = "Workspace is actively mounted — retained.",
            };
        }

        // Recoverable states are retained with their size (minimal-retention rule).
        var isRecoverableState = manifest is not null &&
            manifest.CurrentState is WorkspaceLifecycleState.FailedRecoverable
                or WorkspaceLifecycleState.BuildCheckpoint;
        var completedWithoutOutput = manifest is not null &&
            manifest.CurrentState == WorkspaceLifecycleState.Completed &&
            string.IsNullOrWhiteSpace(manifest.FinalOutputPath);
        if (isRecoverableState || completedWithoutOutput)
        {
            var retained = await MeasureDirectorySizeAsync(dir, cancellationToken);
            return new CompletedWorkspaceCleanupResult
            {
                BytesRetained = retained,
                RetentionReason = WorkspaceRetentionReason.RecoverableBuildCheckpoint,
            };
        }

        // Disposable: delete (attributes stripped, partial failures recorded).
        var sizeBefore = await MeasureDirectorySizeAsync(dir, cancellationToken);
        var leftover = DeleteDirectorySafely(dir, out var error);
        if (leftover is null)
        {
            Transition(workspaceId, WorkspaceLifecycleState.Cleaned, "FinishCleanupCompleted", sizeBefore);
            return new CompletedWorkspaceCleanupResult { Cleaned = true, BytesReclaimed = sizeBefore };
        }

        Transition(workspaceId, WorkspaceLifecycleState.Cleaning, "FinishCleanupFailed");
        UpdateManifest(workspaceId, m =>
        {
            m.RetentionReason = WorkspaceRetentionReason.CleanupFailure;
            m.CleanupFailurePath = leftover;
        });
        return new CompletedWorkspaceCleanupResult
        {
            BytesRetained = sizeBefore,
            RetentionReason = WorkspaceRetentionReason.CleanupFailure,
            Error = error,
        };
    }

    public Task<long> MeasureDirectorySizeAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            if (!Directory.Exists(path))
            {
                return 0L;
            }

            long total = 0;
            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dir = pending.Pop();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        try
                        {
                            total += new FileInfo(file).Length;
                        }
                        catch
                        {
                            // best effort — locked files are not fatal for an estimate
                        }
                    }

                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        pending.Push(sub);
                    }
                }
                catch
                {
                    // best effort
                }
            }

            return total;
        }, cancellationToken);

    // ---- Internals ----

    private WorkspaceClassificationResult Classify(DirectoryInfo dir, IReadOnlyCollection<string> mountedPaths)
    {
        var manifest = WorkspaceManifestStore.TryLoad(dir.FullName);
        var manifestMount = manifest?.MountPath?.TrimEnd('\\');
        var isMounted = manifestMount is not null &&
                        mountedPaths.Any(m => string.Equals(m, manifestMount, StringComparison.OrdinalIgnoreCase));

        if (isMounted)
        {
            return Result(dir, WorkspaceClassification.Active, WorkspaceRetentionReason.ActiveMount,
                "DISM reports this workspace's mount as active.", manifest);
        }

        if (manifest is null)
        {
            // Legacy pre-Phase-12 workspace: no manifest. First implementation
            // classifies unmounted legacy dirs as cleanup candidates but the UI
            // presents them as 旧版残留工作区 (never silently bulk-deleted).
            return Result(dir, WorkspaceClassification.LegacyUnknown,
                WorkspaceRetentionReason.None, "Legacy workspace without manifest.", null);
        }

        switch (manifest.CurrentState)
        {
            case WorkspaceLifecycleState.FailedDisposable:
            case WorkspaceLifecycleState.Cancelled:
            case WorkspaceLifecycleState.Cleaned:
                return Result(dir, WorkspaceClassification.Disposable, WorkspaceRetentionReason.None,
                    $"Terminal disposable state {manifest.CurrentState}.", manifest);
            case WorkspaceLifecycleState.Completed:
                // A completed workspace whose final ISO is recorded OUTSIDE the
                // workspace is disposable (Part C: keep only the user output).
                // Without a recorded output it is conservatively retained.
                return !string.IsNullOrWhiteSpace(manifest.FinalOutputPath)
                    ? Result(dir, WorkspaceClassification.Disposable, WorkspaceRetentionReason.None,
                        "Completed with a final ISO recorded outside the workspace.", manifest)
                    : Result(dir, WorkspaceClassification.Recoverable, WorkspaceRetentionReason.RecoverableBuildCheckpoint,
                        "Completed but no final output recorded — inspect.", manifest);
            case WorkspaceLifecycleState.BuildCheckpoint:
            case WorkspaceLifecycleState.FailedRecoverable:
                return Result(dir, WorkspaceClassification.Recoverable,
                    WorkspaceRetentionReason.RecoverableBuildCheckpoint,
                    $"State {manifest.CurrentState} may hold a recoverable checkpoint.", manifest);
            case WorkspaceLifecycleState.Mounted:
            case WorkspaceLifecycleState.Customized:
                // Stored as mounted but DISM does NOT register it: needs remount
                // recovery — never silently deleted (Part B).
                return Result(dir, WorkspaceClassification.Recoverable, WorkspaceRetentionReason.RecoverableBuildCheckpoint,
                    "Manifest expects a mount but DISM does not register it — recovery required.", manifest);
            case WorkspaceLifecycleState.Created:
            case WorkspaceLifecycleState.Preparing:
            case WorkspaceLifecycleState.Prepared:
            case WorkspaceLifecycleState.Committed:
                // Never mounted (or unmounted) and no recovery flag → abandoned
                // disposable staging.
                return Result(dir, WorkspaceClassification.Disposable, WorkspaceRetentionReason.None,
                    $"Abandoned state {manifest.CurrentState} with no active mount.", manifest);
            default:
                return Result(dir, WorkspaceClassification.Unknown, WorkspaceRetentionReason.None,
                    $"Unhandled state {manifest.CurrentState} — inspect before acting.", manifest);
        }
    }

    private static WorkspaceClassificationResult Result(DirectoryInfo dir, WorkspaceClassification classification,
        WorkspaceRetentionReason reason, string? why, WorkspaceManifest? manifest)
        => new()
        {
            WorkspaceId = dir.Name,
            WorkspaceDirectory = dir.FullName,
            Classification = classification,
            RetentionReason = reason,
            ManifestState = manifest?.CurrentState,
            ManifestPath = manifest is null ? null : WorkspaceManifestStore.ManifestPath(dir.FullName),
            Reason = why,
        };

    private IEnumerable<DirectoryInfo> EnumerateWorkspaceDirectories()
    {
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in KnownRoots)
        {
            if (!seenRoots.Add(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            {
                continue;
            }

            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var dir in new DirectoryInfo(root).EnumerateDirectories())
            {
                if (dir.Name.StartsWith("wf-", StringComparison.OrdinalIgnoreCase))
                {
                    yield return dir;
                }
            }
        }
    }

    private string? WorkspaceDirectoryOrNull(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return null;
        }

        foreach (var root in KnownRoots)
        {
            var candidate = Path.Combine(root, workspaceId);
            if (_safeDelete.IsWithinWorkspace(root, candidate) && Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Recursive delete that strips ReadOnly/System/Hidden before removing each
    /// item. Returns the first leftover path (null = fully deleted), mirroring
    /// Phase 10's destination-attribute cleanup rule (Part O).
    /// </summary>
    private string? DeleteDirectorySafely(string root, out string? error)
    {
        error = null;

        // Phase 12 Part O: strip ReadOnly/System/Hidden before deleting each file,
        // then remove directories deepest-first. Never claim success on a leftover.
        var dirs = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            dirs.Add(current);
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    try
                    {
                        var attributes = File.GetAttributes(file);
                        if ((attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                        }

                        File.Delete(file);
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                        return file;
                    }
                }

                foreach (var sub in Directory.EnumerateDirectories(current))
                {
                    pending.Push(sub);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return current;
            }
        }

        for (var i = dirs.Count - 1; i >= 0; i--)
        {
            try
            {
                Directory.Delete(dirs[i], false);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return dirs[i];
            }
        }

        return null;
    }

    private static bool CanDeleteSafelyFor(WorkspaceLifecycleState state)
        => state is WorkspaceLifecycleState.FailedDisposable
            or WorkspaceLifecycleState.Cancelled
            or WorkspaceLifecycleState.Cleaned
            or WorkspaceLifecycleState.Completed;

    private static WorkspaceTransitionLogEntry Log(string transition, long? bytes = null)
        => new() { Transition = transition, AtUtc = DateTime.UtcNow, BytesReclaimed = bytes };

    private static string GetVersion()
    {
        try
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
