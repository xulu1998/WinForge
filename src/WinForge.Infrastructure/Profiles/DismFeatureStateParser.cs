using System;
using System.Text.RegularExpressions;

namespace WinForge.Infrastructure.Profiles;

/// <summary>
/// Parses the English (<c>/English</c>) output of
/// <c>dism.exe /Get-FeatureInfo /FeatureName:…</c> into the exact feature state
/// string (e.g. <c>Disabled</c>, <c>DisabledWithPayloadRemoved</c>,
/// <c>EnablePending</c>, <c>Enabled</c>).
///
/// <para>The <c>State</c> line is matched tolerantly ("State : Disabled"). When
/// the output cannot be parsed the caller decides the classification (a feature
/// that no longer exists after disable makes DISM return an error — the verifier
/// treats that absence itself as evidence).</para>
/// </summary>
public static class DismFeatureStateParser
{
    private static readonly Regex StateRegex = new(
        @"^State\s*:\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns the exact <c>State</c> value from a <c>/Get-FeatureInfo</c> block,
    /// or <c>"Unknown"</c> when no state line is present.
    /// </summary>
    public static string ParseState(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "Unknown";
        }

        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            var match = StateRegex.Match(trimmed);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim();
            }
        }

        return "Unknown";
    }
}
