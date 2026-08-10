namespace WinForge.Core.Models;

/// <summary>WinForge's recommendation for an ordinary user regarding a component.</summary>
public enum RecommendationLevel
{
    Unknown = 0,

    /// <summary>Safe to remove for most users; little or no loss of function.</summary>
    RecommendedRemove,

    /// <summary>May be removed if the user does not use it.</summary>
    OptionalRemove,

    /// <summary>Usually worth keeping; removal may cause inconvenience.</summary>
    UsuallyKeep,

    /// <summary>Only remove if you understand the consequences.</summary>
    AdvancedOnly,

    /// <summary>Never remove — core Windows function depends on it.</summary>
    NeverRemove
}

/// <summary>How risky removal is for a typical user.</summary>
public enum RiskLevel
{
    Unknown = 0,
    Low,
    Medium,
    High,
    Critical
}

/// <summary>Whether WinForge can remove the component.</summary>
public enum RemovalSupport
{
    Unknown = 0,

    /// <summary>Removal is supported by WinForge.</summary>
    Supported,

    /// <summary>Removal is supported only under certain conditions.</summary>
    Conditional,

    /// <summary>Removal is technically possible but experimental / not yet validated.</summary>
    Experimental,

    /// <summary>Removal is blocked — WinForge will not offer it.</summary>
    Blocked
}

/// <summary>How difficult restoration is if the component is removed.</summary>
public enum RestoreSupport
{
    Unknown = 0,

    /// <summary>Easily restored (e.g. reinstall from Store or re-enable feature).</summary>
    Easy,

    /// <summary>Requires Windows install source / ISO.</summary>
    RequiresSource,

    /// <summary>Requires Windows Update to download.</summary>
    RequiresWindowsUpdate,

    /// <summary>Requires reinstalling Windows.</summary>
    ReinstallWindows
}

/// <summary>Confidence in the estimated space savings figure.</summary>
public enum SavingsConfidence
{
    None = 0,
    Low,
    Medium,
    High
}

/// <summary>User scenarios a component may matter to (Stage 11.1 catalog references these).</summary>
public enum ComponentScenario
{
    Unknown = 0,
    Gaming,
    Office,
    Developer,
    Laptop,
    TouchPen,
    PrintingScanning,
    Wsl,
    Docker,
    HyperV,
    WindowsSandbox,
    XboxGamePass,
    MixedReality,
    Accessibility,
    EnterpriseDomain,
    RemoteDesktop,
    Bluetooth,
    WiFi,
    Biometrics,
    WindowsHello
}

/// <summary>How WinForge classifies a discovered component.</summary>
public enum ComponentClassification
{
    Unknown = 0,

    /// <summary>WinForge understands it; human description + risk exist.</summary>
    Curated,

    /// <summary>Windows object exists; WinForge has not classified it.</summary>
    DiscoveredUnclassified,

    /// <summary>Known system-critical / permanent / servicing-sensitive; never offered.</summary>
    Protected,

    /// <summary>Present but WinForge does not support servicing it.</summary>
    Unsupported
}

/// <summary>Relationship between two logical components.</summary>
public enum DependencyRelation
{
    Unknown = 0,
    Requires,
    RequiredBy,
    RelatedTo,
    ConflictsWith,
    RecommendsKeeping
}

/// <summary>How a <see cref="TechnicalTarget"/> matches a raw Windows identity.</summary>
public enum MatchMethod
{
    Unknown = 0,
    Exact,
    Prefix,
    Contains,
    Suffix
}

/// <summary>Outcome of discovering a single component category.</summary>
public enum InventoryStatus
{
    NotAttempted = 0,

    /// <summary>The source succeeded — the item list may legitimately be empty.</summary>
    Success,

    /// <summary>The source failed (command error, unexpected output).</summary>
    Failed,

    /// <summary>Category is designed but not yet implemented in this stage.</summary>
    NotSupported,

    /// <summary>Discovery was cancelled before this category completed.</summary>
    Cancelled
}
