using System.Collections.Generic;

namespace WinForge.Core.Models;

/// <summary>Per-category result of a discovery pass (mirrors DiscoverySourceStatus).</summary>
public sealed class CategoryDiscoveryResult
{
    public ComponentCategory Category { get; init; } = ComponentCategory.Unknown;
    public InventoryStatus Status { get; init; } = InventoryStatus.NotAttempted;
    public string? Error { get; init; }
    public IReadOnlyList<IRawInventoryItem> Items { get; init; } = new List<IRawInventoryItem>();
}

/// <summary>
/// A logical component row in the inventory: either a curated
/// <see cref="ComponentDefinition"/> (with zero or more matched raw items) or an
/// unclassified/protected/unsupported raw item (Definition == null).
/// </summary>
public sealed class ComponentInventoryEntry
{
    public ComponentDefinition? Definition { get; init; }

    /// <summary>Raw Windows objects that map to this logical component (supports multi-target collapse).</summary>
    public IReadOnlyList<IRawInventoryItem> RawItems { get; init; } = new List<IRawInventoryItem>();

    public ComponentClassification Classification { get; init; } = ComponentClassification.DiscoveredUnclassified;

    /// <summary>The first raw item, for convenient technical-detail display.</summary>
    public IRawInventoryItem? RepresentativeRaw => RawItems.Count > 0 ? RawItems[0] : null;

    /// <summary>Stable id: the definition id, or the raw identity when unclassified.</summary>
    public string LogicalId => Definition?.Id ?? (RepresentativeRaw?.RawIdentity ?? string.Empty);
}

/// <summary>
/// Aggregated component-intelligence result. <see cref="Categories"/> carries the
/// raw discovery (per-category status, tolerant of failures). <see cref="Entries"/>
/// carries the classified, de-duplicated logical components produced by
/// <see cref="ComponentMatcher"/>.
/// </summary>
public sealed class ComponentInventory
{
    /// <summary>True when a discovery pass actually inspected the mounted image.</summary>
    public bool Discovered { get; init; }

    /// <summary>True when discovery was cancelled before completing.</summary>
    public bool Cancelled { get; init; }

    public IReadOnlyList<CategoryDiscoveryResult> Categories { get; init; } = new List<CategoryDiscoveryResult>();
    public IReadOnlyList<ComponentInventoryEntry> Entries { get; init; } = new List<ComponentInventoryEntry>();
}
