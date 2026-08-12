using System;

namespace WinForge.Core.Models;

/// <summary>
/// A single declarative offline customization operation. A plan describes WHAT
/// WinForge intends to change before execution; each operation carries the
/// exact identity it targets (never a fuzzy match) plus its current validation
/// and execution status. The model is platform-agnostic — it holds data only,
/// not behaviour; the execution engine interprets it.
///
/// <para>
/// Payload fields are operation-type specific and optional:
/// <list type="bullet">
///   <item><description>Appx/Package removal uses <see cref="TargetIdentifier"/> (exact package identity).</description></item>
///   <item><description>Registry operations use <see cref="RegistryHive"/>, <see cref="RegistryKeyPath"/>,
///     <see cref="RegistryValueName"/>, <see cref="RegistryValueKind"/>, <see cref="RegistryValueData"/>.</description></item>
///   <item><description>Service configuration uses <see cref="ServiceName"/> and <see cref="ServiceStartType"/>.</description></item>
/// </list>
/// </para>
/// </summary>
public sealed class CustomizationOperation
{
    /// <summary>A stable, unique operation id (e.g. <c>appx:Microsoft.X...</c>).</summary>
    public string OperationId { get; init; } = Guid.NewGuid().ToString("N");

    public CustomizationCategory Category { get; init; }
    public CustomizationOperationType OperationType { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>Exact identity targeted by the operation (package name, service name, file id).</summary>
    public string? TargetIdentifier { get; init; }

    /// <summary>Whether the user has selected this operation for the plan.</summary>
    public bool IsSelected { get; set; }

    /// <summary>Safety classification of the underlying item.</summary>
    public RiskClass Risk { get; init; } = RiskClass.Unsupported;

    /// <summary>Execution order within the plan (lower runs first).</summary>
    public int ExecutionOrder { get; set; }

    public OperationValidationResult ValidationResult { get; set; } = OperationValidationResult.Valid;
    public CustomizationOperationStatus ExecutionStatus { get; set; } = CustomizationOperationStatus.Pending;
    public string? ErrorDetails { get; set; }

    // ---- Registry payload (SetOfflineRegistryValue / DeleteOfflineRegistryValue) ----

    public string? RegistryHive { get; init; }
    public string? RegistryKeyPath { get; init; }
    public string? RegistryValueName { get; init; }
    public OfflineRegistryValueKind? RegistryValueKind { get; init; }
    public string? RegistryValueData { get; init; }

    // ---- Stage 12.4 plan normalization / provenance ----
    // Two selected optimization items (e.g. Privacy "Windows 聚焦内容" and
    // Personalization "Windows 聚焦（锁屏内容）") can compile to the EXACT SAME
    // registry mutation. The plan layer merges identical effective changes into
    // ONE physical operation; this provenance keeps every originating
    // customization id (and hence every selected item) explainable.

    private readonly List<string> _sourceDefinitionIds = new();

    /// <summary>Every originating customization id merged into this operation (first = primary).</summary>
    public IReadOnlyList<string> SourceDefinitionIds => _sourceDefinitionIds;

    public void AddSourceDefinition(string sourceId)
    {
        if (!string.IsNullOrWhiteSpace(sourceId) && !_sourceDefinitionIds.Contains(sourceId, StringComparer.Ordinal))
        {
            _sourceDefinitionIds.Add(sourceId);
        }
    }

    /// <summary>
    /// Canonical effective-target identity for registry operations, or null for
    /// non-registry operations. The registry SCOPE (OfflineMachine vs DefaultUser)
    /// is part of the identity — two operations are never merged across scopes
    /// merely because the key text matches. Path/value-name comparisons are
    /// case-insensitive with normalized separators (Windows registry semantics).
    /// </summary>
    public string? CanonicalRegistryTarget()
    {
        if (OperationType is not (CustomizationOperationType.SetOfflineRegistryValue
            or CustomizationOperationType.DeleteOfflineRegistryValue))
        {
            return null;
        }

        return string.Join("|",
            Scope?.ToString() ?? string.Empty,
            NormalizeRegistryPath(RegistryHive),
            NormalizeRegistryPath(RegistryKeyPath),
            NormalizeValueName(RegistryValueName));
    }

    /// <summary>
    /// True when <paramref name="other"/> targets the SAME effective registry
    /// target (same scope + hive + normalized key + normalized value name).
    /// </summary>
    public bool TargetsSameRegistryAs(CustomizationOperation other)
    {
        var a = CanonicalRegistryTarget();
        var b = other?.CanonicalRegistryTarget();
        return a is not null && a.Equals(b, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when this operation's intended mutation is semantically identical to
    /// <paramref name="other"/>'s (same type + value kind + normalized data).
    /// Numeric DWord/QWord data is compared by value so "1", "0x1" and "01"
    /// normalize to the same change; other kinds compare case-insensitively.
    /// </summary>
    public bool HasSameEffectiveChangeAs(CustomizationOperation other)
    {
        if (other is null || OperationType != other.OperationType)
        {
            return false;
        }

        if (OperationType is not (CustomizationOperationType.SetOfflineRegistryValue
            or CustomizationOperationType.DeleteOfflineRegistryValue))
        {
            return ReferenceEquals(this, other);
        }

        if (RegistryValueKind != other.RegistryValueKind)
        {
            return false;
        }

        return NormalizeRegistryData(RegistryValueKind, RegistryValueData)
            .Equals(NormalizeRegistryData(other.RegistryValueKind, other.RegistryValueData), StringComparison.Ordinal);
    }

    private static string NormalizeRegistryPath(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();

    private static string NormalizeValueName(string? name)
        => string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim().ToUpperInvariant();

    private static string NormalizeRegistryData(OfflineRegistryValueKind? kind, string? data)
    {
        var raw = string.IsNullOrWhiteSpace(data) ? string.Empty : data.Trim();
        if (raw.Length == 0)
        {
            return raw;
        }

        if (kind is OfflineRegistryValueKind.DWord or OfflineRegistryValueKind.QWord)
        {
            var digits = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
            if (long.TryParse(digits, System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out var asHex))
            {
                return "n:" + asHex;
            }

            if (long.TryParse(digits, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var asDec))
            {
                return "n:" + asDec;
            }
        }

        return raw.ToUpperInvariant();
    }

    /// <summary>Absorbs <paramref name="other"/> into this operation (identical effective change).</summary>
    public void MergeIdentical(CustomizationOperation other)
    {
        AddSourceDefinition(other.OperationId);
        foreach (var src in other.SourceDefinitionIds)
        {
            AddSourceDefinition(src);
        }
    }

    // ---- Service payload (ConfigureOfflineService) ----

    public string? ServiceName { get; init; }
    public ServiceStartType? ServiceStartType { get; init; }

    // ---- Stage 11.3 optimization metadata (ADR-051) ----
    // These describe WHAT KIND of change this is for the Review surface and the
    // offline-image scope it targets. They are data, never behaviour: views and
    // the plan display them, the execution engine still branches on the concrete
    // OperationType.

    /// <summary>User-visible kind of change (Remove / Disable / Configure / Service / Feature).</summary>
    public OptimizationAction? ActionKind { get; init; }

    /// <summary>Concrete technical mechanism (ServiceStartup, ExplorerPreference, …).</summary>
    public OptimizationMechanism? Mechanism { get; init; }

    /// <summary>Offline-image scope the change applies to (OfflineMachine / OfflineDefaultUser / …).</summary>
    public OptimizationScope? Scope { get; init; }

    /// <summary>Localization key describing how to revert this change (empty = generic restore text).</summary>
    public string? ReversalKey { get; init; }

    /// <summary>
    /// The Windows/default value WinForge restores on revert (registry operations).
    /// For a freshly-created offline image the "original" value may not exist, so
    /// WinForge records the documented default it would restore instead (Part O).
    /// </summary>
    public string? RestoreValueData { get; init; }

    /// <summary>
    /// Returns the canonical conflict key used for duplicate/conflict detection.
    /// Two operations with the same key target the same concrete change.
    /// </summary>
    public string ConflictKey => OperationType switch
    {
        CustomizationOperationType.SetOfflineRegistryValue or CustomizationOperationType.DeleteOfflineRegistryValue
            // Stage 12.4: the registry SCOPE is part of the conflict identity —
            // an OfflineMachine change and an OfflineDefaultUser change of the
            // same key/value are DIFFERENT targets and must never collide.
            => $"reg|{Scope?.ToString() ?? string.Empty}|{RegistryHive}|{RegistryKeyPath}|{RegistryValueName}",
        CustomizationOperationType.ConfigureOfflineService
            => $"svc|{ServiceName}",
        CustomizationOperationType.RemoveProvisionedAppx or CustomizationOperationType.RemovePackage
            => $"pkg|{TargetIdentifier}",
        CustomizationOperationType.DisableOptionalFeature
            => $"feat|{TargetIdentifier}",
        CustomizationOperationType.RemoveCapability
            => $"cap|{TargetIdentifier}",
        CustomizationOperationType.RemoveOfflineFile
            => $"file|{TargetIdentifier}",
        _ => OperationId
    };

    /// <summary>
    /// Two operations conflict when one sets a registry value and the other
    /// deletes the same value (or they set the same value to different data).
    /// </summary>
    public bool ConflictsWith(CustomizationOperation other)
    {
        if (other is null || ReferenceEquals(this, other))
        {
            return false;
        }

        if (ConflictKey != other.ConflictKey)
        {
            return false;
        }

        if (OperationType == CustomizationOperationType.SetOfflineRegistryValue &&
            other.OperationType == CustomizationOperationType.DeleteOfflineRegistryValue)
        {
            return true;
        }

        if (OperationType == CustomizationOperationType.DeleteOfflineRegistryValue &&
            other.OperationType == CustomizationOperationType.SetOfflineRegistryValue)
        {
            return true;
        }

        if (OperationType == CustomizationOperationType.SetOfflineRegistryValue &&
            other.OperationType == CustomizationOperationType.SetOfflineRegistryValue &&
            !string.Equals(RegistryValueData, other.RegistryValueData, StringComparison.Ordinal))
        {
            return true;
        }

        if (OperationType == CustomizationOperationType.ConfigureOfflineService &&
            other.OperationType == CustomizationOperationType.ConfigureOfflineService &&
            ServiceStartType != other.ServiceStartType)
        {
            return true;
        }

        return false;
    }
}
