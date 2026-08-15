using System;
using System.Collections.Generic;

namespace WinForge.Core.Health;

/// <summary>
/// Status vocabulary for the full-health report (Phase 16 Stage 16.1, ADR-098).
/// Deliberately non-binary: NotTested exists so an offline/uninstrumented VM is
/// never reported as a false Pass, and Warning exists so non-critical findings
/// do not masquerade as either Pass or Fail.
/// </summary>
public enum HealthStatus
{
    Pass = 0,
    Warning = 1,
    NotTested = 2,
    Fail = 3,
}

/// <summary>One check inside a health section.</summary>
public sealed class HealthCheckItem
{
    public string Name { get; set; } = string.Empty;
    public HealthStatus Status { get; set; } = HealthStatus.Pass;
    public string Detail { get; set; } = string.Empty;
}

/// <summary>One named section of the report (media, servicing, security, …).</summary>
public sealed class HealthSection
{
    public HealthStatus Status { get; set; } = HealthStatus.NotTested;
    public List<HealthCheckItem> Checks { get; set; } = new();
}

/// <summary>
/// Typed model of <c>full-health-report.json</c> produced by
/// <c>scripts/Validate-WinForgeInstallation.ps1</c> inside the installed VM.
/// The section names follow the Stage 16.1 schema; overallStatus is aggregated
/// by <c>HealthReportAggregator</c> (Fail &gt; Warning &gt; NotTested &gt; Pass).
/// </summary>
public sealed class FullHealthReport
{
    /// <summary>Raw JSON the report was parsed from (diagnostic; not serialized back).</summary>
    public string? RawJson { get; set; }

    /// <summary>Media / ISO identity section (isoName, isoSha256 when known).</summary>
    public HealthSection Media { get; set; } = new();

    /// <summary>Profile under validation (e.g. "Balanced").</summary>
    public HealthSection Profile { get; set; } = new();

    /// <summary>Edition / build / architecture / language / activation / boot state.</summary>
    public HealthSection WindowsIdentity { get; set; } = new();

    /// <summary>Shell / Explorer / Start menu availability (desktop reached).</summary>
    public HealthSection BootAndShell { get; set; } = new();

    /// <summary>Device Manager problems, display / network / audio presence.</summary>
    public HealthSection Devices { get; set; } = new();

    /// <summary>Adapter state, DHCP/IP, DNS, HTTPS (offline VM reported distinctly).</summary>
    public HealthSection Network { get; set; } = new();

    /// <summary>DISM /CheckHealth, sfc /verifyonly, servicing stack.</summary>
    public HealthSection Servicing { get; set; } = new();

    /// <summary>Windows Update service / orchestration components presence.</summary>
    public HealthSection WindowsUpdate { get; set; } = new();

    /// <summary>SecHealthUI / Defender / firewall presence.</summary>
    public HealthSection Security { get; set; } = new();

    /// <summary>Microsoft Store / App Installer / frameworks presence.</summary>
    public HealthSection StoreAndAppPlatform { get; set; } = new();

    /// <summary>Profile-intended expected-state checks (e.g. Balanced removals + policy values).</summary>
    public HealthSection ProfileExpectedChanges { get; set; } = new();

    /// <summary>Aggregated status of all sections (computed; not trusted from the script).</summary>
    public HealthStatus OverallStatus { get; set; } = HealthStatus.NotTested;

    /// <summary>Non-critical findings (e.g. VM offline, activation not licensed).</summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>Critical findings — any failure blocks FullHealthValidated.</summary>
    public List<string> Failures { get; set; } = new();

    /// <summary>
    /// True only when every gate for ADR-084 FullHealthValidated is satisfied:
    /// the report is schema-valid, every section was actually tested, no section
    /// Failed, and the critical sections (bootAndShell, servicing, security,
    /// network) show Pass. Warnings do NOT block full-health validation.
    /// </summary>
    public bool FullHealthValidated { get; set; }

    public static readonly string[] RequiredSections =
    {
        "media", "profile", "windowsIdentity", "bootAndShell", "devices", "network",
        "servicing", "windowsUpdate", "security", "storeAndAppPlatform",
        "profileExpectedChanges",
    };

    /// <summary>Sections that must be actually tested (not NotTested) for FullHealthValidated.</summary>
    public static readonly string[] CriticalSections = { "bootAndShell", "servicing", "security", "network" };
}

/// <summary>
/// Expected post-install state for a profile, loaded from
/// <c>scripts/&lt;profile&gt;-expected-state.json</c> and verified from the
/// installed OS by <c>scripts/Validate-WinForgeInstallation.ps1</c>.
/// </summary>
public sealed class ProfileExpectedState
{
    public string ProfileId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>AppX package name prefixes that must be ABSENT after install.</summary>
    public List<string> AppxAbsent { get; set; } = new();

    /// <summary>Machine (OfflineMachine) registry values the profile sets.</summary>
    public List<ExpectedRegistryValue> MachineRegistry { get; set; } = new();

    /// <summary>Default-User (OfflineDefaultUser) registry values the profile sets.</summary>
    public List<ExpectedRegistryValue> DefaultUserRegistry { get; set; } = new();
}

/// <summary>One expected registry value (kind is informational; DWord by default).</summary>
public sealed class ExpectedRegistryValue
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ExpectedData { get; set; } = string.Empty;
    public string Kind { get; set; } = "DWord";
}
