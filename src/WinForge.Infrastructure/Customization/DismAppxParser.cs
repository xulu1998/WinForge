using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using WinForge.Core.Models;

namespace WinForge.Infrastructure.Customization;

/// <summary>
/// Parses the English (<c>/English</c>) output of
/// <c>dism.exe /Get-ProvisionedAppxPackages</c> into structured
/// <see cref="DiscoveredAppxPackage"/> items.
///
/// <para>DISM reports each provisioned package as a block of "Key : Value" lines
/// (the same tolerant key/value grammar used elsewhere in WinForge). On a real
/// Windows image the <c>/English</c> run emits the SINGLE-WORD headers
/// <c>PackageName</c> and <c>DisplayName</c> — never the synthetic multi-word
/// "Deployment package name" / "Display name" that earlier parsing invented.
/// Both forms are accepted for robustness.</para>
///
/// <para>The block's removal identity is the exact <c>PackageName</c> value
/// (e.g. <c>Clipchamp.Clipchamp_4.4.10720.0_neutral_~_yxz26nhyzhsrt</c>), which is
/// precisely what <c>dism /Remove-ProvisionedAppxPackage /PackageName:…</c>
/// requires. <c>DisplayName</c> (e.g. <c>Clipchamp.Clipchamp</c>) is captured
/// for display only and is NEVER used as the destructive-operation target. A block
/// that lacks a <c>PackageName</c> is dropped rather than falling back to
/// <c>DisplayName</c> — there is deliberately NO substring/fuzzy matching.</para>
///
/// <para>Empty / missing output is tolerated: an empty list is returned. Unknown
/// DISM fields are ignored; only the stable, documented keys are read.</para>
/// </summary>
public static class DismAppxParser
{
    private static readonly Regex KeyRegex = new(@"^([A-Za-z][A-Za-z0-9 ]*?)\s*:\s*(.*)$", RegexOptions.Compiled);

    // Real `dism /Get-ProvisionedAppxPackages` (with /English) emits SINGLE-WORD
    // headers — "PackageName" and "DisplayName" — NOT the multi-word "Deployment
    // package name" / "Display name" invented by earlier parsing. Both forms are
    // accepted so the parser is robust to either DISM edition/format.
    private static readonly string[] RecognizedAppxKeys =
    {
        "package name", "packagename", "deployment package name",
        "display name", "displayname"
    };

    /// <summary>
    /// Parses provisioned Appx package blocks. Returns one
    /// <see cref="DiscoveredAppxPackage"/> per block that carries a non-empty
    /// deployment package name. Every discovered package is classified
    /// <see cref="RiskClass.Removable"/> (the user decides whether to select it);
    /// the engine never auto-selects.
    /// </summary>
    public static IReadOnlyList<DiscoveredAppxPackage> Parse(string output)
    {
        var result = new List<DiscoveredAppxPackage>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        string? packageName = null;
        string? displayName = null;
        string? version = null;
        string? architecture = null;
        string? publisher = null;

        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(packageName))
            {
                result.Add(new DiscoveredAppxPackage
                {
                    PackageName = packageName!,
                    DisplayName = displayName ?? packageName!,
                    Version = version,
                    Architecture = architecture,
                    Publisher = publisher,
                    Risk = RiskClass.Removable
                });
            }

            packageName = displayName = version = architecture = publisher = null;
        }

        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            // A blank line separates package blocks.
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
                case "deployment package name":
                case "package identity":
                case "package name":
                case "packagename":
                    if (string.IsNullOrEmpty(packageName))
                    {
                        packageName = value;
                    }
                    break;
                case "display name":
                case "displayname":
                    if (string.IsNullOrEmpty(displayName))
                    {
                        displayName = value;
                    }
                    break;
                case "version":
                    version = value;
                    break;
                case "architecture":
                    architecture = value;
                    break;
                case "publisher":
                    publisher = value;
                    break;
                default:
                    break;
            }
        }

        // Flush the final block.
        Flush();
        return result;
    }

    /// <summary>
    /// Determines whether <paramref name="output"/> looks like genuine DISM
    /// <c>/Get-ProvisionedAppxPackages</c> output. This is used by the discovery
    /// service to tell a legitimate "zero apps" result (DISM succeeded but the
    /// image genuinely has none) apart from an unexpected or localized response
    /// (e.g. <c>/English</c> not honoured) that must be treated as a discovery
    /// failure rather than a silent empty inventory.
    /// </summary>
    public static bool IsRecognizedOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        // The English DISM banner is always present on a successful /English run.
        if (output.Contains("Deployment Image Servicing", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var lower = output.ToLowerInvariant();
        return Array.Exists(RecognizedAppxKeys, k => lower.Contains(k));
    }
}
