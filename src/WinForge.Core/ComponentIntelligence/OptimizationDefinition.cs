using System.Collections.Generic;

namespace WinForge.Core.Models;

/// <summary>
/// Which Customize tab an <see cref="OptimizationDefinition"/> belongs to. The
/// tab is data — one shared catalog serves all non-AppX tabs and the view models
/// filter by it (Part L: reuse, do not duplicate six knowledge implementations).
/// </summary>
public enum OptimizationTab
{
    Unknown = 0,
    Apps,
    WindowsComponents,
    Services,
    Privacy,
    System,
    Personalization
}

/// <summary>
/// A single registry value change inside an <see cref="OptimizationDefinition"/>.
/// One user decision (e.g. "dark mode") may require several values — each is its
/// own <see cref="RegistryTarget"/> and produces its own plan operation.
/// </summary>
public sealed class RegistryTarget
{
    /// <summary>Offline hive base: SOFTWARE / SYSTEM / DEFAULT_USER (Stage 11.3 ADR-052).</summary>
    public string Hive { get; init; } = "SOFTWARE";

    /// <summary>Key path relative to the loaded hive root (e.g. <c>Policies\Microsoft\Windows\Explorer</c>).</summary>
    public string KeyPath { get; init; } = string.Empty;

    public string ValueName { get; init; } = string.Empty;
    public OfflineRegistryValueKind ValueKind { get; init; } = OfflineRegistryValueKind.DWord;

    /// <summary>The value WinForge writes when this optimization is selected.</summary>
    public string RecommendedData { get; init; } = string.Empty;

    /// <summary>
    /// The Windows/default value WinForge restores on revert (Part O). For a
    /// freshly-created offline image the original value may not exist, so WinForge
    /// records the documented default it would restore.
    /// </summary>
    public string RestoreData { get; init; } = string.Empty;
}

/// <summary>
/// A generalized, knowledge-backed optimization entry for the non-AppX Customize
/// tabs (Services / Privacy / System / Personalization). It carries the same
/// human-facing fields as a <see cref="ComponentDefinition"/> (purpose,
/// recommendation, risk, impact, restore, evidence, compatibility) plus the
/// Stage 11.3 operation taxonomy (<see cref="OptimizationAction"/> /
/// <see cref="OptimizationMechanism"/> / <see cref="OptimizationScope"/>) and the
/// concrete technical payload (registry targets, service name, feature name).
///
/// <para>Text fields are localization keys resolved via
/// <see cref="WinForge.Core.Services.ILocalizationService"/>; Unknown is preferred
/// over invented information.</para>
/// </summary>
public sealed class OptimizationDefinition
{
    /// <summary>Stable WinForge identifier.</summary>
    public string Id { get; init; } = string.Empty;

    public OptimizationTab Tab { get; init; } = OptimizationTab.Unknown;

    public OptimizationAction Action { get; init; } = OptimizationAction.Unknown;
    public OptimizationMechanism Mechanism { get; init; } = OptimizationMechanism.Unknown;
    public OptimizationScope Scope { get; init; } = OptimizationScope.Unknown;

    public string DisplayNameKey { get; init; } = string.Empty;
    public string ShortDescriptionKey { get; init; } = string.Empty;
    public string LongDescriptionKey { get; init; } = string.Empty;

    public RecommendationLevel Recommendation { get; init; } = RecommendationLevel.Unknown;
    public RiskLevel Risk { get; init; } = RiskLevel.Unknown;
    public RemovalSupport Removal { get; init; } = RemovalSupport.Unknown;
    public RestoreSupport Restore { get; init; } = RestoreSupport.Unknown;

    /// <summary>Localization key describing how to revert this change (empty = generic restore text).</summary>
    public string? ReversalKey { get; init; }

    public IReadOnlyList<ComponentScenario> UserScenarios { get; init; } = new List<ComponentScenario>();
    public IReadOnlyList<string> KeepIf { get; init; } = new List<string>();
    public IReadOnlyList<string> RemoveIf { get; init; } = new List<string>();
    public IReadOnlyList<string> KnownImpact { get; init; } = new List<string>();
    public IReadOnlyList<ComponentDependency> Dependencies { get; init; } = new List<ComponentDependency>();

    public IReadOnlyList<CompatibilityRule> CompatibilityRules { get; init; } = new List<CompatibilityRule>();
    public IReadOnlyList<KnowledgeClaim> Provenance { get; init; } = new List<KnowledgeClaim>();

    /// <summary>
    /// Edition capability required for this optimization to be applicable
    /// (Phase 13.20). None = universally applicable; e.g. a future Windows
    /// Sandbox tweak would set <see cref="Compatibility.EditionCapabilityRequirement.Sandbox"/>.
    /// Recommendations never show edition-gated actions as universally valid.
    /// </summary>
    public Compatibility.EditionCapabilityRequirement EditionRequirement { get; init; } = Compatibility.EditionCapabilityRequirement.None;

    /// <summary>Registry value changes this entry performs (Privacy / System / Personalization).</summary>
    public IReadOnlyList<RegistryTarget> RegistryTargets { get; init; } = new List<RegistryTarget>();

    /// <summary>Service name for <see cref="OptimizationMechanism.ServiceStartup"/> entries.</summary>
    public string? ServiceName { get; init; }

    /// <summary>Proposed start type (null = LeaveDefault — informational, never auto-selected).</summary>
    public ServiceStartType? ProposedStartType { get; init; }

    /// <summary>Exact optional-feature / capability name for FEATURE entries.</summary>
    public string? TargetIdentifier { get; init; }

    /// <summary>Hidden from Standard mode until reviewed (Part M).</summary>
    public bool IsStandardVisible { get; init; } = true;
}
