using System;
using System.Collections.Generic;
using System.Linq;
using WinForge.Core.Models;

namespace WinForge.Core.ComponentIntelligence;

/// <summary>
/// Maps discovered raw identities onto curated KNOWLEDGE. Discovery (what
/// exists) stays separate: the classifier never mutates inventory, it only
/// returns knowledge. Unknown identities return null — they remain visibly
/// unclassified (technical debt, never hidden).
/// </summary>
public sealed class DeepComponentClassifier
{
    private readonly IReadOnlyList<DeepCatalogEntry> _catalog;

    public DeepComponentClassifier(IReadOnlyList<DeepCatalogEntry> catalog)
        => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <summary>
    /// Deterministic: exact alias first, then first catalog entry whose
    /// normalized pattern is contained in the normalized identity.
    /// </summary>
    public DeepComponentKnowledge? Classify(string rawIdentity)
    {
        if (string.IsNullOrWhiteSpace(rawIdentity))
        {
            return null;
        }

        var norm = ComponentNormalizer.Canonical(rawIdentity);

        // 1) exact alias equality.
        foreach (var entry in _catalog)
        {
            foreach (var alias in entry.Aliases)
            {
                if (string.Equals(ComponentNormalizer.Canonical(alias), norm, StringComparison.Ordinal))
                {
                    return ToKnowledge(entry, ClassificationConfidence.KnownPattern);
                }
            }
        }

        // 2) normalized family pattern containment.
        foreach (var entry in _catalog)
        {
            foreach (var pattern in entry.Patterns)
            {
                var key = ComponentNormalizer.NormalizePattern(pattern);
                if (key.Length > 0 && norm.Contains(key, StringComparison.Ordinal))
                {
                    return ToKnowledge(entry, ClassificationConfidence.KnownFamily);
                }
            }
        }

        return null; // Unknown — keep visible as unclassified.
    }

    /// <summary>Alias-only classification (exact identity → knowledge).</summary>
    public DeepComponentKnowledge? ClassifyExact(string rawIdentity)
    {
        if (string.IsNullOrWhiteSpace(rawIdentity))
        {
            return null;
        }

        var norm = ComponentNormalizer.Canonical(rawIdentity);
        foreach (var entry in _catalog)
        {
            foreach (var alias in entry.Aliases)
            {
                if (string.Equals(ComponentNormalizer.Canonical(alias), norm, StringComparison.Ordinal))
                {
                    return ToKnowledge(entry, ClassificationConfidence.KnownPattern);
                }
            }
        }

        return null;
    }

    private static DeepComponentKnowledge ToKnowledge(DeepCatalogEntry entry, ClassificationConfidence confidence)
    {
        // A catalog entry explicitly marked Heuristic stays Heuristic regardless of
        // how it matched (alias or family) — heuristic is about TRUST, not syntax.
        if (entry.Confidence == ClassificationConfidence.Heuristic)
        {
            confidence = ClassificationConfidence.Heuristic;
        }

        var risk = entry.Risk;
        var protection = entry.Protection;

        // SAFETY INVARIANT (ADR-085): a heuristic classification can never look
        // "safe" on its own — if the entry is heuristic and unprotected, the
        // effective risk floors at Moderate and protection floors at Sensitive.
        if (entry.Confidence == ClassificationConfidence.Heuristic)
        {
            if (protection == ComponentProtectionLevel.None)
            {
                protection = ComponentProtectionLevel.Sensitive;
            }

            if (risk == ComponentRiskLevel.Low || risk == ComponentRiskLevel.Unknown)
            {
                risk = ComponentRiskLevel.Moderate;
            }
        }

        return new DeepComponentKnowledge
        {
            CanonicalId = entry.Id,
            DisplayNameKey = entry.DisplayNameKey,
            DescriptionKey = entry.DescriptionKey,
            DisplayNameFallback = entry.DisplayNameFallback,
            DescriptionFallback = entry.DescriptionFallback,
            Function = entry.Function,
            Subcategory = entry.Subcategory,
            Risk = risk,
            Recommendation = entry.Recommendation,
            Protection = protection,
            ProfileTag = entry.ProfileTag,
            Confidence = confidence,
            NotesKey = entry.NotesKey,
            DependencyTags = entry.DependencyTags,
        };
    }
}

