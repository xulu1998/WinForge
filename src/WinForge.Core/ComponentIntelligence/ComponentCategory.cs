namespace WinForge.Core.Models;

/// <summary>
/// High-level category of a Windows object discovered in an offline image.
/// Mirrors the Stage 11.1 discovery taxonomy: A-D are implemented for read-only
/// discovery, E-J are designed (provider interfaces exist) but not yet serviced.
/// </summary>
public enum ComponentCategory
{
    Unknown = 0,

    /// <summary>Provisioned AppX package.</summary>
    AppX,

    /// <summary>Windows Capability.</summary>
    Capability,

    /// <summary>Windows Optional Feature.</summary>
    OptionalFeature,

    /// <summary>Windows CBS / OS servicing package.</summary>
    CbsPackage,

    /// <summary>Windows service.</summary>
    Service,

    /// <summary>Scheduled task.</summary>
    ScheduledTask,

    /// <summary>Driver.</summary>
    Driver,

    /// <summary>Language / language feature.</summary>
    Language,

    /// <summary>Windows Recovery / WinRE.</summary>
    WinRecovery,

    /// <summary>System application / protected component.</summary>
    SystemApp,

    /// <summary>Known system-critical / permanent / servicing-sensitive object.</summary>
    Protected
}
