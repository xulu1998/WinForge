using System;
using System.Linq;

namespace WinForge.Infrastructure.Customization;

/// <summary>
/// Single source of truth for which Windows servicing packages Step 3.3 may
/// remove. The exact same policy governs every layer of the customization
/// pipeline so a package can never be "selectable in the UI but skipped at
/// execution" (or vice-versa):
///
/// <list type="bullet">
///   <item><description>Discovery / classification — a non-allowlisted package is classified
///     <see cref="RiskClass.Protected"/> and is therefore not selectable in the UI.</description></item>
///   <item><description>Plan validation — an operation whose underlying package is not allowlisted
///     is flagged <see cref="OperationValidationResult.Unsupported"/> and blocks execution.</description></item>
///   <item><description>Execution — a non-allowlisted package operation is skipped as a final
///     defense-in-depth guard (the operation should never have reached execution).</description></item>
/// </list>
///
/// <para>Everything not on this explicit allowlist — language packs, servicing
/// stack, core-shell, driver, WinPE/Setup, OneCore, and edition packages — is
/// Protected and can never be selected or removed by this step.</para>
/// </summary>
public static class PackageRemovalPolicy
{
    // The ONLY Windows packages removable by Step 3.3. Matching is a
    // case-insensitive substring against the full DISM package identity, so the
    // version/build-suffixed identity (e.g.
    // "...InternetExplorer-Optional-Package~31bf3856ad364e35~amd64~~10.0.26100.1")
    // still matches the base marker.
    public static IReadOnlyList<string> AllowedPackageMarkers { get; } = new[]
    {
        "Microsoft-Windows-InternetExplorer-Optional",
        "Microsoft-Windows-Printing-XPSServices",
        "Microsoft-Xps-Document-Writer"
    };

    /// <summary>
    /// Returns true only when <paramref name="identity"/> is on the explicit
    /// removal allowlist. Null/empty identities are never allowed.
    /// </summary>
    public static bool IsRemovalAllowed(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            return false;
        }

        var lower = identity.ToLowerInvariant();
        return AllowedPackageMarkers.Any(m => lower.Contains(m.ToLowerInvariant()));
    }
}
