using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.ComponentIntelligence;
using WinForge.Core.Models;
using WinForge.Core.Profiles;
using WinForge.Core.Services;
using WinForge.Core.Validation;
using WinForge.Infrastructure.ComponentIntelligence;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.ImageMetadata;
using WinForge.Infrastructure.Execution;
using WinForge.Infrastructure.IsoInspection;
using WinForge.Infrastructure.Profiles;
using WinForge.Infrastructure.Servicing;
using WinForge.Infrastructure.Validation;
using WinForge.Infrastructure.WimEngine;
using WinForge.Infrastructure.WorkspaceLifecycle;

namespace WinForge.RealCapture;

/// <summary>
/// Phase 14 Stage 14.3 — ELEVATED REAL INVENTORY CAPTURE (Part A).
///
/// A deterministic, project-local diagnostic CLI that runs the EXACT production
/// WinForge pipeline against a real Windows ISO:
///
///   inspect ISO → build workspace → export selected index → mount working WIM
///   → production discovery (DISM /Get-* via WindowsComponentIntelligenceService)
///   → production matcher → DeepComponentClassifier → exact coverage accounting
///   → UnknownFamilyAnalyzer (top 30) → JSON exports → cleanup (unmount + discard).
///
/// No parallel "test-only" discovery is implemented anywhere: every service below
/// is the same production implementation WinForge uses. The source ISO is NEVER
/// modified (export+discard only). Output goes ONLY to the --out directory.
///
/// DISM offline-image operations require elevation. Run from an elevated
/// (Administrator) prompt; Error 740 otherwise.
///
/// Usage:
///   WinForge.RealCapture --iso "C:\...\Win11_25H2_Chinese_Simplified_x64_v2.iso"
///                        [--index 4] [--out F:\Projects\WinForge\.tmp\phase14-real]
///                        [--work <dir>] [--no-cleanup]
/// </summary>
public static class Program
{
    private const string WorkspaceId = "phase14-real-capture";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record Options(
        string IsoPath,
        int Index,
        string OutDir,
        string WorkDir,
        bool NoCleanup,
        string? ApplyProfile,
        string? CommitProfile,
        string IsoOut,
        string IsoName,
        string? ValidationRunProfile,
        bool Commit,
        string? BundleDir);

