using System.Collections.Generic;
using System.Linq;

namespace WinForge.Core.Compatibility;

/// <summary>
/// Safety invariants (Stages 13.15–13.18): standard optimization must NEVER
/// disable essential update / Defender infrastructure, remove core driver
/// packages, or casually break the Store. The catalog assertions below are the
/// authoritative list; tests assert every definition in the catalogs honors them.
/// </summary>
public static class SafetyInvariantCatalog
{
    // ---- Windows Update infrastructure (13.15) ----
    public static readonly IReadOnlyList<string> EssentialUpdateServices = new[]
    {
        "wuauserv",      // Windows Update
        "BITS",          // Background Intelligent Transfer Service
        "UsoSvc",        // Update Orchestrator Service
        "WaaSMedicSvc",  // Windows Update Medic Service
    };

    // ---- Defender (13.16) ----
    public static readonly IReadOnlyList<string> DefenderServices = new[]
    {
        "WinDefend",             // Windows Defender Antivirus Service
        "SecurityHealthService", // Windows Security Service
    };

    // ---- Driver store (13.18) — boot-critical / storage / USB / network / display base ----
    public static readonly IReadOnlyList<string> CoreDriverPackages = new[]
    {
        "Microsoft-Windows-Client-Drivers-Package",
        "Microsoft-Windows-Client-Drivers-Package~31bf3856ad364e35~amd64~~10.0",
        "Microsoft-Windows-Storage-*",
        "Microsoft-Windows-USB-*",
        "Microsoft-Windows-Networking-*",
        "Microsoft-Windows-Display-*",
    };

    // ---- Store / AppX health (13.17) ----
    public static readonly IReadOnlyList<string> StorePackages = new[]
    {
        "Microsoft.WindowsStore",
        "Microsoft.StorePurchaseApp",
        "Microsoft.DesktopAppInstaller", // App Installer / winget relationship
        "Microsoft.StoreExperienceHost",
    };

    /// <summary>True when a service name is essential Windows Update infrastructure.</summary>
    public static bool IsEssentialUpdateService(string serviceName)
        => EssentialUpdateServices.Contains(serviceName, System.StringComparer.OrdinalIgnoreCase);

    /// <summary>True when a service name is part of the Defender surface.</summary>
    public static bool IsDefenderService(string serviceName)
        => DefenderServices.Contains(serviceName, System.StringComparer.OrdinalIgnoreCase);

    /// <summary>True when a package identity looks like a core driver package.</summary>
    public static bool IsCoreDriverPackage(string packageIdentity)
        => CoreDriverPackages.Any(p =>
            p.EndsWith("*", System.StringComparison.Ordinal)
                ? packageIdentity.StartsWith(p.TrimEnd('*'), System.StringComparison.OrdinalIgnoreCase)
                : string.Equals(packageIdentity, p, System.StringComparison.OrdinalIgnoreCase));

    /// <summary>True when a package identity is part of the Store surface.</summary>
    public static bool IsStorePackage(string packageIdentity)
        => StorePackages.Contains(packageIdentity, System.StringComparer.OrdinalIgnoreCase);
}
