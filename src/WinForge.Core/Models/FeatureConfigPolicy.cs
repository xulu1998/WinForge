using System;
using System.Linq;

namespace WinForge.Core.Models;

/// <summary>
/// Single source of truth for which offline optional features / capabilities
/// Stage 11.3 may disable/remove (ADR-051, mirroring <see cref="ServiceConfigPolicy"/>
/// and <see cref="PackageRemovalPolicy"/>). Only exact, WinForge-reviewed DISM
/// FeatureNames / capability identities are allowed. The same policy governs the
/// knowledge catalog selectability, plan validation, and the execution-time
/// defense-in-depth guard, so a feature can never be "selectable in the UI but
/// refused at execution" (or vice-versa).
///
/// <para>Matching is an EXACT, case-insensitive comparison against the feature
/// name. A unit test pins this list to the Windows Features catalog so the two
/// cannot drift.</para>
/// </summary>
public static class FeatureConfigPolicy
{
    /// <summary>
    /// The ONLY offline optional features Stage 11.3 may disable. Every name is a
    /// documented DISM FeatureName reviewed against the real Windows 11 25H2 image.
    /// </summary>
    public static IReadOnlyList<string> AllowedFeatureNames { get; } = new[]
    {
        "Microsoft-Hyper-V",
        "Microsoft-Hyper-V-Management-PowerShell",
        "Containers-DisposableClientVM",
        "Microsoft-Windows-Subsystem-Linux",
        "VirtualMachinePlatform",
        "OpenSSH.Client",
        "OpenSSH.Server",
        "WindowsMediaPlayer",
        "Internet-Printing-Client",
        "ScanManagementConsole",
        "Printing-XPSServices-Features",
        "MicrosoftWindowsPowerShellV2Root",
        "HypervisorPlatform"
    };

    /// <summary>Capabilities are not offered in the first tranche — the allowlist is empty.</summary>
    public static IReadOnlyList<string> AllowedCapabilityNames { get; } = Array.Empty<string>();

    public static bool IsFeatureAllowed(string? featureName)
    {
        if (string.IsNullOrWhiteSpace(featureName))
        {
            return false;
        }

        return AllowedFeatureNames.Any(n => string.Equals(n, featureName, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsCapabilityAllowed(string? capabilityName)
    {
        if (string.IsNullOrWhiteSpace(capabilityName))
        {
            return false;
        }

        return AllowedCapabilityNames.Any(n => string.Equals(n, capabilityName, StringComparison.OrdinalIgnoreCase));
    }
}
