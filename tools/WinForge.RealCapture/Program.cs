using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinForge.Core.ComponentIntelligence;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Infrastructure.ComponentIntelligence;
using WinForge.Infrastructure.Customization;
using WinForge.Infrastructure.ImageMetadata;
using WinForge.Infrastructure.Execution;
using WinForge.Infrastructure.IsoInspection;
using WinForge.Infrastructure.Servicing;
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
        bool NoCleanup);

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
                await WriteJsonAsync(Path.Combine(options.OutDir, "profile-plans.json"), BuildProfilePlans(raw, deep));

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
        IIsoMountService IsoMount);

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

        return new ComposedServices(inspection, workspaceFactory, servicing, intelligence, catalog, isoMount);
    }

    private static Options Parse(string[] args)
    {
        string? iso = null;
        var index = 4;
        string? outDir = null;
        string? workDir = null;
        var noCleanup = false;

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

        var defaultOut = Path.Combine(FindRepoRoot(), ".tmp", "phase14-real");
        var resolvedOut = string.IsNullOrWhiteSpace(outDir) ? defaultOut : Path.GetFullPath(outDir);
        var resolvedWork = string.IsNullOrWhiteSpace(workDir)
            ? Path.Combine(resolvedOut, "work")
            : Path.GetFullPath(workDir);

        return new Options(Path.GetFullPath(iso), index, resolvedOut, resolvedWork, noCleanup);
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
        Console.WriteLine("WinForge.RealCapture — Phase 14.3 elevated real inventory capture.");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  WinForge.RealCapture --iso <path> [--index 4] [--out <dir>] [--work <dir>] [--no-cleanup]");
        Console.WriteLine();
        Console.WriteLine("  --iso        Windows 11 ISO to inspect (read-only). REQUIRED.");
        Console.WriteLine("  --index      WIM index to scan (default 4 = Pro for the 25H2 zh-CN x64 ISO).");
        Console.WriteLine("  --out        Report output dir (default <repo>/.tmp/phase14-real).");
        Console.WriteLine("  --work       Temporary working dir for export/mount (default <out>/work).");
        Console.WriteLine("  --no-cleanup Keep the exported/mounted working image for inspection.");
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

    // ---- Phase 15 Stage 15.1 — deterministic per-profile plan comparison on the
    //      real captured inventory (ADR-094 §13). PLAN VALIDATION ONLY: nothing is
    //      applied or built. Exact operation counts per primary profile. ----

    private static ProfilePlansJson BuildProfilePlans(ComponentInventory raw, DeepComponentClassifier deep)
    {
        var subjects = raw.Categories
            .SelectMany(c => c.Items)
            .Select(i => (Item: i, K: deep.Classify(i.RawIdentity)))
            .Where(x => x.K is not null)
            .Select(x => WinForge.Core.Profiles.ProfilePlanSubject.FromKnowledge(
                x.Item.RawIdentity, x.Item.Category, x.K!))
            .ToList();

        var profiles = new WinForge.Infrastructure.Profiles.ProfileCatalog().GetProfiles();
        var service = new WinForge.Core.Profiles.ProfileExecutionService();
        var reports = service.GenerateAllPrimaries(
            subjects,
            new HashSet<WinForge.Core.Profiles.GamingExtra>(),
            new HashSet<string>(),
            new HashSet<string>(),
            profiles);

        return new ProfilePlansJson
        {
            Media = "Win11_25H2_zh-CN_x64",
            Note = "Deterministic per-primary-profile plan summaries over the real captured inventory (plan validation only; nothing applied/built).",
            Profiles = reports.Select(r => new ProfilePlanJson
            {
                ProfileId = r.ProfileId,
                AutoApply = r.AutoApply,
                Recommended = r.Recommended,
                Optional = r.Optional,
                Kept = r.Kept,
                Blocked = r.Blocked,
                ChangeCount = r.ChangeCount,
                ByOperationType = r.ByOperationType
                    .OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
                    .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            }).ToList(),
        };
    }

    private static string BucketOf(ClassificationCoverageMetrics metrics, string rawIdentity)
        => metrics.Buckets.TryGetValue(rawIdentity, out var bucket) ? bucket : "Unknown";

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