/// <summary>
/// Coverage metrics (Stage 14.1/14.2). Unknown stays visible as technical debt —
/// metrics are never massaged to look better, and entries are never double-counted
/// (curated deep matches count once; protected ⊂ known).
/// </summary>
public sealed class ClassificationCoverageMetrics
{
    public int TotalDiscovered { get; init; }

    /// <summary>Matched a curated definition (Stage 11.2 knowledge rows).</summary>
    public int Curated { get; init; }

    /// <summary>Protected objects (deep catalog protection == Protected). Subset of KnownDeep.</summary>
    public int Protected { get; init; }

    /// <summary>Deep-classified via catalog (KnownPattern/KnownFamily).</summary>
    public int KnownDeep { get; init; }

    /// <summary>Deep-classified but heuristic (never a removal rule by itself).</summary>
    public int Heuristic { get; init; }

    /// <summary>Not classified — visible technical debt.</summary>
    public int UnknownUnclassified { get; init; }

    /// <summary>Curated + KnownDeep over total (no double counting).</summary>
    public double CoverageRatio =>
        TotalDiscovered == 0 ? 0 : (double)(Curated + KnownDeep) / TotalDiscovered;

    /// <summary>Per-source breakdown (source = ComponentCategory kind).</summary>
    public IReadOnlyDictionary<ComponentCategory, SourceCoverage> BySource { get; init; } =
        new Dictionary<ComponentCategory, SourceCoverage>();
}

/// <summary>Per-source coverage slice (known/protected/heuristic/unknown, no double count).</summary>
public sealed class SourceCoverage
{
    public ComponentCategory Source { get; init; }
    public int Total { get; init; }
    public int Known { get; init; }
    public int Protected { get; init; }
    public int Heuristic { get; init; }
    public int Unknown { get; init; }
}

/// <summary>
/// Family-frequency analysis of Unknown identities (Stage 14.2 §4). Derives a
/// likely family prefix from the canonical form; clusters and counts families so
/// the debt report ranks real Unknown groups instead of listing hundreds of ids.
/// </summary>
public static class UnknownFamilyAnalyzer
{
    /// <summary>
    /// Best-effort family prefix of a canonical identity: for dotted/package ids
    /// the leading two segments, for dashed servicing ids the leading segments up
    /// to (and including) the second dash, else the whole canonical key.
    /// </summary>
    public static string FamilyOf(string rawIdentity)
    {
        var c = ComponentNormalizer.Canonical(rawIdentity);
        if (string.IsNullOrEmpty(c))
        {
            return string.Empty;
        }

        if (c.Contains('-', StringComparison.Ordinal))
        {
            var parts = c.Split('-', StringSplitOptions.RemoveEmptyEntries);
            // "microsoft-windows-client-foo" -> "microsoft-windows-client"
            return parts.Length >= 3 ? string.Join('-', parts[0], parts[1], parts[2]) : c;
        }

        if (c.Contains('.', StringComparison.Ordinal))
        {
            var parts = c.Split('.', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? string.Join('.', parts[0], parts[1]) : c;
        }

        return c;
    }

    /// <summary>Cluster identities into families with counts (descending).</summary>
    public static IReadOnlyList<FamilyFrequency> Cluster(IEnumerable<string> identities)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in identities)
        {
            var f = FamilyOf(id);
            if (string.IsNullOrEmpty(f))
            {
                continue;
            }

            map[f] = map.TryGetValue(f, out var n) ? n + 1 : 1;
        }

        return map
            .Select(kv => new FamilyFrequency { Family = kv.Key, Count = kv.Value })
            .OrderByDescending(f => f.Count)
            .ThenBy(f => f.Family, StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>One clustered family + its frequency.</summary>
public sealed class FamilyFrequency
{
    public string Family { get; init; } = string.Empty;
    public int Count { get; init; }
}
