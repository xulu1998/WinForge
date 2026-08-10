using System;
using System.Collections.Generic;
using System.Linq;

namespace WinForge.Core.Models;

/// <summary>
/// Pure (platform-agnostic, no DISM) logic that classifies a raw discovery into
/// logical <see cref="ComponentInventoryEntry"/> rows. It keeps the "DISCOVERED
/// WINDOWS OBJECT" separate from the "COMPONENT DEFINITION" (ADR-045): a raw item
/// only becomes <see cref="ComponentClassification.Curated"/> when a catalog
/// definition's <see cref="TechnicalTarget"/> actually matches it. Unknown is
/// preferred over invented information.
///
/// <para>Responsibilities:</para>
/// <list type="bullet">
///   <item><description>Map raw items onto curated definitions (prefix / contains / exact).</description></item>
///   <item><description>Classify unmatched items as Protected / Unsupported / DiscoveredUnclassified.</description></item>
///   <item><description>Collapse multiple raw identities that belong to one logical component.</description></item>
///   <item><description>Surface curated definitions not present in the image as catalog-only rows.</description></item>
/// </list>
/// </summary>
public static class ComponentMatcher
{
    // Categories WinForge does NOT service in Stage 11.1. A discovered item of
    // these kinds is Unsupported (never offered as removable) — even though the
    // provider interfaces for them are designed, no servicing exists yet.
    private static readonly HashSet<ComponentCategory> UnsupportedCategories = new()
    {
        ComponentCategory.Service,
        ComponentCategory.ScheduledTask,
        ComponentCategory.Driver,
        ComponentCategory.Language,
        ComponentCategory.WinRecovery,
        ComponentCategory.SystemApp
    };

    // Identity substrings that mark a discovered object as Protected regardless of
    // category: servicing stack, core-shell, recovery, setup, language, drivers.
    private static readonly string[] ProtectedMarkers =
    {
        "ServicingStack", "Foundation", "WinPE", "Setup", "LanguagePack",
        "Language", "Driver", "WinRE", "Recovery", "Microsoft-Windows-Edition",
        "Microsoft-Windows-Client", "Microsoft-Windows-Foundation",
        "Microsoft-Windows-ServicingStack", "Windows-Recovery", "Client-Desktop"
    };

    /// <summary>
    /// Builds the classified entry list. When <paramref name="raw"/> is null (no
    /// discovery performed, e.g. no mounted image) only catalog-only curated rows
    /// are returned so the prototype can still present known components.
    /// </summary>
    public static ComponentInventory BuildInventoryEntries(
        ComponentInventory? raw, IReadOnlyList<ComponentDefinition> catalog)
    {
        var discovered = raw?.Discovered ?? false;
        var cancelled = raw?.Cancelled ?? false;
        var categories = raw?.Categories ?? new List<CategoryDiscoveryResult>();
        var matchedDefinitionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var entries = new List<ComponentInventoryEntry>();

        if (raw is not null)
        {
            foreach (var cat in raw.Categories)
            {
                foreach (var item in cat.Items)
                {
                    var def = FindMatchingDefinition(item, catalog);
                    if (def is not null)
                    {
                        matchedDefinitionIds.Add(def.Id);
                    }

                    entries.Add(new ComponentInventoryEntry
                    {
                        Definition = def,
                        RawItems = new List<IRawInventoryItem> { item },
                        Classification = Classify(item, def)
                    });
                }
            }

            entries = CollapseByDefinition(entries);
        }

        // Curated definitions not present in the inventory become catalog-only rows.
        foreach (var def in catalog)
        {
            if (matchedDefinitionIds.Contains(def.Id))
            {
                continue;
            }

            entries.Add(new ComponentInventoryEntry
            {
                Definition = def,
                RawItems = new List<IRawInventoryItem>(),
                Classification = ComponentClassification.Curated
            });
        }

        return new ComponentInventory
        {
            Discovered = discovered,
            Cancelled = cancelled,
            Categories = categories,
            Entries = entries
        };
    }

    private static List<ComponentInventoryEntry> CollapseByDefinition(List<ComponentInventoryEntry> entries)
    {
        var byKey = new Dictionary<string, ComponentInventoryEntry>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var entry in entries)
        {
            // Group curated matches by definition id; group unclassified/protected/
            // unsupported by raw identity so one logical component with several
            // technical targets collapses into a single row with merged RawItems.
            var key = entry.Definition?.Id
                ?? ("raw:" + (entry.RawItems.Count > 0 ? entry.RawItems[0].RawIdentity : Guid.NewGuid().ToString()));

            if (!byKey.ContainsKey(key))
            {
                byKey[key] = entry;
                order.Add(key);
                continue;
            }

            var existing = byKey[key];
            var merged = new List<IRawInventoryItem>(existing.RawItems);
            merged.AddRange(entry.RawItems);
            byKey[key] = new ComponentInventoryEntry
            {
                Definition = existing.Definition,
                RawItems = merged,
                Classification = existing.Classification
            };
        }

        return order.Select(k => byKey[k]).ToList();
    }

    private static ComponentClassification Classify(IRawInventoryItem item, ComponentDefinition? def)
    {
        if (def is not null)
        {
            return ComponentClassification.Curated;
        }

        if (UnsupportedCategories.Contains(item.Category))
        {
            return ComponentClassification.Unsupported;
        }

        if (IsProtectedIdentity(item.RawIdentity))
        {
            return ComponentClassification.Protected;
        }

        return ComponentClassification.DiscoveredUnclassified;
    }

    private static ComponentDefinition? FindMatchingDefinition(
        IRawInventoryItem item, IReadOnlyList<ComponentDefinition> catalog)
    {
        foreach (var def in catalog)
        {
            foreach (var target in def.TechnicalTargets)
            {
                if (target.Category != item.Category)
                {
                    continue;
                }

                if (Matches(item.RawIdentity, target))
                {
                    return def;
                }
            }
        }

        return null;
    }

    private static bool Matches(string identity, TechnicalTarget target)
    {
        if (string.IsNullOrEmpty(identity) || string.IsNullOrEmpty(target.Pattern))
        {
            return false;
        }

        return target.Match switch
        {
            MatchMethod.Exact => string.Equals(identity, target.Pattern, StringComparison.OrdinalIgnoreCase),
            MatchMethod.Prefix => identity.StartsWith(target.Pattern, StringComparison.OrdinalIgnoreCase),
            MatchMethod.Suffix => identity.EndsWith(target.Pattern, StringComparison.OrdinalIgnoreCase),
            MatchMethod.Contains => identity.Contains(target.Pattern, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool IsProtectedIdentity(string identity)
    {
        if (string.IsNullOrEmpty(identity))
        {
            return false;
        }

        var lower = identity.ToLowerInvariant();
        return ProtectedMarkers.Any(m => lower.Contains(m.ToLowerInvariant()));
    }
}
