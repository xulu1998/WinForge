using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using WinForge.Core.Models;

namespace WinForge.Infrastructure.ComponentIntelligence;

/// <summary>
/// Parses the English output of <c>dism.exe /Get-Packages</c> into
/// <see cref="RawCbsPackage"/> items, capturing Package Identity, State, Release
/// Type, Install Time, and the Permanent flag where DISM reports them.
/// </summary>
public static class CbsPackageInventoryParser
{
    private static readonly Regex KeyRegex = new(@"^([A-Za-z][A-Za-z0-9 ]*?)\s*:\s*(.*)$", RegexOptions.Compiled);

    public static IReadOnlyList<RawCbsPackage> Parse(string output)
    {
        var result = new List<RawCbsPackage>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        string? identity = null;
        var state = CbsPackageState.Unknown;
        string? releaseType = null;
        string? installTime = null;
        var permanent = false;

        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(identity))
            {
                result.Add(new RawCbsPackage
                {
                    Category = ComponentCategory.CbsPackage,
                    RawIdentity = identity!,
                    DisplayName = identity!,
                    State = state.ToString(),
                    PkgState = state,
                    ReleaseType = releaseType,
                    InstallTime = installTime,
                    Permanent = permanent
                });
            }

            identity = null;
            state = CbsPackageState.Unknown;
            releaseType = null;
            installTime = null;
            permanent = false;
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
                case "package name":
                case "packagename":
                    identity = value;
                    break;
                case "state":
                    state = ParseState(value);
                    break;
                case "release type":
                    releaseType = value;
                    break;
                case "install time":
                    installTime = value;
                    break;
                case "permanent":
                    permanent = IsYes(value);
                    break;
                default:
                    break;
            }
        }

        Flush();
        return result;
    }

    private static CbsPackageState ParseState(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "installed" => CbsPackageState.Installed,
            "staged" => CbsPackageState.Staged,
            "superseded" => CbsPackageState.Superseded,
            "partially installed" => CbsPackageState.PartiallyInstalled,
            _ => CbsPackageState.Unknown
        };
    }

    internal static bool IsYes(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        return v == "yes" || v == "true" || v == "1";
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

        return output.ToLowerInvariant().Contains("package identity");
    }
}
