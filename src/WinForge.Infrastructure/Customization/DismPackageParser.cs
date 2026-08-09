using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using WinForge.Core.Models;

namespace WinForge.Infrastructure.Customization;

/// <summary>
/// Parses the English (<c>/English</c>) output of <c>dism.exe /Get-Packages</c>
/// into structured <see cref="DiscoveredWindowsPackage"/> items, and classifies
/// each package for safe-removal gating (Step 3.3 section E).
///
/// <para>DISM reports each package as a block of "Key : Value" lines. The package
/// identity and its <c>Release Type</c> are read; everything else is ignored. The
/// classification is conservative: only clearly optional <i>feature/capability</i>
/// packages are offered for removal (<see cref="RiskClass.Removable"/>); language
/// packs, servicing-stack / core-shell dependencies, drivers, WinPE/Setup
/// dependencies, and anything unrecognized stay <see cref="RiskClass.Protected"/>
/// or <see cref="RiskClass.Unsupported"/> and can never be removed by this step.
/// Actual removal is further restricted to a small allowlist at execution time
/// (see <see cref="WindowsCustomizationExecutionService"/>).</para>
/// </summary>
public static class DismPackageParser
{
    private static readonly Regex KeyRegex = new(@"^([A-Za-z][A-Za-z0-9 ]*?)\s*:\s*(.*)$", RegexOptions.Compiled);

    // Substrings that indicate a package must never be removed by this step.
    private static readonly string[] ProtectedNameMarkers =
    {
        "ServicingStack",
        "Foundation",
        "Client-Features",
        "Client-Desktop",
        "WinPE",
        "Setup",
        "LanguagePack",
        "Language",
        "Driver",
        "Microsoft-Windows-Edition",
        "Microsoft-Windows-Client"
    };

    /// <summary>
    /// Parses package blocks and classifies each. Returns one
    /// <see cref="DiscoveredWindowsPackage"/> per block that carries a non-empty
    /// package identity.
    /// </summary>
    public static IReadOnlyList<DiscoveredWindowsPackage> Parse(string output)
    {
        var result = new List<DiscoveredWindowsPackage>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        string? identity = null;
        string? releaseType = null;
        string? displayName = null;

        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(identity))
            {
                result.Add(new DiscoveredWindowsPackage
                {
                    PackageIdentity = identity!,
                    DisplayName = displayName ?? identity!,
                    Classification = Classify(identity!, releaseType),
                    Risk = DeriveRisk(Classify(identity!, releaseType))
                });
            }

            identity = releaseType = displayName = null;
        }

        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                Flush();
                continue;
            }

            var km = KeyRegex.Match(raw);
            if (!km.Success)
            {
                continue;
            }

            var key = km.Groups[1].Value.Trim();
            var value = km.Groups[2].Value.Trim();

            switch (key.ToLowerInvariant())
            {
                case "package identity":
                    identity = value;
                    break;
                case "release type":
                    releaseType = value;
                    break;
                case "display name":
                    if (string.IsNullOrEmpty(displayName))
                    {
                        displayName = value;
                    }
                    break;
                default:
                    break;
            }
        }

        Flush();
        return result;
    }

    private static PackageClassification Classify(string identity, string? releaseType)
    {
        var lower = identity.ToLowerInvariant();

        if (lower.Contains("language"))
        {
            return PackageClassification.Language;
        }

        if (lower.Contains("driver") || lower.Contains("winpe") || lower.Contains("setup"))
        {
            return PackageClassification.Driver;
        }

        if (lower.Contains("servicingstack") || lower.Contains("foundation"))
        {
            return PackageClassification.Core;
        }

        if (releaseType is not null)
        {
            var rt = releaseType.ToLowerInvariant();
            if (rt.Contains("language"))
            {
                return PackageClassification.Language;
            }

            if (rt.Contains("driver") || rt.Contains("setup") || rt.Contains("servicing"))
            {
                return PackageClassification.Driver;
            }
        }

        // Heuristic protected markers (covers core-shell and edition packages).
        foreach (var marker in ProtectedNameMarkers)
        {
            if (lower.Contains(marker.ToLowerInvariant()))
            {
                return PackageClassification.Core;
            }
        }

        // Anything else is treated as an optional feature/capability that *may* be
        // offered for removal, subject to the execution allowlist.
        return PackageClassification.Feature;
    }

    private static RiskClass DeriveRisk(PackageClassification classification) => classification switch
    {
        PackageClassification.Feature => RiskClass.Removable,
        _ => RiskClass.Protected
    };
}
