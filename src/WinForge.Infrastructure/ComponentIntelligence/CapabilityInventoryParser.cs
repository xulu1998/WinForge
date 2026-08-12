using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using WinForge.Core.Models;

namespace WinForge.Infrastructure.ComponentIntelligence;

/// <summary>
/// Parses the English output of <c>dism.exe /Get-Capabilities</c> into
/// <see cref="RawCapability"/> items (Capability Identity + State).
/// </summary>
public static class CapabilityInventoryParser
{
    private static readonly Regex KeyRegex = new(@"^([A-Za-z][A-Za-z0-9 ]*?)\s*:\s*(.*)$", RegexOptions.Compiled);

    public static IReadOnlyList<RawCapability> Parse(string output)
    {
        var result = new List<RawCapability>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        string? identity = null;
        var state = CapabilityState.Unknown;

        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(identity))
            {
                result.Add(new RawCapability
                {
                    Category = ComponentCategory.Capability,
                    RawIdentity = identity!,
                    DisplayName = identity!,
                    State = state.ToString(),
                    CapState = state
                });
            }

            identity = null;
            state = CapabilityState.Unknown;
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
                case "capability identity":
                    identity = value;
                    break;
                case "state":
                    state = ParseState(value);
                    break;
                default:
                    break;
            }
        }

        Flush();
        return result;
    }

    private static CapabilityState ParseState(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "installed" => CapabilityState.Installed,
            "not present" => CapabilityState.NotPresent,
            _ => CapabilityState.Unknown
        };
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

        return output.ToLowerInvariant().Contains("capability identity");
    }
}
