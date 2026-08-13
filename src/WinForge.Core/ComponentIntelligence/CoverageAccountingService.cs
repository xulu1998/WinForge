using System;
using System.Collections.Generic;
using WinForge.Core.Models;

namespace WinForge.Core.ComponentIntelligence;

/// <summary>
/// EXACT, no-double-count coverage accounting for a real discovery result
/// (Phase 14 Stage 14.3 §3/§18). Uses ONLY production data:
/// <list type="bullet">
///   <item><description>the raw per-category discovery (<see cref="ComponentInventory.Categories"/>);</description></item>
///   <item><description>the production matcher's classified entries
///   (<see cref="ComponentMatcher.BuildInventoryEntries"/> → Curated / Protected);</description></item>
///   <item><description>the production deep classifier (<see cref="DeepComponentClassifier"/>).</description></item>
/// </list>
/// Every raw object lands in EXACTLY ONE exclusive bucket:
/// Curated | KnownDeep | Heuristic | Unknown. <see cref="ClassificationCoverageMetrics.Protected"/>
/// is a PROPERTY count (a subset of the known buckets) — it is never an additional
/// bucket, so totals always reconcile. Metrics are never estimated: a raw item that
/// the classifier cannot map stays visibly Unknown (technical debt, never hidden).
/// </summary>
public static class CoverageAccountingService
{
    /// <summary>
    /// Computes exact coverage from a raw discovery + production classification.
    /// <paramref name="raw"/> is the discovery result; <paramref name="classified"/>
    /// is the matcher output built from the same raw inventory
    /// (<see cref="ComponentMatcher.BuildInventoryEntries"/>).
    /// </summary>
    public static ClassificationCoverageMetrics Compute(
        ComponentInventory raw,
        ComponentInventory classified,
        DeepComponentClassifier deep)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(classified);
        ArgumentNullException.ThrowIfNull(deep);

        // raw identity -> matcher classification (Curated/Protected/Unsupported/DiscoveredUnclassified).
        // An identity may appear in several entries only if the catalog collapses it —
        // the most specific (Curated > Protected > Unsupported > DiscoveredUnclassified) wins.
        var matcherClass = new Dictionary<string, ComponentClassification>(StringComparer.Ordinal);
        foreach (var entry in classified.Entries)
        {
            foreach (var rawItem in entry.RawItems)
            {
                if (string.IsNullOrEmpty(rawItem.RawIdentity))
                {
                    continue;
                }

                if (!matcherClass.TryGetValue(rawItem.RawIdentity, out var existing) ||
                    Rank(entry.Classification) > Rank(existing))
                {
                    matcherClass[rawItem.RawIdentity] = entry.Classification;
                }
            }
        }

        var bySource = new Dictionary<ComponentCategory, SourceAccumulator>();
        var buckets = new Dictionary<string, string>(StringComparer.Ordinal);
        var total = 0;
        var curated = 0;
        var known = 0;
        var heuristic = 0;
        var unknown = 0;
        var protectedCount = 0;
        var matcherProtected = 0;

        foreach (var category in raw.Categories)
        {
            if (category.Items is null)
            {
                continue;
            }

            foreach (var item in category.Items)
            {
                if (string.IsNullOrEmpty(item.RawIdentity))
                {
                    continue;
                }

                total++;
                var acc = Accumulator(bySource, category.Category);
                acc.Total++;

                var isMatcherProtected =
                    matcherClass.TryGetValue(item.RawIdentity, out var mc) &&
                    mc == ComponentClassification.Protected;
                if (isMatcherProtected)
                {
                    matcherProtected++;
                }

                var knowledge = deep.Classify(item.RawIdentity);

                if (mc == ComponentClassification.Curated)
                {
                    // Exclusive bucket 1: production curated match (Stage 11.2 rows).
                    curated++;
                    acc.Curated++;
                    buckets[item.RawIdentity] = "Curated";
                    if (isMatcherProtected || IsProtected(knowledge))
                    {
                        protectedCount++;
                        acc.Protected++;
                    }

                    continue;
                }

                if (knowledge is null)
                {
                    // Exclusive bucket 4: not classifiable — visible debt.
                    unknown++;
                    acc.Unknown++;
                    buckets[item.RawIdentity] = "Unknown";
                    continue;
                }

                if (knowledge.Confidence == ClassificationConfidence.Heuristic)
                {
                    // Exclusive bucket 3: heuristic — knowledge exists but is NOT
                    // trustworthy enough to ever auto-remove.
                    heuristic++;
                    acc.Heuristic++;
                    buckets[item.RawIdentity] = "Heuristic";
                }
                else
                {
                    // Exclusive bucket 2: deep-classified via the curated catalog.
                    known++;
                    acc.Known++;
                    buckets[item.RawIdentity] = "KnownDeep";
                }

                if (isMatcherProtected || knowledge.Protection == ComponentProtectionLevel.Protected)
                {
                    protectedCount++;
                    acc.Protected++;
                }
            }
        }

        // Reconcile: exclusive buckets must sum to the total (curated+known+heuristic+unknown).
        var sources = new Dictionary<ComponentCategory, SourceCoverage>();
        foreach (var (source, a) in bySource)
        {
            sources[source] = a.ToSourceCoverage(source);
        }

        return new ClassificationCoverageMetrics
        {
            TotalDiscovered = total,
            Curated = curated,
            Protected = protectedCount,
            KnownDeep = known,
            Heuristic = heuristic,
            UnknownUnclassified = unknown,
            BySource = sources,
            MatcherProtected = matcherProtected,
            Buckets = buckets,
        };
    }

    private static bool IsProtected(DeepComponentKnowledge? knowledge)
        => knowledge is not null && knowledge.Protection == ComponentProtectionLevel.Protected;

    private static int Rank(ComponentClassification classification) => classification switch
    {
        ComponentClassification.Curated => 4,
        ComponentClassification.Protected => 3,
        ComponentClassification.Unsupported => 2,
        _ => 1,
    };

    private static SourceAccumulator Accumulator(
        IDictionary<ComponentCategory, SourceAccumulator> map, ComponentCategory source)
    {
        if (!map.TryGetValue(source, out var acc))
        {
            acc = new SourceAccumulator();
            map[source] = acc;
        }

        return acc;
    }

    private sealed class SourceAccumulator
    {
        public int Total { get; set; }
        public int Curated { get; set; }
        public int Known { get; set; }
        public int Heuristic { get; set; }
        public int Unknown { get; set; }
        public int Protected { get; set; }

        public SourceCoverage ToSourceCoverage(ComponentCategory source) => new()
        {
            Source = source,
            Total = Total,
            Curated = Curated,
            Known = Known,
            Heuristic = Heuristic,
            Unknown = Unknown,
            Protected = Protected,
        };
    }
}
