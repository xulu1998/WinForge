namespace WinForge.Core.Models;

/// <summary>
/// The user-visible KIND of change an optimization makes (Stage 11.3 operation
/// taxonomy, ADR-051). The Review surface uses this so it can say "移除"/REMOVE,
/// "禁用"/DISABLE, "配置"/CONFIGURE, "服务"/SERVICE or "功能"/FEATURE instead of
/// labeling every control a "Remove" operation. It is independent of the concrete
/// <see cref="OptimizationMechanism"/> that implements the change.
/// </summary>
public enum OptimizationAction
{
    Unknown = 0,

    /// <summary>Remove an application / component (REMOVE).</summary>
    Remove,

    /// <summary>Disable a behavior, feature, or experience (DISABLE).</summary>
    Disable,

    /// <summary>Set a value / preference to a recommended state (CONFIGURE).</summary>
    Configure,

    /// <summary>Change a Windows service startup type (SERVICE).</summary>
    Service,

    /// <summary>Disable / remove an optional Windows feature or capability (FEATURE).</summary>
    Feature
}

/// <summary>
/// The concrete technical mechanism an optimization uses (Stage 11.3 coverage
/// matrix field). Views never branch on mechanism-specific behavior — the
/// mechanism is carried as DATA on the definition / operation and interpreted by
/// the plan and execution layers.
/// </summary>
public enum OptimizationMechanism
{
    Unknown = 0,
    RemoveProvisionedAppx,
    RemoveCapability,
    DisableOptionalFeature,
    RemovePackage,
    ServiceStartup,
    RegistryPolicy,
    OfflineRegistry,
    ScheduledTask,
    ExplorerPreference,
    StartPreference,
    SearchPreference,
    TaskbarPreference,
    PrivacyPolicy,
    SystemPolicy,
    VisualPreference
}

/// <summary>
/// Where an optimization applies when the target is an OFFLINE Windows image
/// (Stage 11.3 Part J / ADR-052). A tweak that only affects the currently
/// logged-in host user is NOT automatically valid for the offline image — every
/// catalog entry states its scope explicitly, and scopes that cannot be applied
/// to the offline image are marked <see cref="PostInstallRequired"/> or
/// <see cref="UnsupportedOffline"/> rather than silently claimed.
/// </summary>
public enum OptimizationScope
{
    Unknown = 0,

    /// <summary>Machine-wide setting written to the offline SOFTWARE / SYSTEM hives (HKLM-equivalent).</summary>
    OfflineMachine,

    /// <summary>User preference written to the offline Default User profile (<c>Users\Default\NTUSER.DAT</c>).</summary>
    OfflineDefaultUser,

    /// <summary>Applies to all user profiles in the offline image.</summary>
    OfflineAllUsers,

    /// <summary>Provisioned AppX package removal on the offline image.</summary>
    ProvisionedApp,

    /// <summary>Optional feature / capability change on the mounted offline image (DISM).</summary>
    MountedImageFeature,

    /// <summary>Only meaningful after first logon / on the running OS — NOT applied to the offline image.</summary>
    PostInstallRequired,

    /// <summary>Cannot be reliably applied to an offline image with current support.</summary>
    UnsupportedOffline
}
