using System;
using System.Text;
using System.Text.RegularExpressions;

namespace WinForge.Core.ComponentIntelligence;

/// <summary>
/// Normalizes Windows component identifiers so families can be classified
/// without hundreds of duplicate catalog entries. Strips versions, language
/// suffixes, architecture tokens, package tokens, publisher ids, and dotted
/// family variants. Deliberately conservative: unrelated packages must never
/// collide (ADR-085).
/// </summary>
public static class ComponentNormalizer
{
    private static readonly Regex StripTokens = new(
        @"(?:~[\w\-\.]*|~+|_[A-Za-z0-9][\w\-\.]*|\b(?:10\.0|8\.0|6\.3|6\.2|6\.1|5\.1)\.[\d\.]+|\.(?:neutral|processorarchitecture))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Collapse = new(
        @"\s+",
        RegexOptions.Compiled);

    /// <summary>
    /// Deterministic canonical key for a raw identity (never localized, never
    /// display text). Lowercase, token-stripped, whitespace-collapsed.
    /// </summary>
    public static string Canonical(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            return string.Empty;
        }

        var s = identity.Trim();
        s = StripTokens.Replace(s, " ");
        s = Collapse.Replace(s, " ").Trim();
        return s.ToLowerInvariant();
    }

    /// <summary>
    /// Normalized match key for family patterns (same stripping as
    /// <see cref="Canonical"/> plus trailing-dot trim so "Microsoft-Windows-
    /// Printing-*" matches "Microsoft-Windows-Printing" and "-XPSServices").
    /// </summary>
    public static string NormalizePattern(string pattern)
        => Canonical(pattern.TrimEnd('*', '.', ' '));

    /// <summary>True when two canonical keys collide (deterministic equality).</summary>
    public static bool Collides(string a, string b)
        => string.Equals(Canonical(a), Canonical(b), StringComparison.Ordinal);

    /// <summary>
    /// Guards a catalog against over-aggressive normalization: two DIFFERENT
    /// identifiers that canonicalize to the SAME key are a collision. Returns
    /// the colliding pair when found.
    /// </summary>
    public static (string A, string B)? FindCollision(IReadOnlyCollection<string> identities)
    {
        var seen = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in identities)
        {
            var key = Canonical(id);
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            if (seen.TryGetValue(key, out var prev) && !string.Equals(prev, id, StringComparison.Ordinal))
            {
                return (prev, id);
            }

            seen[key] = id;
        }

        return null;
    }
}