    public static async Task<int> Main(string[] args)
    {
        Options? options = null;
        try
        {
            options = Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 2;
        }

        var logger = new ConsoleLoggerService();
        var ct = CancellationToken.None;

        if (options.ApplyProfile is not null)
        {
            Console.WriteLine("=== WinForge Phase 15 Stage 15.4 — Real Offline Profile Apply Validation ===");
            Console.WriteLine($"ISO    : {options.IsoPath}");
            Console.WriteLine($"Index  : {options.Index}");
            Console.WriteLine($"Profile: {options.ApplyProfile}");
            Console.WriteLine($"Out    : {options.OutDir}");
            Console.WriteLine();

            return await RunApplyValidationAsync(options, logger);
        }

        if (options.ValidationRunProfile is not null)
        {
            Console.WriteLine("=== WinForge Phase 17 — Profile Validation Run (release-candidate prep) ===");
            Console.WriteLine($"ISO    : {options.IsoPath}");
            Console.WriteLine($"Index  : {options.Index}");
            Console.WriteLine($"Profile: {options.ValidationRunProfile}");
            Console.WriteLine($"Commit : {(options.Commit ? "yes (commit + ISO build chained)" : "no (prepare-only)")}");
            Console.WriteLine($"Reports: {options.OutDir}");
            Console.WriteLine();

            return await RunValidationRunAsync(options, logger);
        }

        if (options.CommitProfile is not null)
        {
            Console.WriteLine("=== WinForge Phase 16 Stage 16.1 — Real Offline Profile COMMIT + ISO Build ===");
            Console.WriteLine($"ISO    : {options.IsoPath}");
            Console.WriteLine($"Index  : {options.Index}");
            Console.WriteLine($"Profile: {options.CommitProfile}");
            Console.WriteLine($"ISO out: {options.IsoOut}");
            Console.WriteLine($"ISO name: {options.IsoName}");
            Console.WriteLine($"Reports: {options.OutDir}");
            Console.WriteLine();

            return await RunCommitProfileAsync(options, logger);
        }

        Console.WriteLine("=== WinForge Phase 14.3 — Elevated Real Inventory Capture ===");
        Console.WriteLine($"ISO   : {options.IsoPath}");
        Console.WriteLine($"Index : {options.Index}");
        Console.WriteLine($"Out   : {options.OutDir}");
        Console.WriteLine();

        try
        {
            var services = Compose(options, logger);

            // ---- 1. Phase 2 inspection (production, read-only) ----
            var inspection = await services.Inspection.InspectAsync(options.IsoPath, ct);
            if (inspection.Status != IsoInspectionStatus.Completed ||
                inspection.ImageMetadata is null ||
                inspection.ImageMetadata.Status != WindowsImageMetadataStatus.Completed)
            {
                Console.Error.WriteLine(
                    "ISO inspection did not complete. If you see DISM error 740, this tool must run");
                Console.Error.WriteLine("from an ELEVATED (Administrator) prompt.");
                Console.Error.WriteLine(inspection.ErrorMessage is null ? string.Empty : $"Detail: {inspection.ErrorMessage}");
                return 3;
            }

            var edition = inspection.ImageMetadata.Editions.FirstOrDefault(e => e.Index == options.Index);
            if (edition is null)
            {
                Console.Error.WriteLine($"Index {options.Index} not present in the ISO. Available:");
                foreach (var e in inspection.ImageMetadata.Editions.OrderBy(e => e.Index))
                {
                    Console.Error.WriteLine($"  {e.Index}: {e.Name}");
                }

                return 3;
            }

            Console.WriteLine($"Target: {edition.Name} (index {edition.Index}) {edition.Architecture} {edition.Version}");

            // ---- 2. Workspace (production, read-only source) ----
            var workspaceBuild = services.WorkspaceFactory.BuildWorkspace(inspection, edition);
            if (workspaceBuild.Status != ImageWorkspaceStatus.Ready || workspaceBuild.Workspace is null)
            {
                Console.Error.WriteLine($"Workspace build failed: {string.Join("; ", workspaceBuild.Issues)}");
                return 3;
            }

            // ---- 3. Export selected index → working WIM (source ISO untouched) ----
            var prepared = await services.Servicing.PrepareWorkingImageAsync(workspaceBuild.Workspace, WorkspaceId, ct);
            if (!prepared.Success || prepared.Workspace is null)
            {
                PrintServicingFailure("export", prepared.ErrorMessage, prepared.Issues);
                return 4;
            }

            var workspace = prepared.Workspace;
            try
            {
                // ---- 4. Mount working WIM ----
                var mounted = await services.Servicing.MountAsync(workspace, ct);
                if (!mounted.Success)
                {
                    PrintServicingFailure("mount", mounted.ErrorMessage, mounted.Issues);
                    return 4;
                }

                Console.WriteLine($"Mounted working image at {workspace.MountDirectory}");

                // ---- 5. Production discovery ----
                var raw = await services.Intelligence.DiscoverAsync(workspace, ct);
                if (!raw.Discovered)
                {
                    Console.Error.WriteLine("Discovery did not run (workspace not usable).");
                    return 5;
                }

                if (raw.Cancelled)
                {
                    Console.Error.WriteLine("Discovery was cancelled.");
                    return 5;
                }

                // ---- 6. Production classification ----
                var catalog = services.Catalog.GetDefinitions();
                var classified = ComponentMatcher.BuildInventoryEntries(raw, catalog);
                var deep = new DeepComponentClassifier(DeepComponentCatalogData.Entries);
                var metrics = CoverageAccountingService.Compute(raw, classified, deep);

                // ---- 7. Unknown family report (real data) ----
                var unknownItems = raw.Categories
                    .SelectMany(c => c.Items)
                    .Where(i => BucketOf(metrics, i.RawIdentity) == "Unknown")
                    .ToList();
                var families = BuildUnknownFamilies(unknownItems);

                // ---- 8. Gaming candidates ----
                var gamingCandidates = raw.Categories
                    .SelectMany(c => c.Items)
                    .Select(i => (Item: i, Knowledge: deep.Classify(i.RawIdentity)))
                    .Where(x => x.Knowledge is not null &&
                        (x.Knowledge.ProfileTag == ComponentProfileTag.GamingRelevant ||
                         x.Knowledge.Function == ComponentFunctionCategory.Gaming))
                    .Select(x =>
                    {
                        var k = x.Knowledge!;
                        return new GamingCandidateJson
                        {
                            Id = x.Item.RawIdentity,
                            Source = x.Item.Category.ToString(),
                            CanonicalId = k.CanonicalId,
                            DisplayName = string.IsNullOrWhiteSpace(k.DisplayNameFallback)
                                ? k.CanonicalId
                                : k.DisplayNameFallback,
                            ProfileTag = k.ProfileTag.ToString(),
                            Function = k.Function.ToString(),
                            Risk = k.Risk.ToString(),
                            Recommendation = k.Recommendation.ToString(),
                        };
                    })
                    .OrderBy(x => x.Source).ThenBy(x => x.Id, StringComparer.Ordinal)
                    .ToList();

                // ---- 9. JSON exports ----
                Directory.CreateDirectory(options.OutDir);
                await WriteJsonAsync(Path.Combine(options.OutDir, "inventory-summary.json"), BuildSummary(options, edition, raw, metrics));
                await WriteJsonAsync(Path.Combine(options.OutDir, "coverage-by-source.json"), BuildCoverageBySource(metrics));
                await WriteJsonAsync(Path.Combine(options.OutDir, "inventory-items.json"), BuildInventoryItems(raw, metrics, deep));
                await WriteJsonAsync(Path.Combine(options.OutDir, "unknown-items.json"), new UnknownItemsJson
                {
                    Count = unknownItems.Count,
                    Items = unknownItems.Select(i => new UnknownItemJson
                    {
                        Id = i.RawIdentity,
                        Source = i.Category.ToString(),
                        Normalized = ComponentNormalizer.Canonical(i.RawIdentity),
                    }).OrderBy(i => i.Source).ThenBy(i => i.Id, StringComparer.Ordinal).ToList(),
                });
                await WriteJsonAsync(Path.Combine(options.OutDir, "unknown-families.json"), families);
                await WriteJsonAsync(Path.Combine(options.OutDir, "gaming-candidates.json"), new GamingCandidatesJson
                {
                    Count = gamingCandidates.Count,
                    Items = gamingCandidates,
                });
                await WriteJsonAsync(Path.Combine(options.OutDir, "real-derived-families.json"), BuildDerivedFixture(raw, metrics, deep));
                await WriteJsonAsync(Path.Combine(options.OutDir, "profile-plans.json"), BuildProfilePlans(raw, deep, catalog));
                await WriteJsonAsync(Path.Combine(options.OutDir, "profile-buildplans.json"), BuildProfileBuildPlans(raw, deep, catalog));

                PrintResults(metrics, unknownItems, families, gamingCandidates);
            }
            finally
            {
                // ---- 10. Cleanup (production): unmount+discard, dismount ISO, remove workspace ----
                await CleanupAsync(services, workspace, options, logger);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("CAPTURE FAILED: " + ex.Message);
            if (ex.Message.Contains("740", StringComparison.Ordinal) ||
                ex.Message.Contains("elevat", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("DISM requires elevation (Error 740). Run this tool from an");
                Console.Error.WriteLine("ELEVATED (Administrator) prompt:  right-click the terminal ->");
                Console.Error.WriteLine("\"Run as administrator\".");
            }

            return 1;
        }
    }

    // =====================================================================

    private sealed record ComposedServices(
        IIsoInspectionService Inspection,
        IImageWorkspaceFactory WorkspaceFactory,
        IImageServicingService Servicing,
        IComponentIntelligenceService Intelligence,
        IComponentCatalogProvider Catalog,
        IIsoMountService IsoMount,
        IBuildService Build);

    private static ComposedServices Compose(Options options, ILoggerService logger)
    {
        var processRunner = new WindowsProcessRunner();
        var isoMount = new WindowsIsoMountService(logger: logger);
        var metadata = new WindowsImageMetadataService(processRunner, logger);
        var inspection = new WindowsIsoInspectionService(isoMount, metadata, logger);
        var workspaceFactory = new ImageWorkspaceFactory();
        var paths = new WorkspacePathProvider(rootOverride: options.WorkDir);
        var safeDelete = new WorkspaceSafeDelete();
        var lifecycle = new WorkspaceLifecycleManager(paths, processRunner, safeDelete, logger);
        var servicing = new ImageServicingService(processRunner, isoMount, paths, safeDelete, logger, lifecycle);
        var validator = new MountIdentityValidator();
        var intelligence = new WindowsComponentIntelligenceService(processRunner, logger, validator);
        var catalog = new CompositeComponentCatalog(new CuratedComponentCatalog(), new WindowsFeaturesCatalog());

        // Production build pipeline (Phase 10/12) — commit → export → media prep →
        // oscdimg → independent ISO verification. Reused as-is for commit mode.
        var fileSystem = new WinForge.Infrastructure.Build.WindowsFileSystem();
        var build = new WinForge.Infrastructure.Build.ImageBuildService(
            servicing,
            new WinForge.Infrastructure.Build.DismWimExporter(processRunner, fileSystem, logger),
            new WinForge.Infrastructure.Build.IsoMediaPreparer(isoMount, fileSystem, logger),
            new WinForge.Infrastructure.Build.OscdimgIsoBuilder(
                new WinForge.Infrastructure.Build.AdkToolLocator(), processRunner, fileSystem, logger),
            new WinForge.Infrastructure.Build.BuildVerifier(fileSystem, processRunner, isoMount, logger),
            new WinForge.Infrastructure.Build.AdkToolLocator(),
            fileSystem,
            logger);

        return new ComposedServices(inspection, workspaceFactory, servicing, intelligence, catalog, isoMount, build);
    }

    private static Options Parse(string[] args)
    {
        string? iso = null;
        var index = 4;
        string? outDir = null;
        string? workDir = null;
        var noCleanup = false;
        string? applyProfile = null;
        string? commitProfile = null;
        string? isoOut = null;
        string? isoName = null;
        string? validationRunProfile = null;
        var commit = false;
        string? bundleDir = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--iso":
                    iso = RequireValue(args, ref i, "--iso");
                    break;
                case "--index":
                    if (!int.TryParse(RequireValue(args, ref i, "--index"), out index) || index < 1)
                    {
                        throw new ArgumentException("--index must be a positive integer.");
                    }

                    break;
                case "--out":
                    outDir = RequireValue(args, ref i, "--out");
                    break;
                case "--work":
                    workDir = RequireValue(args, ref i, "--work");
                    break;
                case "--no-cleanup":
                    noCleanup = true;
                    break;
                case "--apply-profile":
                    applyProfile = RequireValue(args, ref i, "--apply-profile");
                    break;
                case "--commit-profile":
                    commitProfile = RequireValue(args, ref i, "--commit-profile");
                    break;
                case "--iso-out":
                    isoOut = RequireValue(args, ref i, "--iso-out");
                    break;
                case "--iso-name":
                    isoName = RequireValue(args, ref i, "--iso-name");
                    break;
                case "--validation-run":
                    validationRunProfile = RequireValue(args, ref i, "--validation-run");
                    break;
                case "--commit":
                    commit = true;
                    break;
                case "--bundle-dir":
                    bundleDir = RequireValue(args, ref i, "--bundle-dir");
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(iso))
        {
            throw new ArgumentException("--iso is required.");
        }

        if (!File.Exists(iso))
        {
            throw new ArgumentException($"ISO not found: {iso}");
        }

        if (applyProfile is not null && commitProfile is not null)
        {
            throw new ArgumentException("--apply-profile (discard-only validation) and --commit-profile " +
                                        "(commit + ISO build) are mutually exclusive — commit intent must be explicit.");
        }

        if (validationRunProfile is not null)
        {
            if (applyProfile is not null || commitProfile is not null)
            {
                throw new ArgumentException("--validation-run is mutually exclusive with --apply-profile and --commit-profile " +
                                            "(it ORCHESTRATES a full run; use --commit inside it for the commit+ISO-build step).");
            }

            if (commit && validationRunProfile is null)
            {
                throw new ArgumentException("--commit requires --validation-run.");
            }
        }

        var knownProfiles = new WinForge.Infrastructure.Profiles.ProfileCatalog().GetProfiles()
            .Where(p => p.Kind == ProfileKind.Primary && p.Id != "Custom")
            .Select(p => p.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var id in new[] { applyProfile, commitProfile, validationRunProfile })
        {
            if (id is not null && !knownProfiles.Contains(id))
            {
                throw new ArgumentException(
                    $"--apply-profile/--commit-profile/--validation-run '{id}' is not a known primary profile. " +
                    $"Available: {string.Join(", ", knownProfiles.OrderBy(x => x, StringComparer.Ordinal))}");
            }
        }

        var defaultOut = Path.Combine(FindRepoRoot(), ".tmp", "phase14-real");
        var resolvedOut = string.IsNullOrWhiteSpace(outDir) ? defaultOut : Path.GetFullPath(outDir);
        var resolvedWork = string.IsNullOrWhiteSpace(workDir)
            ? Path.Combine(resolvedOut, "work")
            : Path.GetFullPath(workDir);

        // Commit-mode ISO output: user-chosen dir (default Documents\WinForge),
        // deterministic name (never the repo, never the source ISO root).
        var defaultIsoOut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WinForge");
        var resolvedIsoOut = string.IsNullOrWhiteSpace(isoOut) ? defaultIsoOut : Path.GetFullPath(isoOut);
        var resolvedIsoName = string.IsNullOrWhiteSpace(isoName)
            ? $"WinForge-{commitProfile ?? "Custom"}-Win11-25H2-Pro-zh-CN-x64"
            : isoName;

        return new Options(Path.GetFullPath(iso), index, resolvedOut, resolvedWork, noCleanup,
            applyProfile, commitProfile, resolvedIsoOut, resolvedIsoName, validationRunProfile, commit,
            string.IsNullOrWhiteSpace(bundleDir) ? null : Path.GetFullPath(bundleDir));
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "WinForge.sln")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return Directory.GetCurrentDirectory();
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {flag}.");
        }

