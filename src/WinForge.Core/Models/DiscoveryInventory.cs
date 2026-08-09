using System.Collections.Generic;

namespace WinForge.Core.Models;

/// <summary>
/// A provisioned Appx package discovered from the offline mounted image. The
/// engine removes it only by <see cref="PackageName"/> (exact identity) — never
/// by fuzzy substring matching.
/// </summary>
public sealed class DiscoveredAppxPackage
{
    public string PackageName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? Architecture { get; init; }
    public string? Publisher { get; init; }

    /// <summary>Whether the package is present in the offline image.</summary>
    public bool Present { get; init; } = true;

    /// <summary>Safety classification — only <see cref="RiskClass.Safe"/>/Removable may be removed.</summary>
    public RiskClass Risk { get; init; } = RiskClass.Safe;
}

/// <summary>
/// A Windows servicing / capability package discovered from the offline image.
/// Actual removal is restricted to an explicit allowlisted category; everything
/// else is classified Protected/Unsupported and cannot be removed.
/// </summary>
public sealed class DiscoveredWindowsPackage
{
    public string PackageIdentity { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Coarse classification used for safe-removal gating.</summary>
    public PackageClassification Classification { get; init; } = PackageClassification.Unknown;

    public RiskClass Risk { get; init; } = RiskClass.Protected;
}

/// <summary>Coarse servicing-package classification for Step 3.3 safety gating.</summary>
public enum PackageClassification
{
    /// <summary>Language / locale infrastructure — never auto-removed.</summary>
    Language,

    /// <summary>Core shell / servicing-stack dependency — never removed.</summary>
    Core,

    /// <summary>Feature / capability that is safe to offer for removal under the allowlist.</summary>
    Feature,

    /// <summary>Driver or setup dependency — never removed.</summary>
    Driver,

    /// <summary>Unrecognized — treated as unsupported.</summary>
    Unknown
}

/// <summary>
/// An offline Windows service discovered from the SYSTEM hive of the mounted
/// image. Only services that exist in the image and belong to the mounted
/// workspace may be reconfigured.
/// </summary>
public sealed class DiscoveredOfflineService
{
    public string ServiceName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Current START value as read from the offline hive (0-4).</summary>
    public int CurrentStartValue { get; init; }

    public ServiceStartType CurrentStartType => (ServiceStartType)CurrentStartValue;

    /// <summary>
    /// The start type a trusted definition recommends for this service (e.g.
    /// Disabled). Null for services discovered directly from the image where no
    /// recommendation applies.
    /// </summary>
    public ServiceStartType? RecommendedStartType { get; init; }

    public RiskClass Risk { get; init; } = RiskClass.Removable;
}

/// <summary>
/// A trusted, offline-applicable registry-backed setting definition (Privacy /
/// System pages). These are generated only by WinForge's definition provider —
/// never from arbitrary UI input.
/// </summary>
public sealed class DiscoveredRegistrySetting
{
    public string SettingId { get; init; } = string.Empty;
    public CustomizationCategory Category { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    public string Hive { get; init; } = string.Empty;
    public string KeyPath { get; init; } = string.Empty;
    public string ValueName { get; init; } = string.Empty;
    public OfflineRegistryValueKind ValueKind { get; init; }
    public string RecommendedData { get; init; } = string.Empty;

    public RiskClass Risk { get; init; } = RiskClass.Safe;
}

/// <summary>
/// Per-source outcome of a discovery pass. A failed source (DISM error,
/// unrecognized/localized output, offline hive load/enumeration failure) is
/// reported as <see cref="Failed"/> with an error message — it must NEVER be
/// silently collapsed into an empty (zero-item) inventory.
/// </summary>
public enum DiscoverySourceStatus
{
    /// <summary>The source was not attempted (e.g. no usable mounted session).</summary>
    NotAttempted,

    /// <summary>The source succeeded — the item list may legitimately be empty.</summary>
    Success,

    /// <summary>The source failed (command error, unexpected output, or registry error).</summary>
    Failed
}

/// <summary>
/// Aggregated discovery result returned by <see cref="ICustomizationDiscoveryService"/>.
/// Structured data only — never raw DISM text reaches the UI.
/// </summary>
public sealed class DiscoveryInventory
{
    public IReadOnlyList<DiscoveredAppxPackage> AppxPackages { get; init; } = new List<DiscoveredAppxPackage>();
    public IReadOnlyList<DiscoveredWindowsPackage> WindowsPackages { get; init; } = new List<DiscoveredWindowsPackage>();
    public IReadOnlyList<DiscoveredOfflineService> Services { get; init; } = new List<DiscoveredOfflineService>();
    public IReadOnlyList<DiscoveredRegistrySetting> RegistrySettings { get; init; } = new List<DiscoveredRegistrySetting>();

    /// <summary>True when a discovery pass actually inspected the mounted image.</summary>
    public bool Discovered { get; init; }

    // --- Per-source status (distinguishes success-with-zero from failure) ---

    public DiscoverySourceStatus AppxStatus { get; init; } = DiscoverySourceStatus.NotAttempted;
    public string? AppxError { get; init; }

    public DiscoverySourceStatus PackageStatus { get; init; } = DiscoverySourceStatus.NotAttempted;
    public string? PackageError { get; init; }

    public DiscoverySourceStatus ServiceStatus { get; init; } = DiscoverySourceStatus.NotAttempted;
    public string? ServiceError { get; init; }
}
