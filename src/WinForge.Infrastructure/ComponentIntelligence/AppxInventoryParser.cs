using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using WinForge.Core.Models;

namespace WinForge.Infrastructure.ComponentIntelligence;

/// <summary>
/// Parses the English (<c>/English</c>) output of
/// <c>dism.exe /Get-ProvisionedAppxPackages</c> into structured
/// <see cref="RawAppxPackage"/> items, capturing the exact removal identity
/// (<c>PackageName</c>), the friendly <c>DisplayName</c>, version, architecture,
/// publisher, resource id, and a derived package family name. Never fuzzy-matches.
/// </summary>
public static class AppxInventoryParser
{
    private static readonly Regex KeyRegex = new(@"^([A-Za-z][A-Za-z0-9 ]*?)\s*:\s*(.*)$", RegexOptions.Compiled);

    public static IReadOnlyList<RawAppxPackage> Parse(string output)
    {
        var result = new List<RawAppxPackage>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        string? packageName = null;
        string? displayName = null;
        string? version = null;
        string? architecture = null;
        string? publisher = null;
        string? resourceId = null;

        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(packageName))
            {
                result.Add(new RawAppxPackage
                {
                    Category = ComponentCategory.AppX,
                    RawIdentity = packageName!,
                    DisplayName = displayName ?? packageName!,
                    Version = version,
                    Architecture = architecture,
                    Publisher = publisher,
                    ResourceId = resourceId,
                    PackageFamilyName = DeriveFamilyName(packageName!),
                    State = "Provisioned"
                });
            }

            packageName = displayName = version = architecture = publisher = resourceId = null;
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
                case "resourceid":
                    resourceId = value;
                    break;
                default:
                    break;
            }
        }

        Flush();
        return result;
    }

    /// <summary>
    /// Derives the package family name (&lt;name&gt;_&lt;publisher-hash&gt;) from the
    /// full PackageName identity. Returns null when the identity has no publisher
    /// segment.
    /// </summary>
    private static string? DeriveFamilyName(string packageName)
    {
        var parts = packageName.Split('_');
        return parts.Length >= 2 ? parts[0] + "_" + parts[^1] : null;
    }

    public static bool IsRecognizedOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        if (output.Contains("Deployment Image Servicing", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var lower = output.ToLowerInvariant();
        return lower.Contains("package name") || lower.Contains("packagename") || lower.Contains("display name");
    }
}
