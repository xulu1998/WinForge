using System;
using System.Text.RegularExpressions;

namespace WinForge.Core.ComponentIntelligence;

/// <summary>
/// Semantic metadata for Windows Language capability identities
/// (Stage 14.3b, ADR-091 §3). Classification-only: it lets future profile/planning
/// logic distinguish the IMAGE DEFAULT language, user-required languages and
/// supplemental language features WITHOUT ever inferring "not zh-CN ⇒ safe
/// automatic removal". No destructive language stripping is implemented here.
///
/// Identity shape (real 25H2 zh-CN x64 media): <c>Language.&lt;Role&gt;~~~&lt;locale&gt;~0.0.1.0</c>,
/// e.g. <c>Language.Basic~~~af-ZA~0.0.1.0</c>. Roles observed in the real Unknown
/// report: Basic (123), Handwriting (89), TextToSpeech (49), OCR (35), Fonts (24),
/// Speech (17) — 337 objects total.
/// </summary>
public static class LanguageCapabilityMetadata
{
    public sealed record LanguageCapability(string Role, string? Locale);

    private static readonly Regex ParseRegex = new(
        @"^Language\.(?<role>[A-Za-z]+)(?:~~~(?<locale>[A-Za-z]{2,3}(?:-[A-Za-z]{2,8})?))?(?:~|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Parses role + locale from a language capability identity (null when not a language capability).</summary>
    public static LanguageCapability? Parse(string rawIdentity)
    {
        if (string.IsNullOrWhiteSpace(rawIdentity))
        {
            return null;
        }

        var m = ParseRegex.Match(rawIdentity.Trim());
        if (!m.Success)
        {
            return null;
        }

        var locale = m.Groups["locale"].Success && !string.IsNullOrEmpty(m.Groups["locale"].Value)
            ? m.Groups["locale"].Value
            : null;
        return new LanguageCapability(m.Groups["role"].Value, locale);
    }

    /// <summary>
    /// True when the identity's locale matches the IMAGE DEFAULT language
    /// (case-insensitive, dash normalized). The default language capability
    /// (e.g. <c>Language.Basic zh-CN</c> on a zh-CN image) should normally be
    /// retained; other locales are NOT automatically removable by this stage.
    /// </summary>
    public static bool IsTargetLocale(string rawIdentity, string? imageDefaultLanguage)
    {
        if (string.IsNullOrWhiteSpace(imageDefaultLanguage))
        {
            return false;
        }

        var parsed = Parse(rawIdentity);
        if (parsed is null || string.IsNullOrEmpty(parsed.Locale))
        {
            return false;
        }

        return string.Equals(
            parsed.Locale.Replace('_', '-'),
            imageDefaultLanguage.Trim().Replace('_', '-'),
            StringComparison.OrdinalIgnoreCase);
    }
}
