using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using WinForge.Core.Models;

namespace WinForge.Infrastructure.ComponentIntelligence;

/// <summary>
/// Parses the English output of <c>dism.exe /Get-Features</c> into
/// <see cref="RawOptionalFeature"/> items (Feature Name + State, plus restart /
/// parent metadata where DISM reports it).
/// </summary>
public static class OptionalFeatureInventoryParser
{
    private static readonly Regex KeyRegex = new(@"^([A-Za-z][A-Za-z0-9 ]*?)\s*:\s*(.*)$", RegexOptions.Compiled);

    public static IReadOnlyList<RawOptionalFeature> Parse(string output)
    {
        var result = new List<RawOptionalFeature>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        string? identity = null;
        var state = FeatureState.Unknown;
        string? parent = null;
        var restart = false;

        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(identity))
            {
                result.Add(new RawOptionalFeature
                {
                    Category = ComponentCategory.OptionalFeature,
                    RawIdentity = identity!,
                    DisplayName = identity!,
                    State = state.ToString(),
                    FeatureStateValue = state,
                    Parent = parent,
                    RestartRequired = restart
                });
            }

            identity = null;
            state = FeatureState.Unknown;
            parent = null;
            restart = false;
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
                case "feature name":
                    identity = value;
                    break;
                case "state":
                    state = ParseState(value);
                    break;
                case "parent":
                    parent = value;
                    break;
                case "restart required":
                    restart = IsYes(value);
                    break;
                default:
                    break;
            }
        }

        Flush();
        return result;
    }

    private static FeatureState ParseState(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "enabled" => FeatureState.Enabled,
            "disabled" => FeatureState.Disabled,
            _ => FeatureState.Unknown
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

        return output.ToLowerInvariant().Contains("feature name");
    }
}
