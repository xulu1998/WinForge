using System.Collections.Generic;

namespace WinForge.Core.Models;

/// <summary>Install state of a Windows Capability as reported by DISM.</summary>
public enum CapabilityState
{
    Unknown = 0,
    Installed,
    NotPresent,
    Error
}

/// <summary>Enable state of a Windows Optional Feature as reported by DISM.</summary>
public enum FeatureState
{
    Unknown = 0,
    Enabled,
    Disabled
}

/// <summary>Install state of a Windows CBS / OS servicing package as reported by DISM.</summary>
public enum CbsPackageState
{
    Unknown = 0,
    Installed,
    Staged,
    Superseded,
    PartiallyInstalled
}

/// <summary>
/// A single Windows object discovered from an offline image. The UI never depends
/// on the raw identity directly — it is mapped onto a <see cref="ComponentDefinition"/>
/// through the catalog. Strongly-typed subclasses capture the exact fields required
/// per Stage 11.1 category; <see cref="Properties"/> carries any extra metadata the
/// parser captured but the model does not name explicitly.
/// </summary>
public interface IRawInventoryItem
{
    /// <summary>The discovery category this raw item belongs to.</summary>
    ComponentCategory Category { get; }

    /// <summary>The stable Windows identity (package name, capability identity, feature name, …).</summary>
    string RawIdentity { get; }

    /// <summary>Human-friendly name where available (e.g. AppX DisplayName).</summary>
    string DisplayName { get; }

    /// <summary>Version where available.</summary>
    string? Version { get; }

    /// <summary>Raw state string (Installed / Enabled / Provisioned / …) for display.</summary>
    string? State { get; }
}

/// <summary>Base class for all raw inventory items (Stage 11.1, read-only discovery).</summary>
public abstract class RawInventoryItem : IRawInventoryItem
{
    public ComponentCategory Category { get; init; } = ComponentCategory.Unknown;
    public string RawIdentity { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string? State { get; init; }
    public string? Architecture { get; init; }
    public string? Publisher { get; init; }

    /// <summary>Extra category-specific metadata captured by the parser (never null).</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

/// <summary>Provisioned AppX package (DISM /Get-ProvisionedAppxPackages).</summary>
public sealed class RawAppxPackage : RawInventoryItem
{
    /// <summary>Package family name (&lt;name&gt;_&lt;publisher-hash&gt;), derived from PackageName.</summary>
    public string? PackageFamilyName { get; init; }

    /// <summary>ResourceId (e.g. "~").</summary>
    public string? ResourceId { get; init; }

    /// <summary>Signature kind where DISM reports it.</summary>
    public string? SignatureKind { get; init; }
}

/// <summary>Windows Capability (DISM /Get-Capabilities).</summary>
public sealed class RawCapability : RawInventoryItem
{
    public CapabilityState CapState { get; init; } = CapabilityState.Unknown;
}

/// <summary>Windows Optional Feature (DISM /Get-Features).</summary>
public sealed class RawOptionalFeature : RawInventoryItem
{
    public FeatureState FeatureStateValue { get; init; } = FeatureState.Unknown;

    /// <summary>Parent feature name where discoverable.</summary>
    public string? Parent { get; init; }

    /// <summary>Whether enabling/disabling requires a restart (where DISM reports it).</summary>
    public bool RestartRequired { get; init; }
}

/// <summary>Windows CBS / OS servicing package (DISM /Get-Packages).</summary>
public sealed class RawCbsPackage : RawInventoryItem
{
    public CbsPackageState PkgState { get; init; } = CbsPackageState.Unknown;
    public string? ReleaseType { get; init; }
    public string? InstallTime { get; init; }
    public bool Permanent { get; init; }
    public string? Applicable { get; init; }
}
