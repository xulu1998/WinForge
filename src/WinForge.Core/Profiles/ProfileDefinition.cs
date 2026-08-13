using System.Collections.Generic;
using WinForge.Core.Models;

namespace WinForge.Core.Profiles;

/// <summary>
/// One profile rule for a single logical item: keep it or trim it, with a
/// deterministic localized reason key. The target is ALWAYS a stable WinForge
/// logical id (component / optimization id) — never a raw Windows package name
/// (Part A).
/// </summary>
public sealed class ProfileRecommendationOverride
{
    /// <summary>Logical <see cref="ComponentDefinition.Id"/> / <see cref="OptimizationDefinition.Id"/>.</summary>
    public string TargetId { get; init; } = string.Empty;

    public ProfileIntent Intent { get; init; } = ProfileIntent.Unknown;

    /// <summary>Deterministic localization key, e.g. <c>Profile.Reason.Gaming.Xbox</c>.</summary>
    public string ReasonKey { get; init; } = string.Empty;

    /// <summary>
    /// Rule tier within the scenario-override level. Higher numbers lose to lower
    /// numbers when two selected profiles disagree on the same item (KEEP wins on
    /// conflict regardless — this only orders keep-vs-keep and trim-vs-trim).
    /// </summary>
    public int Tier { get; init; } = 5;
}

/// <summary>
/// A first-class usage scenario profile ("what kind of Windows are you building?").
/// Profiles operate on logical WinForge ids and describe PRIORITIES, not hard
/// deletion lists. They RECOMMEND; they never silently remove (product principle).
///
/// <para>Rule precedence (Part D, documented in ADR-058):</para>
/// <list type="number">
/// <item>Critical safety constraint</item>
/// <item>Explicit user keep preference (user override)</item>
/// <item>Required dependency (a kept component requires this one)</item>
/// <item>Profile requirement (<see cref="RequiredCapabilities"/>, present-gated)</item>
/// <item>Scenario recommendation override (<see cref="RecommendationOverrides"/> /
/// <see cref="PreferredCapabilities"/> / <see cref="AvoidedComponents"/>)</item>
/// <item>Component default recommendation</item>
/// </list>
/// </summary>
public sealed class ProfileDefinition
{
    /// <summary>Stable id, e.g. <c>Gaming</c>, <c>Developer</c>, <c>Lightweight</c>.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Primary (mutually-exclusive radio card) or ExtraScenario (independent
    /// secondary checkbox, Part 2). Both feed the same engine rule model.
    /// </summary>
    public ProfileKind Kind { get; init; } = ProfileKind.Primary;

    public string DisplayNameKey { get; init; } = string.Empty;
    public string DescriptionKey { get; init; } = string.Empty;

    /// <summary>Localization key for a small icon glyph.</summary>
    public string IconKey { get; init; } = string.Empty;

    /// <summary>Descriptive usage-scenario tags (Part C — priorities, not checkboxes).</summary>
    public IReadOnlyList<ProfileScenario> Scenarios { get; init; } = new List<ProfileScenario>();

    /// <summary>
    /// Phase 14.3: when set, this primary profile is powered by the KNOWLEDGE-DRIVEN
    /// gaming pipeline (ADR-088/089) — the profile's per-item recommendations come
    /// from <see cref="GamingProfileEvaluationService"/> consuming deep component
    /// knowledge, not from hand-maintained raw-id override lists. The legacy
    /// <see cref="RecommendationOverrides"/>/requirements may coexist as explicit
    /// conservative keep rules, but the policy layer is the primary mechanism.
    /// </summary>
    public GamingProfileKind? GamingKind { get; init; }

    /// <summary>Explicit per-item keep/trim rules with deterministic reasons.</summary>
    public IReadOnlyList<ProfileRecommendationOverride> RecommendationOverrides { get; init; } = new List<ProfileRecommendationOverride>();

    /// <summary>
    /// Logical ids this profile REQUIRES when present on the image (hard keep,
    /// tier 4). Wins conflicts against another profile's trim (visible resolution).
    /// </summary>
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = new List<string>();

    /// <summary>Logical ids this profile prefers to keep (soft keep, tier 5).</summary>
    public IReadOnlyList<string> PreferredCapabilities { get; init; } = new List<string>();

    /// <summary>Logical ids this profile trims (equivalent to Trim overrides, tier 5).</summary>
    public IReadOnlyList<string> AvoidedComponents { get; init; } = new List<string>();

    public IReadOnlyList<CompatibilityRule> CompatibilityRules { get; init; } = new List<CompatibilityRule>();
}
