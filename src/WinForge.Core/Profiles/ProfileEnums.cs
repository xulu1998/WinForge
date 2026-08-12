namespace WinForge.Core.Profiles;

/// <summary>Usage scenario tags a profile is built around (descriptive, not rules).</summary>
public enum ProfileScenario
{
    Unknown = 0,
    Balanced,
    Gaming,
    Developer,
    Office,
    Lightweight,
    DedicatedMinimal,
    XboxGamePass,
    Wsl,
    Docker,
    HyperV,
    WindowsSandbox,
    PrintingScanning
}

/// <summary>What a profile wants done with a logical item.</summary>
public enum ProfileIntent
{
    Unknown = 0,

    /// <summary>The item should be kept for this profile.</summary>
    Keep,

    /// <summary>The item should be trimmed (removed / disabled / set) for this profile.</summary>
    Trim
}

/// <summary>
/// The effective recommendation a profile-aware evaluation produces for one item.
/// Deliberately action-agnostic: display wording is derived per
/// <see cref="WinForge.Core.Models.OptimizationAction"/> (Part L) — the engine
/// reasons in shared levels, views render action-aware language.
/// </summary>
public enum EffectiveRecommendationLevel
{
    Unknown = 0,

    /// <summary>Protected / critical — never selectable, never auto-selected.</summary>
    Blocked,

    /// <summary>Present but no strong steer — the user decides (按需).</summary>
    ManualReview,

    /// <summary>The item should be kept.</summary>
    RecommendKeep,

    /// <summary>A recommended change (Configure / Service startup) should be applied.</summary>
    RecommendSet,

    /// <summary>The item should be disabled (Feature / Disable actions).</summary>
    RecommendDisable,

    /// <summary>The item should be removed (Remove actions).</summary>
    RecommendRemove
}

/// <summary>Which deterministic rule produced the effective level (Part D precedence, documented).</summary>
public enum RecommendationRuleSource
{
    Unknown = 0,

    /// <summary>Critical safety constraint — wins over everything.</summary>
    CriticalSafetyConstraint,

    /// <summary>Explicit user choice — wins over every profile rule.</summary>
    UserOverride,

    /// <summary>A kept component requires this one (dependency edge).</summary>
    RequiredDependency,

    /// <summary>A selected profile lists the item in RequiredCapabilities.</summary>
    ProfileRequirement,

    /// <summary>Scenario recommendation override (profile keep/trim rule).</summary>
    ScenarioOverride,

    /// <summary>The component's own curated default recommendation.</summary>
    ComponentDefault
}
