using System;
using System.Linq;

namespace WinForge.Core.Models;

/// <summary>
/// Single source of truth for which offline Windows services Step 3.3 may
/// reconfigure. The exact same policy governs every layer of the customization
/// pipeline so a service can never be "selectable in the UI but refused at
/// execution" (or vice-versa):
///
/// <list type="bullet">
///   <item><description>Discovery / classification — a service not on the allowlist is classified
///     <see cref="ServiceClass.Protected"/> (or <see cref="ServiceClass.Driver"/> for drivers) and is
///     therefore not selectable in the UI.</description></item>
///   <item><description>Plan validation — a <c>ConfigureOfflineService</c> operation whose service name
///     is not allowlisted is flagged <see cref="OperationValidationResult.Unsupported"/> and blocks execution.</description></item>
///   <item><description>PlanSync — refuses to add a <c>ConfigureOfflineService</c> operation for any
///     unapproved service name, even if called directly with a crafted operation.</description></item>
///   <item><description>Execution — a non-allowlisted service operation is skipped as a final
///     defense-in-depth guard (the operation should never have reached execution).</description></item>
/// </list>
///
/// <para>Only the explicit trusted allowlist is configurable. This deliberately
/// mirrors <see cref="CustomizationDefinitionProvider.GetRecommendedServiceChanges"/>
/// (DiagTrack / WerSvc / PcaSvc) — the canonical trusted definitions. A unit test
/// pins the two lists to stay in sync. Everything else (language/servicing
/// dependencies, drivers, kernel components, performance / provider entries such
/// as <c>.NET CLR Data</c>, <c>.NET Data Provider for Oracle</c>, and the
/// hundreds of other arbitrary <c>Services</c> sub-keys) is Protected and can
/// never be reconfigured by this step.</para>
/// </summary>
public static class ServiceConfigPolicy
{
    // The ONLY offline services Step 3.3/11.3 may reconfigure. Matching is a
    // case-insensitive substring against the service name, so a version- or
    // edition-suffixed name still matches the base marker. The first three are
    // the Step 3.3 trusted set; the remainder are Stage 11.3 reviewed additions
    // (Xbox/gaming accessories, retail demo, offline maps, media sharing, touch
    // input, geolocation) — each has human purpose + risk + revert in the
    // OptimizationCatalog and a unit test pins the catalog to this allowlist.
    public static IReadOnlyList<string> AllowedServiceMarkers { get; } = new[]
    {
        "DiagTrack",
        "WerSvc",
        "PcaSvc",
        "XboxGipSvc",
        "XboxNetApiSvc",
        "XblAuthManager",
        "RetailDemo",
        "MapsBroker",
        "WMPNetworkSvc",
        "TabletInputService",
        "lfsvc"
    };

    /// <summary>
    /// Returns true only when <paramref name="serviceName"/> is on the explicit
    /// configuration allowlist. Null/empty names are never allowed.
    /// </summary>
    public static bool IsConfigurable(string? serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return false;
        }

        var lower = serviceName.ToLowerInvariant();
        return AllowedServiceMarkers.Any(m => lower.Contains(m.ToLowerInvariant()));
    }
}
