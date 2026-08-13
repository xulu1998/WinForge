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
/// Coverage metrics (Stage 14.1 §11). Unknown stays visible as technical debt —
/// metrics are never massaged to look better.
/// </summary>
public sealed class ClassificationCoverageMetrics
{
    public int TotalDiscovered { get; init; }
    public int Curated { get; init; }
    public int Protected { get; init; }
    public int ClassifiedKnown { get; init; }
    public int UnknownUnclassified { get; init; }

    public double CoverageRatio => TotalDiscovered == 0 ? 0 : (double)ClassifiedKnown / TotalDiscovered;

    /// <summary>Per-source breakdown (source = ComponentCategory kind).</summary>
    public IReadOnlyDictionary<ComponentCategory, SourceCoverage> BySource { get; init; } =
        new Dictionary<ComponentCategory, SourceCoverage>();
}

/// <summary>Per-source coverage slice.</summary>
public sealed class SourceCoverage
{
    public ComponentCategory Source { get; init; }
    public int Total { get; init; }
    public int ClassifiedKnown { get; init; }
    public int Unknown { get; init; }
}