        return args[++i];
    }

    private static void PrintUsage()
    {
        Console.WriteLine();
        Console.WriteLine("WinForge.RealCapture — Phase 14.3 elevated real inventory capture / Phase 15.4 apply validation / Phase 16.1 commit+ISO build.");
        Console.WriteLine();
        Console.WriteLine("Usage (capture):");
        Console.WriteLine("  WinForge.RealCapture --iso <path> [--index 4] [--out <dir>] [--work <dir>] [--no-cleanup]");
        Console.WriteLine();
        Console.WriteLine("Usage (real offline apply validation — Stage 15.4, DISCARD ONLY):");
        Console.WriteLine("  WinForge.RealCapture --iso <path> --apply-profile <ProfileId> [--index 4] [--out <dir>]");
        Console.WriteLine();
        Console.WriteLine("Usage (real offline COMMIT + ISO build — Stage 16.1):");
        Console.WriteLine("  WinForge.RealCapture --iso <path> --commit-profile <ProfileId> [--index 4] [--out <dir>] [--iso-out <dir>] [--iso-name <name>]");
        Console.WriteLine();
        Console.WriteLine("Usage (profile validation run — Stage 17.6, release-candidate prep):");
        Console.WriteLine("  WinForge.RealCapture --iso <path> --validation-run <ProfileId> [--commit] [--bundle-dir <dir>]");
        Console.WriteLine("    Builds the plan, derives the profile expected-state from the SELECTED");
        Console.WriteLine("    operations, archives the run under <repo>/.tmp/validation/<runId>/ with a");
        Console.WriteLine("    latest pointer (never overwrites history), and generates the portable");
        Console.WriteLine("    FullHealth bundle (health script + expected-state + manifest + README with");
        Console.WriteLine("    the exact -ProfileId/-MediaId/-ExpectedJson/-IsoSha256 command). With");
        Console.WriteLine("    --commit, the commit+ISO-build pipeline is chained and its evidence is");
        Console.WriteLine("    archived into the same run.");
        Console.WriteLine();
        Console.WriteLine("  --iso             Windows 11 ISO to inspect (READ-ONLY input). REQUIRED.");
        Console.WriteLine("  --index           WIM index to use (default 4 = Pro for the 25H2 zh-CN x64 ISO).");
        Console.WriteLine("  --out             Report output dir (default <repo>/.tmp/phase14-real).");
        Console.WriteLine("  --work            Temporary working dir for export/mount (default <out>/work).");
        Console.WriteLine("  --no-cleanup      Keep the exported/mounted working image for inspection.");
        Console.WriteLine("  --apply-profile   Execute + read-back-verify ONE primary profile against an");
        Console.WriteLine("                    isolated exported+mounted working image, then DISCARD the");
        Console.WriteLine("                    mount and clean the workspace. Only SelectedOperations run.");
        Console.WriteLine("                    (Balanced and DedicatedGaming are the Stage 15.4 profiles;");
        Console.WriteLine("                    run one profile per invocation.)");
        Console.WriteLine("  --commit-profile  EXPLICIT commit/build mode: execute + read-back verify,");
        Console.WriteLine("                    then (only if the pre-commit gate passes) COMMIT the working");
        Console.WriteLine("                    WIM and build a final bootable ISO through the PRODUCTION");
        Console.WriteLine("                    pipeline (oscdimg). Never modifies the source ISO. Output");
        Console.WriteLine("                    ISO goes to --iso-out (default Documents\\WinForge).");
        Console.WriteLine("  --iso-out         Output directory for the final ISO (default Documents\\WinForge).");
        Console.WriteLine("  --iso-name        Output ISO file name without extension (deterministic default");
        Console.WriteLine("                    WinForge-<Profile>-Win11-25H2-Pro-zh-CN-x64).");
        Console.WriteLine("  --validation-run  Phase 17 profile validation run (see above).");
        Console.WriteLine("  --commit          Inside --validation-run: chain the commit + ISO build step.");
        Console.WriteLine("  --bundle-dir      Output dir for the portable FullHealth bundle (default <run>/bundle).");
        Console.WriteLine();
        Console.WriteLine("MUST run from an elevated (Administrator) prompt — DISM requires elevation.");
    }

    private static void PrintServicingFailure(string stage, string? error, IReadOnlyList<string> issues)
    {
        Console.Error.WriteLine($"Servicing {stage} failed.");
        if (!string.IsNullOrWhiteSpace(error))
        {
            Console.Error.WriteLine(error);
        }

        foreach (var issue in issues)
        {
            Console.Error.WriteLine("  - " + issue);
        }

        if (error is not null &&
            (error.Contains("740", StringComparison.Ordinal) || error.Contains("elevat", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("DISM requires elevation (Error 740) — run as Administrator.");
        }
    }

    private static async Task CleanupAsync(ComposedServices services, ImageServicingWorkspace workspace, Options options, ILoggerService logger)
    {
        if (options.NoCleanup)
        {
            logger.Warning("--no-cleanup: leaving the working image mounted at " + workspace.MountDirectory);
            return;
        }

        try
        {
            var unmount = await services.Servicing.UnmountDiscardAsync(workspace, CancellationToken.None);
            if (!unmount.Success)
            {
                logger.Warning("Unmount/discard reported a problem (leaving the working image recoverable): " + unmount.ErrorMessage);
            }
            else
            {
                logger.Info("Working image unmounted (changes discarded).");
            }
        }
        catch (Exception ex)
        {
            logger.Warning("Unmount/discard failed: " + ex.Message);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(workspace.SourceIsoPath))
            {
                await services.IsoMount.DismountAsync(workspace.SourceIsoPath, CancellationToken.None);
                logger.Info("Source ISO dismounted.");
            }
        }
        catch (Exception ex)
        {
            logger.Warning("ISO dismount failed: " + ex.Message);
        }

        try
        {
            var lifecycle = new WorkspaceLifecycleManager(
                new WorkspacePathProvider(rootOverride: options.WorkDir),
                new WindowsProcessRunner(),
                new WorkspaceSafeDelete(),
                logger);
            var cleanup = await lifecycle.CleanupWorkspaceAsync(WorkspaceId, CancellationToken.None);
            logger.Info(cleanup.Succeeded
                ? "Workspace cleaned up."
                : "Workspace cleanup reported: " + cleanup.Error);
        }
        catch (Exception ex)
        {
            logger.Warning("Workspace cleanup failed: " + ex.Message);
        }
    }

    private static InventorySummaryJson BuildSummary(
        Options options,
        WindowsEditionInfo edition,
        ComponentInventory raw,
        ClassificationCoverageMetrics metrics)
    {
        var status = new Dictionary<string, string>();
        foreach (var cat in raw.Categories)
        {
            status[cat.Category.ToString()] = cat.Status == InventoryStatus.Success
                ? $"Success ({cat.Items.Count})"
                : cat.Status + (cat.Error is null ? string.Empty : ": " + cat.Error);
        }

        return new InventorySummaryJson
        {
            TargetIso = options.IsoPath,
            IsoFileName = Path.GetFileName(options.IsoPath),
            SelectedIndex = edition.Index,
            EditionName = edition.Name,
            Architecture = edition.Architecture,
            Build = edition.Build,
            CategoryStatus = status,
            Totals = new TotalsJson
            {
                TotalInventory = metrics.TotalDiscovered,
                Curated = metrics.Curated,
                Protected = metrics.Protected,
                KnownDeep = metrics.KnownDeep,
                Heuristic = metrics.Heuristic,
                Unknown = metrics.UnknownUnclassified,
                MatcherProtected = metrics.MatcherProtected,
                CoverageRatio = Math.Round(metrics.CoverageRatio * 100, 2),
                TotalClassifiedRatio = Math.Round(metrics.TotalClassifiedRatio * 100, 2),
            },
            CuratedLogicalComponents = CountCuratedLogical(raw, metrics),
        };
    }

    private static int CountCuratedLogical(ComponentInventory raw, ClassificationCoverageMetrics metrics)
    {
        // Informational only: production matcher's curated ENTRY count is computed
        // by the caller via ComponentMatcher.BuildInventoryEntries; here we report
        // the raw-object Curated bucket from the exact accounting.
        return metrics.Curated;
    }

    private static CoverageBySourceJson BuildCoverageBySource(ClassificationCoverageMetrics metrics)
        => new()
        {
            Totals = new TotalsJson
            {
                TotalInventory = metrics.TotalDiscovered,
                Curated = metrics.Curated,
                Protected = metrics.Protected,
                KnownDeep = metrics.KnownDeep,
                Heuristic = metrics.Heuristic,
                Unknown = metrics.UnknownUnclassified,
                MatcherProtected = metrics.MatcherProtected,
                CoverageRatio = Math.Round(metrics.CoverageRatio * 100, 2),
                TotalClassifiedRatio = Math.Round(metrics.TotalClassifiedRatio * 100, 2),
            },
            Sources = metrics.BySource
                .OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
                .Select(kv => new SourceCoverageJson
                {
                    Source = kv.Key.ToString(),
                    Total = kv.Value.Total,
                    Curated = kv.Value.Curated,
                    Protected = kv.Value.Protected,
                    Known = kv.Value.Known,
                    Heuristic = kv.Value.Heuristic,
                    Unknown = kv.Value.Unknown,
                })
                .ToList(),
        };

    private static InventoryItemJson[] BuildInventoryItems(
        ComponentInventory raw,
        ClassificationCoverageMetrics metrics,
        DeepComponentClassifier deep)
    {
        var items = new List<InventoryItemJson>();
        foreach (var cat in raw.Categories)
        {
            foreach (var item in cat.Items)
            {
                var bucket = BucketOf(metrics, item.RawIdentity);
                var knowledge = deep.Classify(item.RawIdentity);
                items.Add(new InventoryItemJson
                {
                    Id = item.RawIdentity,
                    Source = cat.Category.ToString(),
                    Classification = bucket,
                    CanonicalId = knowledge?.CanonicalId,
                    Function = knowledge?.Function.ToString(),
                    Risk = knowledge?.Risk.ToString(),
                    Protection = knowledge?.Protection.ToString(),
                    Recommendation = knowledge?.Recommendation.ToString(),
                    Confidence = knowledge?.Confidence.ToString(),
                });
            }
        }

        return items.OrderBy(i => i.Source).ThenBy(i => i.Id, StringComparer.Ordinal).ToArray();
    }

    private static UnknownFamiliesJson BuildUnknownFamilies(IReadOnlyList<IRawInventoryItem> unknownItems)
    {
        // Cluster by (family, source) so each cluster has an unambiguous source.
        var clusters = new Dictionary<(string Family, string Source), List<string>>();
        foreach (var item in unknownItems)
        {
            var family = UnknownFamilyAnalyzer.FamilyOf(item.RawIdentity);
            if (string.IsNullOrEmpty(family))
            {
                continue;
            }

            var key = (family, item.Category.ToString());
            if (!clusters.TryGetValue(key, out var list))
            {
                list = new List<string>();
                clusters[key] = list;
            }

            if (!list.Contains(item.RawIdentity))
            {
                list.Add(item.RawIdentity);
            }
        }

        var ranked = clusters
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Key.Family, StringComparer.Ordinal)
            .Take(30)
            .Select((kv, i) => new UnknownFamilyJson
            {
                Rank = i + 1,
                Family = kv.Key.Family,
                Source = kv.Key.Source,
                Count = kv.Value.Count,
                RepresentativeIdentifiers = kv.Value
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .Take(3)
                    .ToList(),
                NormalizedKey = ComponentNormalizer.Canonical(kv.Key.Family),
                Reason = "No curated pattern/alias in the deep catalog matched this family; it stays visible as Unknown technical debt (never auto-removed).",
            })
            .ToList();

        return new UnknownFamiliesJson { Count = ranked.Count, Families = ranked };
    }

    private static RealDerivedFamiliesJson BuildDerivedFixture(
        ComponentInventory raw,
        ClassificationCoverageMetrics metrics,
        DeepComponentClassifier deep)
    {
        // Unique (source, family) representatives with version/arch/language and
        // host-path tokens stripped via the production normalizer. This is the
        // stable regression fixture shape — no per-object dump, no machine paths.
        var seen = new HashSet<(string Source, string Family, string Bucket)>();
        var entries = new List<RealDerivedFamilyEntryJson>();
        foreach (var cat in raw.Categories)
        {
            foreach (var item in cat.Items)
            {
                var canonical = ComponentNormalizer.Canonical(item.RawIdentity);
                if (string.IsNullOrEmpty(canonical))
                {
                    continue;
                }

                var bucket = BucketOf(metrics, item.RawIdentity);
                var family = UnknownFamilyAnalyzer.FamilyOf(canonical);
                var key = (cat.Category.ToString(), family, bucket);
                if (!seen.Add(key))
                {
                    continue;
                }

                var knowledge = deep.Classify(item.RawIdentity);
                entries.Add(new RealDerivedFamilyEntryJson
                {
                    Source = cat.Category.ToString(),
                    Family = family,
                    Representative = canonical,
                    Classification = bucket,
                    CanonicalId = knowledge?.CanonicalId,
                });
            }
        }

        return new RealDerivedFamiliesJson
        {
            Media = "Win11_25H2_zh-CN_x64",
            Note = "Real-derived stable families (version/arch/language/host-path stripped). Generated by WinForge.RealCapture; intended for tests/fixtures/25H2-Pro-zhCN-component-families.json.",
            Entries = entries.OrderBy(e => e.Source).ThenBy(e => e.Family, StringComparer.Ordinal).ToList(),
        };
    }

    // ---- Phase 15 Stage 15.2/15.3 — UNIFIED candidate stream + real plan
    //      accounting + structural BuildPlan validation (ADR-095/096).
    //      PLAN VALIDATION ONLY: nothing is applied or built. ----

    private static (ProfileCandidateBuildResult Built, IReadOnlyList<ProfileDefinition> Profiles,
        IReadOnlySet<string> Present) BuildUnifiedStream(
        ComponentInventory raw, DeepComponentClassifier deep, IReadOnlyList<ComponentDefinition> curatedCatalog)
    {
        var inventoryInputs = raw.Categories
            .SelectMany(c => c.Items)
            .Select(i => new ProfileInventoryInput
            {
                RawIdentity = i.RawIdentity,
                Category = i.Category,
                Deep = deep.Classify(i.RawIdentity),
                Curated = ComponentMatcher.FindMatchingDefinition(i, curatedCatalog),
            })
            .ToList();

        var optimizations = new WinForge.Infrastructure.Customization.OptimizationCatalog().GetEntries();
        var built = ProfileCandidateService.BuildCandidates(inventoryInputs, optimizations);
        var profiles = new WinForge.Infrastructure.Profiles.ProfileCatalog().GetProfiles();
        var present = built.Subjects.Select(s => s.LogicalId).ToHashSet(StringComparer.Ordinal);
        return (built, profiles, present);
    }

    private static ProfilePlansJson BuildProfilePlans(
        ComponentInventory raw, DeepComponentClassifier deep, IReadOnlyList<ComponentDefinition> curatedCatalog)
    {
        var (built, profiles, present) = BuildUnifiedStream(raw, deep, curatedCatalog);
        var service = new WinForge.Core.Profiles.ProfileExecutionService();
        var reports = service.GenerateAllPrimaries(
            built.Subjects,
            new HashSet<WinForge.Core.Profiles.GamingExtra>(),
            new HashSet<string>(),
            present,
            profiles);

        return new ProfilePlansJson
        {
            Media = "Win11_25H2_zh-CN_x64",
            Note = "Unified candidate stream (inventory deep+curated + optimization definitions, canonical dedup) — exact per-profile v2 plan summaries over the real captured inventory (plan validation only; nothing applied/built).",
            Inventory = ToAccountingJson(built.Accounting),
            OptimizationCandidates = built.OptimizationCandidates,
            OptimizationDuplicates = built.OptimizationDuplicates,
            Profiles = reports.Select(r => new ProfilePlanJson
            {
                ProfileId = r.ProfileId,
                InventoryAccounting = ToAccountingJson(built.Accounting),
                DecisionCounts = new ProfileDecisionCountsJson
                {
                    AutoApply = r.AutoApply,
                    Recommended = r.Recommended,
                    Optional = r.Optional,
                    Kept = r.Kept,
                    Blocked = r.Blocked,
                    NotApplicable = r.NotApplicable,
                },
                PlanChanges = new ProfilePlanChangesJson
                {
                    Total = r.ChangeCount,
                    ByOperationType = r.ByOperationType
                        .OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
                        .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                },
                SemanticActionKeys = r.ChangeKeys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
                KeptHighlights = r.Items
                    .Where(i => i.Disposition == ProfileDisposition.Keep)
                    .Take(6)
                    .Select(i => i.DisplayName)
                    .ToList(),
                BlockedHighlights = r.Items
                    .Where(i => i.Disposition == ProfileDisposition.Blocked)
                    .Take(4)
                    .Select(i => i.DisplayName)
                    .ToList(),
            }).ToList(),
        };
    }

    private static ProfileInventoryAccountingJson ToAccountingJson(ProfileInventoryAccounting a) => new()
    {
        TotalInventory = a.TotalInventory,
        EvaluatedForProfile = a.EvaluatedForProfile,
        CuratedOutsideDeepInventory = a.CuratedOutsideDeepInventory,
        ExcludedUnknownKnowledge = a.ExcludedUnknownKnowledge,
        ExcludedUnsupportedSource = a.ExcludedUnsupportedSource,
        ExcludedFilteredDuplicate = a.ExcludedFilteredDuplicate,
        ExcludedNotApplicable = a.ExcludedNotApplicable,
        ExcludedOther = a.ExcludedOther,
        BySource = a.BySource
            .OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
    };

    // ---- Phase 15 Stage 15.3 — structural BuildPlan validation (ADR-096).
    //      PROOF that every primary profile produces a non-null, validated,
    //      conflict-free, supported plan over the real inventory. Nothing is
    //      applied or built. ----

    private static ProfileBuildPlansJson BuildProfileBuildPlans(
        ComponentInventory raw, DeepComponentClassifier deep, IReadOnlyList<ComponentDefinition> curatedCatalog)
    {
        var (built, profiles, present) = BuildUnifiedStream(raw, deep, curatedCatalog);
        var service = new WinForge.Core.Profiles.ProfileExecutionService();
        var profilesJson = new List<ProfileBuildPlanJson>();

        foreach (var profile in profiles.Where(p => p.Kind == ProfileKind.Primary && p.Id != "Custom"))
        {
            var report = service.GenerateDelta(profile, built.Subjects,
                new HashSet<WinForge.Core.Profiles.GamingExtra>(), new HashSet<string>(), present, profiles);
            var (plan, issues) = service.BuildPlan(profile, built.Subjects,
                new HashSet<WinForge.Core.Profiles.GamingExtra>(), new HashSet<string>(), present, profiles);

            // Stage 15.3b (ADR-096 addendum): expose canonical merges so structural
            // validation can prove same-target candidates collapsed with no
            // information loss. Aggregator input mirrors BuildPlan exactly.
            var aggregate = WinForge.Core.Profiles.ProfilePlanAggregator.Aggregate(report.Items);

            profilesJson.Add(new ProfileBuildPlanJson
            {
                ProfileId = profile.Id,
                DeltaCount = report.ChangeCount,
                BuildPlanOperationCount = plan?.Operations.Count ?? 0,
                SelectedOperationCount = plan?.SelectedOperations.Count ?? 0,
                MergedDuplicateCount = aggregate.MergedDuplicateCount,
                MergeGroupCount = aggregate.MergeGroups.Count,
                DroppedKeepWins = aggregate.DroppedKeepWins,
                ValidationPassed = plan is not null,
                ValidationErrors = issues.ToList(),
                OperationsByType = plan is null
                    ? new Dictionary<string, int>()
                    : plan.Operations
                        .GroupBy(o => o.OperationType.ToString(), StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.Count()),
                CanonicalOperationKeys = plan is null
                    ? new List<string>()
                    : plan.Operations.Select(o => o.ConflictKey).OrderBy(k => k, StringComparer.Ordinal).ToList(),
                MergeGroups = aggregate.MergeGroups
                    .Select(g => new ProfileBuildPlanMergeGroupJson
                    {
                        CanonicalKey = g.CanonicalKey,
                        SourceCount = g.SourceCount,
                        SourceIds = g.SourceIds.ToList(),
                        SourceIdentities = g.SourceIdentities.ToList(),
                    })
                    .ToList(),
            });
        }

        return new ProfileBuildPlansJson
        {
            Media = "Win11_25H2_zh-CN_x64",
            Note = "Structural BuildPlan validation per primary profile over the real captured inventory (ADR-096; plan validation only — nothing applied/built). ValidationPassed == non-null validated plan.",
            Profiles = profilesJson,
        };
    }

    private static string BucketOf(ClassificationCoverageMetrics metrics, string rawIdentity)
        => metrics.Buckets.TryGetValue(rawIdentity, out var bucket) ? bucket : "Unknown";

    // =====================================================================
    // Phase 15 Stage 15.4 — REAL OFFLINE APPLY VALIDATION (ADR-097).
    //
    //   --apply-profile <PrimaryId>
    //
    // Proves a profile-generated BuildPlan EXECUTES against a real mounted
    // Windows image — selected (AutoApply) operations only — and that the result
    // is INDEPENDENTLY READ BACK (AppX / optional feature / offline service /
    // offline registry, incl. OfflineDefaultUser). The working image is an
    // isolated export of the selected WIM index; the source ISO is NEVER
    // modified; after validation the mount is DISCARDED and the workspace is
    // cleaned. A failed mount cleanup is a BLOCKER that stops further validation.
    // =====================================================================

    private static async Task<int> RunApplyValidationAsync(Options options, ILoggerService logger)
    {
        var services = Compose(options, logger);
        var ct = CancellationToken.None;
        var profileId = options.ApplyProfile!;
        ProfileApplyValidationReport report = new() { ProfileId = profileId };
        ImageServicingWorkspace? workspace = null;
        var exitCode = 0;

        try
        {
            // ---- 1. Inspect the source ISO (read-only input) ----
            var inspection = await services.Inspection.InspectAsync(options.IsoPath, ct);
            if (inspection.Status != IsoInspectionStatus.Completed ||
                inspection.ImageMetadata is null ||
                inspection.ImageMetadata.Status != WindowsImageMetadataStatus.Completed)
            {
                Console.Error.WriteLine(
                    "ISO inspection did not complete. If you see DISM error 740, this tool must run");
                Console.Error.WriteLine("from an ELEVATED (Administrator) prompt.");
                Console.Error.WriteLine(inspection.ErrorMessage is null ? string.Empty : $"Detail: {inspection.ErrorMessage}");
                return 3;
            }

            var edition = inspection.ImageMetadata.Editions.FirstOrDefault(e => e.Index == options.Index);
            if (edition is null)
            {
                Console.Error.WriteLine($"Index {options.Index} not present in the ISO. Available:");
                foreach (var e in inspection.ImageMetadata.Editions.OrderBy(e => e.Index))
                {
                    Console.Error.WriteLine($"  {e.Index}: {e.Name}");
                }

                return 3;
            }

            Console.WriteLine($"Target: {edition.Name} (index {edition.Index}) {edition.Architecture} {edition.Version}");

            // ---- 2. Isolated workspace for THIS validation run ----
            var workspaceBuild = services.WorkspaceFactory.BuildWorkspace(inspection, edition);
            if (workspaceBuild.Status != ImageWorkspaceStatus.Ready || workspaceBuild.Workspace is null)
            {
                Console.Error.WriteLine($"Workspace build failed: {string.Join("; ", workspaceBuild.Issues)}");
                return 3;
            }

            // ---- 3. Export selected index → workspace-owned working WIM ----
            var prepared = await services.Servicing.PrepareWorkingImageAsync(workspaceBuild.Workspace, WorkspaceId, ct);
            if (!prepared.Success || prepared.Workspace is null)
            {
                PrintServicingFailure("export", prepared.ErrorMessage, prepared.Issues);
                return 4;
            }

            workspace = prepared.Workspace;

            // ---- 4. Mount the workspace-owned working WIM ----
            var mounted = await services.Servicing.MountAsync(workspace, ct);
            if (!mounted.Success)
            {
                PrintServicingFailure("mount", mounted.ErrorMessage, mounted.Issues);
                exitCode = 4;
                return exitCode;
            }

            Console.WriteLine($"Mounted working image at {workspace.MountDirectory}");

            // ---- 5. Production discovery + classification (same pipeline as capture) ----
            var raw = await services.Intelligence.DiscoverAsync(workspace, ct);
            if (!raw.Discovered)
            {
                Console.Error.WriteLine("Discovery did not run (workspace not usable).");
                exitCode = 5;
                return exitCode;
            }

            if (raw.Cancelled)
            {
                Console.Error.WriteLine("Discovery was cancelled.");
                exitCode = 5;
                return exitCode;
            }

            var catalog = services.Catalog.GetDefinitions();
            var deep = new DeepComponentClassifier(DeepComponentCatalogData.Entries);

            // ---- 6. Unified candidate stream → final validated BuildPlan ----
            var (built, profiles, present) = BuildUnifiedStream(raw, deep, catalog);
            var profile = profiles.Single(p => p.Id == profileId);
            var execution = new WinForge.Core.Profiles.ProfileExecutionService();
            var (plan, issues) = execution.BuildPlan(profile, built.Subjects,
                new HashSet<WinForge.Core.Profiles.GamingExtra>(), new HashSet<string>(), present, profiles);

            if (plan is null || issues.Count > 0)
            {
                Console.Error.WriteLine("ApplyValidation: BuildPlan is not valid — nothing was executed.");
                foreach (var issue in issues)
                {
                    Console.Error.WriteLine("  - " + issue);
                }

                report = new ProfileApplyValidationReport
                {
                    ProfileId = profileId,
                    BuildPlanOperationCount = plan?.Operations.Count ?? 0,
                    SelectedOperationCount = plan?.SelectedOperations.Count ?? 0,
                    Failed = plan?.SelectedOperations.Count ?? 0,
                    ValidationPassed = false,
                    Operations = (plan?.SelectedOperations ?? System.Array.Empty<CustomizationOperation>())
                        .Select(op => new ProfileApplyOperationReport
                        {
                            CanonicalKey = op.ConflictKey,
                            OperationType = op.OperationType.ToString(),
                            ExpectedAction = op.ActionKind?.ToString() ?? op.OperationType.ToString(),
                            ExecutionStatus = CustomizationOperationStatus.Pending.ToString(),
                            VerificationStatus = ApplyVerificationStatus.NotApplicable.ToString(),
                            VerificationDetail = "BuildPlan validation failed; no operation was executed.",
                        })
                        .ToList(),
                };
                exitCode = 9;
                return exitCode;
            }

            var validateIssues = plan.Validate();
            if (validateIssues.Count > 0)
            {
                Console.Error.WriteLine("ApplyValidation: plan did not validate — nothing was executed.");
                foreach (var issue in validateIssues)
                {
                    Console.Error.WriteLine("  - " + issue);
                }

                report = new ProfileApplyValidationReport
                {
                    ProfileId = profileId,
                    BuildPlanOperationCount = plan.Operations.Count,
                    SelectedOperationCount = plan.SelectedOperations.Count,
                    Failed = plan.SelectedOperations.Count,
                    ValidationPassed = false,
                };
                exitCode = 9;
                return exitCode;
            }

            // ---- 7. Execute selected-only + independent read-back verification ----
            var processRunner = new WindowsProcessRunner();
            var registry = new OfflineRegistryService(logger);
            var validator = new MountIdentityValidator();
            var applyService = new ProfileApplyValidationService();
            report = await applyService.ValidateAsync(new ProfileApplyValidationRequest
            {
                Profile = profile,
                Plan = plan,
                Workspace = workspace,
                Executor = new WindowsCustomizationExecutionService(processRunner, registry, logger, validator),
                Verifier = new OfflineApplyVerifier(processRunner, registry, logger),
                Validator = validator,
                Logger = logger,
            }, ct);

            PrintApplySummary(report);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("APPLY VALIDATION FAILED: " + ex.Message);
            if (ex.Message.Contains("740", StringComparison.Ordinal) ||
                ex.Message.Contains("elevat", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("DISM requires elevation (Error 740) — run from an ELEVATED prompt.");
            }

            exitCode = 1;
            report = new ProfileApplyValidationReport
            {
                ProfileId = profileId,
                ValidationPassed = false,
                FailureStage = "Unexpected",
                Error = ex.Message,
                MountCleanup = report.MountCleanup,
            };
        }
        finally
        {
            // ---- 8. Cleanup ALWAYS runs: discard mount, dismount ISO, remove workspace ----
            if (workspace is not null)
            {
                report.MountCleanup = await CleanupWithReportAsync(services, workspace, options, logger);
                if (!report.MountCleanup.DiscardSucceeded)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("BLOCKER: mount cleanup failed — stopping further profile validation.");
                    Console.Error.WriteLine(report.MountCleanup.Error ?? "Unmount/discard reported failure.");
                    exitCode = 10;
                }
            }
            else
            {
                report.MountCleanup = new ProfileApplyMountCleanupReport
                {
                    DiscardSucceeded = true,
                    WorkspaceCleanupSucceeded = false,
                    Error = "No workspace was created for this validation run.",
                };
            }
        }

        // ---- 9. Report (always written when a workspace existed) ----
        if (workspace is not null)
        {
            Directory.CreateDirectory(options.OutDir);
            await WriteJsonAsync(Path.Combine(options.OutDir, "profile-apply-validation.json"), report);
        }

        return exitCode;
    }

    // =====================================================================
    // Phase 17 Stage 17.6 — PROFILE VALIDATION RUN (release-candidate prep).
    //
    //   --validation-run <PrimaryId> [--commit] [--bundle-dir <dir>]
    //
    // One explicit command prepares a profile validation run: production plan
    // → expected-state derived from the SELECTED operations only → run archive
    // under <repo>/.tmp/validation/<runId>/ (manifest + expected-state + plan
    // snapshot; latest pointer never overwrites history) → portable FullHealth
    // bundle (health script + expected-state + validation-manifest + README
    // with the exact -ProfileId/-MediaId/-ExpectedJson/-IsoSha256 command).
    // With --commit, the production commit + ISO build is chained and its
    // evidence archived into the same run. No VMware UI, no OOBE automation.
    // =====================================================================
    private static async Task<int> RunValidationRunAsync(Options options, ILoggerService logger)
    {
        var services = Compose(options, logger);
        var ct = CancellationToken.None;
        var profileId = options.ValidationRunProfile!;
        var repoRoot = FindRepoRoot();
        var commitSha = ReadCommitSha(repoRoot);
        var archive = new ValidationArtifactArchiveService(Path.Combine(repoRoot, ".tmp", "validation"));
        var bundleService = new ValidationBundleService(Path.Combine(repoRoot, "scripts"));
        ImageServicingWorkspace? workspace = null;
        var exitCode = 0;

        var run = new ValidationArtifactRun
        {
            RunId = ValidationArtifactArchiveService.NewRunId(profileId, commitSha),
            TimestampUtc = DateTime.UtcNow,
            SourceIsoPath = options.IsoPath,
            Profile = profileId,
            WindowsIndex = options.Index,
            WinForgeCommitSha = commitSha,
            ValidationLevel = "WorkflowValidated",
            ResultStatus = "Prepared",
            Phase = "Plan",
        };

        try
        {
            // ---- 1-4. Inspect / workspace / export / mount (production path) ----
            var inspection = await services.Inspection.InspectAsync(options.IsoPath, ct);
            if (inspection.Status != IsoInspectionStatus.Completed ||
                inspection.ImageMetadata is null ||
                inspection.ImageMetadata.Status != WindowsImageMetadataStatus.Completed)
            {
                Console.Error.WriteLine("ISO inspection did not complete. MUST run from an ELEVATED prompt (DISM).");
                return 3;
            }

            var edition = inspection.ImageMetadata.Editions.FirstOrDefault(e => e.Index == options.Index);
            if (edition is null)
            {
                Console.Error.WriteLine($"Index {options.Index} not present in the ISO.");
                return 3;
            }

            run.Edition = edition.Name;
            run.Language = inspection.ImageMetadata.Languages is { Count: > 0 }
                ? string.Join(",", inspection.ImageMetadata.Languages)
                : "zh-CN";
            run.Architecture = edition.Architecture;
            run.SourceIsoSha256 = null; // host-side computed; never blocks prep
            Console.WriteLine($"Target: {edition.Name} (index {edition.Index}) {edition.Architecture} {edition.Version}");

            var workspaceBuild = services.WorkspaceFactory.BuildWorkspace(inspection, edition);
            if (workspaceBuild.Status != ImageWorkspaceStatus.Ready || workspaceBuild.Workspace is null)
            {
                Console.Error.WriteLine($"Workspace build failed: {string.Join("; ", workspaceBuild.Issues)}");
                return 3;
            }

            var prepared = await services.Servicing.PrepareWorkingImageAsync(workspaceBuild.Workspace, WorkspaceId, ct);
            if (!prepared.Success || prepared.Workspace is null)
            {
                PrintServicingFailure("export", prepared.ErrorMessage, prepared.Issues);
                return 4;
            }

            workspace = prepared.Workspace;

            var mounted = await services.Servicing.MountAsync(workspace, ct);
            if (!mounted.Success)
            {
                PrintServicingFailure("mount", mounted.ErrorMessage, mounted.Issues);
                return 4;
            }

            Console.WriteLine($"Mounted working image at {workspace.MountDirectory}");

            // ---- 5. Production discovery + classification ----
            var raw = await services.Intelligence.DiscoverAsync(workspace, ct);
            if (!raw.Discovered || raw.Cancelled)
            {
                Console.Error.WriteLine("Discovery did not complete.");
                return 5;
            }

            var catalog = services.Catalog.GetDefinitions();
            var deep = new DeepComponentClassifier(DeepComponentCatalogData.Entries);

            // ---- 6. Unified candidate stream → final validated BuildPlan ----
            var (built, profiles, present) = BuildUnifiedStream(raw, deep, catalog);
            var profile = profiles.Single(p => p.Id == profileId);
            var execution = new WinForge.Core.Profiles.ProfileExecutionService();
            var (plan, planIssues) = execution.BuildPlan(profile, built.Subjects,
                new HashSet<WinForge.Core.Profiles.GamingExtra>(), new HashSet<string>(), present, profiles);

            if (plan is null || planIssues.Count > 0 || plan.Validate().Count > 0)
            {
                Console.Error.WriteLine("ValidationRun: BuildPlan is not valid — run aborted (nothing committed).");
                foreach (var issue in planIssues.Concat(plan?.Validate() ?? new List<string>()))
                {
                    Console.Error.WriteLine("  - " + issue);
                }

                run.ResultStatus = "Failed";
                run.Phase = "Plan";
                run.Notes.Add("BuildPlan validation failed; run aborted before any execution/commit.");
                archive.WriteManifest(run);
                archive.UpdateLatest(run);
                await CleanupWithReportAsync(services, workspace, options, logger);
                return 9;
            }

            // ---- 7. Expected-state from the SELECTED operations (never Recommend rows) ----
            var expected = ExpectedStateBuilder.Build(profileId, plan.SelectedOperations);
            var runDir = archive.CreateRunDirectory(run);
            File.WriteAllText(Path.Combine(runDir, $"{profileId.ToLowerInvariant()}-expected-state.json"),
                JsonSerializer.Serialize(expected, PlanCaptureJsonOptions));
            File.WriteAllText(Path.Combine(runDir, "profile-plan.json"), JsonSerializer.Serialize(new
            {
                profileId,
                buildPlanOperationCount = plan.Operations.Count,
                selectedOperationCount = plan.SelectedOperations.Count,
                canonicalOperationKeys = plan.Operations.Select(o => o.ConflictKey).OrderBy(k => k, StringComparer.Ordinal).ToList(),
                selectedKeys = plan.SelectedOperations.Select(o => o.ConflictKey).OrderBy(k => k, StringComparer.Ordinal).ToList(),
            }, PlanCaptureJsonOptions));
            run.Files.Add($"{profileId.ToLowerInvariant()}-expected-state.json");
            run.Files.Add("profile-plan.json");
            run.Phase = "ExpectedState";
            run.Notes.Add($"Expected-state derived from {plan.SelectedOperations.Count} selected operations (Recommend rows excluded).");
            archive.WriteManifest(run);

            // ---- 8. Optional chained commit + ISO build ----
            if (options.Commit)
            {
                var commitOptions = options with
                {
                    CommitProfile = profileId,
                    ApplyProfile = null,
                    ValidationRunProfile = null,
                };
                var commitExit = await RunCommitProfileAsync(commitOptions, logger);
                var commitReportPath = Path.Combine(options.OutDir, "profile-commit-validation.json");
                if (commitExit == 0 && File.Exists(commitReportPath))
                {
                    File.Copy(commitReportPath, Path.Combine(runDir, "profile-commit-validation.json"), overwrite: true);
                    run.Files.Add("profile-commit-validation.json");
                    try
                    {
                        var commitReport = JsonDocument.Parse(File.ReadAllText(commitReportPath)).RootElement;
                        if (commitReport.TryGetProperty("iso", out var iso) && iso.ValueKind == JsonValueKind.Object)
                        {
                            run.GeneratedIsoPath = iso.TryGetProperty("outputPath", out var p) ? p.GetString() : null;
                            run.GeneratedIsoSha256 = iso.TryGetProperty("sha256", out var s) ? s.GetString() : null;
                        }

                        run.ResultStatus = "Succeeded";
                        run.Phase = "IsoBuild";
                        run.Notes.Add("Commit + ISO build chained successfully; evidence archived.");
                    }
                    catch (JsonException)
                    {
                        run.Notes.Add("Commit report written but could not be parsed for ISO metadata.");
                    }
                }
                else
                {
                    run.ResultStatus = "Failed";
                    run.Phase = "Commit";
                    run.Notes.Add($"Chained commit exited {commitExit}; see profile-commit-validation.json.");
                }

                archive.WriteManifest(run);
                archive.UpdateLatest(run);
            }

            // ---- 9. Portable FullHealth bundle ----
            var bundleDir = options.BundleDir ?? Path.Combine(runDir, "bundle");
            try
            {
                bundleService.GenerateBundle(bundleDir, profileId, run);
                Console.WriteLine($"Bundle generated: {bundleDir}");
            }
            catch (FileNotFoundException ex)
            {
                Console.Error.WriteLine("Bundle generation skipped: " + ex.Message);
                run.Notes.Add("Bundle generation skipped (missing script/expected-state template).");
            }

            archive.WriteManifest(run);
            archive.UpdateLatest(run);

            // ---- 10. Discard-only cleanup (never leaves the mount) ----
            var cleanup = await CleanupWithReportAsync(services, workspace, options, logger);
            run.Notes.Add(cleanup.Error ?? $"Cleanup: discard={cleanup.DiscardSucceeded}, workspace={cleanup.WorkspaceCleanupSucceeded}");
            archive.WriteManifest(run);

            PrintValidationRunSummary(run);
            return exitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ValidationRun failed: {ex.Message}");
            run.ResultStatus = "Failed";
            run.Phase = "Prepare";
            run.Notes.Add(ex.Message);
            archive.WriteManifest(run);
            archive.UpdateLatest(run);
            if (workspace is not null)
            {
                await CleanupWithReportAsync(services, workspace, options, logger);
            }

            return 10;
        }
    }

    private static void PrintValidationRunSummary(ValidationArtifactRun run)
    {
        Console.WriteLine();
        Console.WriteLine("=== PROFILE VALIDATION RUN — PREPARED ===");
        Console.WriteLine($"Run id    : {run.RunId}");
        Console.WriteLine($"Profile   : {run.Profile}");
        Console.WriteLine($"Archive   : .tmp/validation/{run.RunId}");
        Console.WriteLine($"Phase     : {run.Phase}  Status: {run.ResultStatus}");
        Console.WriteLine($"ISO       : {run.GeneratedIsoPath ?? "(not built — run --commit-profile or re-run with --commit)"}");
        Console.WriteLine();
        Console.WriteLine("Next (VM validation): copy the bundle folder into the VM and run the health script");
        Console.WriteLine("with the exact arguments in the bundle README.txt (-ProfileId/-MediaId/-ExpectedJson/-IsoSha256).");
    }

    private static string ReadCommitSha(string repoRoot)
    {
        try
        {
            var head = Path.Combine(repoRoot, ".git", "HEAD");
            if (!File.Exists(head))
            {
                return "unknown";
            }

            var text = File.ReadAllText(head).Trim();
            if (text.StartsWith("ref:", StringComparison.Ordinal))
            {
                var refName = text.Substring(5).Trim();
                var refPath = Path.Combine(repoRoot, ".git", refName);
                if (File.Exists(refPath))
                {
                    return File.ReadAllText(refPath).Trim();
                }

                var packed = Path.Combine(repoRoot, ".git", "packed-refs");
                if (File.Exists(packed))
                {
                    var line = File.ReadLines(packed).FirstOrDefault(l =>
                        l.Trim().EndsWith(refName, StringComparison.Ordinal) && l.Trim().StartsWith("#") == false);
                    if (line is not null)
                    {
                        return line.Trim().Split(' ')[0];
                    }
                }

                return "unknown";
            }

            return text;
        }
        catch
        {
            return "unknown";
        }
    }

    private static readonly JsonSerializerOptions PlanCaptureJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    // =====================================================================
    // Phase 16 Stage 16.1 — REAL OFFLINE COMMIT + ISO BUILD (ADR-098).
    //
    //   --commit-profile <PrimaryId>
    //
    // Same isolated workflow as the Stage 15.4 apply validation (inspect ISO
    // read-only → export selected index → mount → BuildPlan → execute ONLY
    // SelectedOperations → read-back), then — ONLY if the pre-commit gate
    // passes (every attempted op Verified) and ONLY after the commit-mode
    // ownership guard (session-owned paths + authoritative DISM mount
    // inventory, unknown mounts abort) — COMMITS the working WIM and builds a
    // final bootable ISO through the PRODUCTION pipeline (ImageBuildService).
    // The committed WIM is then re-opened and re-verified (post-commit
    // persistence), the ISO structure is validated, and the output metadata
    // (path, size, SHA-256) is reported. Commit intent is EXPLICIT here — the
    // discard-only --apply-profile mode can never accidentally commit.
    // =====================================================================

    private static async Task<int> RunCommitProfileAsync(Options options, ILoggerService logger)
    {
        var services = Compose(options, logger);
        var ct = CancellationToken.None;
        var profileId = options.CommitProfile!;
        ProfileCommitValidationReport report = new() { ProfileId = profileId };
        ImageServicingWorkspace? workspace = null;
        var exitCode = 0;

        try
        {
            // ---- 1. Inspect the source ISO (read-only input) ----
            var inspection = await services.Inspection.InspectAsync(options.IsoPath, ct);
            if (inspection.Status != IsoInspectionStatus.Completed ||
                inspection.ImageMetadata is null ||
                inspection.ImageMetadata.Status != WindowsImageMetadataStatus.Completed)
            {
                Console.Error.WriteLine(
                    "ISO inspection did not complete. If you see DISM error 740, this tool must run");
                Console.Error.WriteLine("from an ELEVATED (Administrator) prompt.");
                Console.Error.WriteLine(inspection.ErrorMessage is null ? string.Empty : $"Detail: {inspection.ErrorMessage}");
                return 3;
            }

            var edition = inspection.ImageMetadata.Editions.FirstOrDefault(e => e.Index == options.Index);
            if (edition is null)
            {
                Console.Error.WriteLine($"Index {options.Index} not present in the ISO. Available:");
                foreach (var e in inspection.ImageMetadata.Editions.OrderBy(e => e.Index))
                {
                    Console.Error.WriteLine($"  {e.Index}: {e.Name}");
                }

                return 3;
            }

            Console.WriteLine($"Target: {edition.Name} (index {edition.Index}) {edition.Architecture} {edition.Version}");

            // ---- 2. Isolated workspace for THIS commit run ----
            var workspaceBuild = services.WorkspaceFactory.BuildWorkspace(inspection, edition);
            if (workspaceBuild.Status != ImageWorkspaceStatus.Ready || workspaceBuild.Workspace is null)
            {
                Console.Error.WriteLine($"Workspace build failed: {string.Join("; ", workspaceBuild.Issues)}");
                return 3;
            }

            // ---- 3. Export selected index → workspace-owned working WIM ----
            var prepared = await services.Servicing.PrepareWorkingImageAsync(workspaceBuild.Workspace, WorkspaceId, ct);
            if (!prepared.Success || prepared.Workspace is null)
            {
                PrintServicingFailure("export", prepared.ErrorMessage, prepared.Issues);
                return 4;
            }

            workspace = prepared.Workspace;

            // ---- 4. Mount the workspace-owned working WIM ----
            var mounted = await services.Servicing.MountAsync(workspace, ct);
            if (!mounted.Success)
            {
                PrintServicingFailure("mount", mounted.ErrorMessage, mounted.Issues);
                return 4;
            }

            Console.WriteLine($"Mounted working image at {workspace.MountDirectory}");

            // ---- 5. Production discovery + classification (same pipeline as capture) ----
            var raw = await services.Intelligence.DiscoverAsync(workspace, ct);
            if (!raw.Discovered)
            {
                Console.Error.WriteLine("Discovery did not run (workspace not usable).");
                return 5;
            }

            if (raw.Cancelled)
            {
                Console.Error.WriteLine("Discovery was cancelled.");
                return 5;
            }

            var catalog = services.Catalog.GetDefinitions();
            var deep = new DeepComponentClassifier(DeepComponentCatalogData.Entries);

            // ---- 6. Unified candidate stream → final validated BuildPlan ----
            var (built, profiles, present) = BuildUnifiedStream(raw, deep, catalog);
            var profile = profiles.Single(p => p.Id == profileId);
            var execution = new ProfileExecutionService();
            var (plan, issues) = execution.BuildPlan(profile, built.Subjects,
                new HashSet<GamingExtra>(), new HashSet<string>(), present, profiles);

            if (plan is null || issues.Count > 0 || plan.Validate().Count > 0)
            {
                Console.Error.WriteLine("Commit: BuildPlan is not valid — nothing was executed or committed.");
                foreach (var issue in issues.Concat(plan?.Validate() ?? System.Array.Empty<string>()))
                {
                    Console.Error.WriteLine("  - " + issue);
                }

                report.BuildPlanOperationCount = plan?.Operations.Count ?? 0;
                report.SelectedOperationCount = plan?.SelectedOperations.Count ?? 0;
                report.PreCommitGateFailure = "BuildPlan validation failed; nothing was executed or committed.";
                exitCode = 9;
                return exitCode;
            }

            // ---- 7. Execute selected-only + independent read-back (same as Stage 15.4) ----
            var processRunner = new WindowsProcessRunner();
            var registry = new OfflineRegistryService(logger);
            var validator = new MountIdentityValidator();
            var applyService = new ProfileApplyValidationService();
            var applyReport = await applyService.ValidateAsync(new ProfileApplyValidationRequest
            {
                Profile = profile,
                Plan = plan,
                Workspace = workspace,
                Executor = new WindowsCustomizationExecutionService(processRunner, registry, logger, validator),
                Verifier = new OfflineApplyVerifier(processRunner, registry, logger),
                Validator = validator,
                Logger = logger,
            }, ct);

            report.BuildPlanOperationCount = applyReport.BuildPlanOperationCount;
            report.SelectedOperationCount = applyReport.SelectedOperationCount;
            report.Attempted = applyReport.Attempted;
            report.Succeeded = applyReport.Succeeded;
            report.Failed = applyReport.Failed;
            report.Skipped = applyReport.Skipped;
            report.PreCommitValidationPassed = applyReport.ValidationPassed;
            report.Operations = new List<ProfileApplyOperationReport>(applyReport.Operations);
            PrintApplySummary(applyReport);

            if (!applyReport.ValidationPassed || applyReport.Failed > 0)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("COMMIT BLOCKED: pre-commit read-back gate rejected the run — " +
                                        "the working image will be DISCARDED, nothing committed.");
                exitCode = 9;
                return exitCode;
            }

            // ---- 8. Explicit commit + production ISO build + post-commit re-verification ----
            Directory.CreateDirectory(options.IsoOut);
            var commitService = new ProfileIsoCommitService(
                services.Build,
                new OfflineApplyVerifier(processRunner, registry, logger),
                validator,
                services.Servicing,
                services.IsoMount,
                processRunner,
                logger);
            report = await commitService.CommitAsync(new ProfileIsoCommitRequest
            {
                Profile = profile,
                Plan = plan,
                Workspace = workspace,
                ApplyReport = applyReport,
                SourceIsoPath = options.IsoPath,
                SourceIsoSizeBytes = new FileInfo(options.IsoPath).Length,
                SourceImageRelativePath = workspace.SourceImageRelativePath ?? "sources/install.wim",
                SourceImageType = workspace.SourceImageType,
                SourceEditionName = workspace.SelectedEditionName,
                OutputDirectory = options.IsoOut,
                OutputFileName = options.IsoName,
            }, ct);

            if (report.Committed)
            {
                // BuildAsync committed + unmounted the working image. Mark the
                // CLI-side workspace Prepared so cleanup treats it as a safe no-op
                // (never a second DISM operation against a gone mount).
                workspace.State = ServicingWorkspaceState.Prepared;
            }

            PrintCommitSummary(report);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("COMMIT VALIDATION FAILED: " + ex.Message);
            if (ex.Message.Contains("740", StringComparison.Ordinal) ||
                ex.Message.Contains("elevat", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("DISM requires elevation (Error 740) — run from an ELEVATED prompt.");
            }

            exitCode = 1;
            report.CommitError = ex.Message;
            report.PostCommitError = ex.Message;
        }
        finally
        {
            // ---- 9. Cleanup ALWAYS runs: discard any remaining mount, dismount ISO, remove workspace ----
            if (workspace is not null)
            {
                report.MountCleanup = await CleanupWithReportAsync(services, workspace, options, logger);
                if (!report.MountCleanup.DiscardSucceeded)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("BLOCKER: mount cleanup failed — stopping further profile validation.");
                    Console.Error.WriteLine(report.MountCleanup.Error ?? "Unmount/discard reported failure.");
                    exitCode = 10;
                }
            }
            else
            {
                report.MountCleanup = new ProfileApplyMountCleanupReport
                {
                    DiscardSucceeded = true,
                    WorkspaceCleanupSucceeded = false,
                    Error = "No workspace was created for this commit run.",
                };
            }
        }

        // ---- 10. Report (always written) ----
        if (workspace is not null)
        {
            Directory.CreateDirectory(options.OutDir);
            await WriteJsonAsync(Path.Combine(options.OutDir, "profile-commit-validation.json"), report);
        }

        return exitCode;
    }

    private static void PrintCommitSummary(ProfileCommitValidationReport report)
    {
        Console.WriteLine();
        Console.WriteLine("=== PROFILE COMMIT + ISO BUILD SUMMARY ===");
        Console.WriteLine($"Profile            : {report.ProfileId}");
        Console.WriteLine($"BuildPlan ops      : {report.BuildPlanOperationCount}");
        Console.WriteLine($"Selected ops       : {report.SelectedOperationCount}");
        Console.WriteLine($"Attempted/Succeeded: {report.Attempted}/{report.Succeeded}");
        Console.WriteLine($"Failed/Skipped     : {report.Failed}/{report.Skipped}");
        Console.WriteLine($"Pre-commit gate    : {(report.PreCommitValidationPassed ? "PASS" : "REJECTED")}");
        if (report.PreCommitGateFailure is not null)
        {
            Console.WriteLine($"Gate failure       : {report.PreCommitGateFailure}");
        }

        Console.WriteLine($"Committed          : {report.Committed}");
        if (report.CommitError is not null)
        {
            Console.WriteLine($"Commit error       : {report.CommitError}");
        }

        Console.WriteLine($"Post-commit verify : {(report.PostCommitVerified ? "PASS" : "FAILED")}");
        if (report.PostCommitError is not null)
        {
            Console.WriteLine($"Post-commit error  : {report.PostCommitError}");
        }

        foreach (var check in report.PostCommitChecks)
        {
            Console.WriteLine($"  [{check.VerificationStatus,-16}] {check.CanonicalKey} — {check.VerificationDetail}");
        }

        if (report.Iso is not null)
        {
            Console.WriteLine($"ISO output         : {report.Iso.OutputPath}");
            Console.WriteLine($"ISO size           : {report.Iso.SizeBytes:N0} bytes");
            Console.WriteLine($"ISO SHA-256        : {report.Iso.Sha256}");
            Console.WriteLine($"ISO structure      : {(report.Iso.StructureValidated ? "VALID" : "INVALID")}");
            foreach (var check in report.Iso.StructureChecks)
            {
                Console.WriteLine($"  - {check}");
            }
        }

        Console.WriteLine($"Cleanup            : discard={report.MountCleanup.DiscardSucceeded} workspace={report.MountCleanup.WorkspaceCleanupSucceeded}");
    }

    private static void PrintApplySummary(ProfileApplyValidationReport report)
    {
        Console.WriteLine();
        Console.WriteLine("=== PROFILE APPLY VALIDATION SUMMARY ===");
        Console.WriteLine($"Profile          : {report.ProfileId}");
        Console.WriteLine($"BuildPlan ops    : {report.BuildPlanOperationCount}");
        Console.WriteLine($"Selected ops     : {report.SelectedOperationCount}");
        Console.WriteLine($"Attempted        : {report.Attempted}");
        Console.WriteLine($"Succeeded        : {report.Succeeded}");
        Console.WriteLine($"Failed           : {report.Failed}");
        Console.WriteLine($"Skipped          : {report.Skipped}");
        Console.WriteLine($"ValidationPassed : {report.ValidationPassed}");
        if (report.FailureStage is not null)
        {
            Console.WriteLine($"FAILURE STAGE    : {report.FailureStage}");
            Console.WriteLine($"FAILED OP KEY    : {report.FailedCanonicalKey ?? "(run-level)"}");
            Console.WriteLine($"ERROR            : {report.Error}");
        }

        foreach (var op in report.Operations)
        {
            Console.WriteLine($"  [{op.ExecutionStatus,-16}|{op.VerificationStatus,-16}] {op.CanonicalKey} — {op.VerificationDetail}");
        }
    }

    /// <summary>
    /// Cleanup with an explicit report: discard the workspace-owned mount (via
    /// authoritative DISM mount inventory — an unknown mount is NEVER discarded),
    /// dismount the source ISO, then remove the workspace. Returns
    /// <see cref="ProfileApplyMountCleanupReport"/> for §3 mountCleanup.
    /// </summary>
    private static async Task<ProfileApplyMountCleanupReport> CleanupWithReportAsync(
        ComposedServices services, ImageServicingWorkspace workspace, Options options, ILoggerService logger)
    {
        if (options.NoCleanup)
        {
            logger.Warning("--no-cleanup: leaving the working image mounted at " + workspace.MountDirectory);
            return new ProfileApplyMountCleanupReport
            {
                DiscardSucceeded = false,
                WorkspaceCleanupSucceeded = false,
                Error = "--no-cleanup requested; working image retained.",
            };
        }

        var discardOk = true;
        var workspaceOk = true;
        string? error = null;

        try
        {
            var unmount = await services.Servicing.UnmountDiscardAsync(workspace, CancellationToken.None);
            discardOk = unmount.Success;
            if (!unmount.Success)
            {
                error = unmount.ErrorMessage;
                logger.Warning("Unmount/discard reported a problem: " + unmount.ErrorMessage);
            }
            else
            {
                logger.Info("Working image unmounted (changes discarded).");
            }
        }
        catch (Exception ex)
        {
            discardOk = false;
            error = ex.Message;
            logger.Warning("Unmount/discard failed: " + ex.Message);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(workspace.SourceIsoPath))
            {
                await services.IsoMount.DismountAsync(workspace.SourceIsoPath, CancellationToken.None);
                logger.Info("Source ISO dismounted.");
            }
        }
        catch (Exception ex)
        {
            logger.Warning("ISO dismount failed: " + ex.Message);
        }

        try
        {
            var lifecycle = new WorkspaceLifecycleManager(
                new WorkspacePathProvider(rootOverride: options.WorkDir),
                new WindowsProcessRunner(),
                new WorkspaceSafeDelete(),
                logger);
            var cleanup = await lifecycle.CleanupWorkspaceAsync(WorkspaceId, CancellationToken.None);
            workspaceOk = cleanup.Succeeded;
            if (!cleanup.Succeeded)
            {
                error = cleanup.Error;
                logger.Warning("Workspace cleanup reported: " + cleanup.Error);
            }
            else
            {
                logger.Info("Workspace cleaned up.");
            }
        }
        catch (Exception ex)
        {
            workspaceOk = false;
            error = ex.Message;
            logger.Warning("Workspace cleanup failed: " + ex.Message);
        }

        return new ProfileApplyMountCleanupReport
        {
            DiscardSucceeded = discardOk,
            WorkspaceCleanupSucceeded = workspaceOk,
            Error = error,
        };
    }

    private static void PrintResults(
        ClassificationCoverageMetrics metrics,
        IReadOnlyList<IRawInventoryItem> unknownItems,
        UnknownFamiliesJson families,
        IReadOnlyList<GamingCandidateJson> gamingCandidates)
    {
        Console.WriteLine();
        Console.WriteLine("=== EXACT REAL-MEDIA COVERAGE (no estimation) ===");
        Console.WriteLine($"Total inventory        : {metrics.TotalDiscovered}");
        Console.WriteLine($"Curated (matcher)      : {metrics.Curated}");
        Console.WriteLine($"Protected (property)   : {metrics.Protected}  (matcher-protected: {metrics.MatcherProtected})");
        Console.WriteLine($"Known deep classified  : {metrics.KnownDeep}");
        Console.WriteLine($"Heuristic classified   : {metrics.Heuristic}");
        Console.WriteLine($"Unknown (visible debt) : {metrics.UnknownUnclassified}");
        Console.WriteLine($"Knowledge coverage     : {metrics.CoverageRatio:P2}");
        Console.WriteLine($"Total classified       : {metrics.TotalClassifiedRatio:P2}");
        Console.WriteLine();
        Console.WriteLine("By source (total | curated | protected | known | heuristic | unknown):");
        foreach (var kv in metrics.BySource.OrderBy(k => k.Key.ToString(), StringComparer.Ordinal))
        {
            var s = kv.Value;
            Console.WriteLine(
                $"  {kv.Key,-16} {s.Total,5} | {s.Curated,5} | {s.Protected,5} | {s.Known,5} | {s.Heuristic,5} | {s.Unknown,5}");
        }

        Console.WriteLine();
        Console.WriteLine($"Unknown families (top {families.Count} clusters over {unknownItems.Count} unknown objects):");
        foreach (var f in families.Families)
        {
            Console.WriteLine($"  #{f.Rank,-3} {f.Family,-45} {f.Source,-14} {f.Count,4}  e.g. {string.Join(", ", f.RepresentativeIdentifiers.Take(2))}");
        }

        Console.WriteLine();
        Console.WriteLine($"Gaming-relevant candidates: {gamingCandidates.Count}");
        Console.WriteLine();
        Console.WriteLine("Reports written to the --out directory.");
    }

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions));
        Console.WriteLine("  wrote " + path);
    }
}
