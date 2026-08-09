using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using WinForge.Core.Models;

namespace WinForge.Infrastructure.ImageMetadata;

/// <summary>
/// Parses the English (<c>/English</c>) output of
/// <c>dism.exe /Get-ImageInfo</c> into structured edition metadata.
///
/// Microsoft DISM exposes image metadata through TWO distinct read-only queries:
///
/// 1. <c>/Get-ImageInfo /ImageFile:"..."</c> (no <c>/Index</c>) — the *enumeration*
///    query. It reliably yields, per image index, only <c>Index</c>,
///    <c>Name</c>, <c>Description</c>, and <c>Size</c>. It does NOT report
///    architecture, edition id, version, installation type, or languages.
/// 2. <c>/Get-ImageInfo /ImageFile:"..." /Index:&lt;n&gt;</c> — the *detail* query.
///    For that single index it additionally returns <c>Architecture</c>,
///    <c>Version</c>, <c>Edition</c>, <c>Edition Id</c>, <c>Installation</c>,
///    <c>Languages</c>, and <c>Default Language</c>.
///
/// The two parsers below mirror that split exactly. <see cref="ParseImageList"/>
/// reads only the reliable enumeration fields; <see cref="ParseImageDetails"/>
/// reads the full detail for one index. The service merges them by index.
///
/// Design rules (see AGENTS.md and Step 2.2 spec):
/// - Only the <c>/English</c> output is parsed, never localized text.
/// - Parsing is order-insensitive within each index block and tolerant of
///   unknown / future DISM fields (they are ignored, never fatal).
/// - It never relies on fixed column positions; every value is read by key.
/// - The DISM banner <c>Version:</c> (the tool version, e.g. 10.0.26100.1) is
///   intentionally ignored — only per-index <c>Version</c> values describe the
///   Windows image.
/// - Language entries are validated with <see cref="TryNormalizeLanguageTag"/>:
///   only conservative BCP-47-like tags are accepted, so DISM footer prose such
///   as "The operation completed successfully." can never leak into the language
///   list. The language section terminates as soon as a non-language, non-blank,
///   non-key line is seen.
/// </summary>
public static class DismImageInfoParser
{
    // Matches the start of an index block, e.g. "Index : 1".
    private static readonly Regex IndexRegex = new(@"^Index\s*:\s*(\d+)", RegexOptions.Compiled);

    // Matches a "Key : Value" line. The key may contain spaces ("Edition Id").
    private static readonly Regex KeyRegex = new(@"^([A-Za-z][A-Za-z ]*?)\s*:\s*(.*)$", RegexOptions.Compiled);

    // Conservative BCP-47-like language tag: a 2-3 letter primary subtag followed
    // by at least one hyphenated subtag that is either a 2-letter region/script
    // variant, a 3-digit region, or a 4-letter script (e.g. en-US, zh-CN,
    // pt-BR, sr-Latn-RS). Requiring the hyphen + subtag structure rejects ordinary
    // prose such as "The", "Deployment", "Image", or "Version".
    private static readonly Regex LanguageTagRegex = new(
        @"^[A-Za-z]{2,3}(-([A-Za-z]{2}|[0-9]{3}|[A-Za-z]{4}))+$",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses the enumeration query output (no <c>/Index</c>). Returns one
    /// <see cref="WindowsEditionInfo"/> per image index, populated ONLY with the
    /// fields DISM reliably reports at enumeration time: <see cref="Index"/>,
    /// <see cref="Name"/>, <see cref="Description"/>. Other fields stay
    /// <c>null</c> / empty; the detail query fills them later.
    /// </summary>
    /// <returns>Indexes in the order DISM reported them. Empty when none found.</returns>
    public static IReadOnlyList<WindowsEditionInfo> ParseImageList(string output)
    {
        var editions = new List<WindowsEditionInfo>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return editions;
        }

        var lines = output.Replace("\r\n", "\n").Split('\n');
        foreach (var block in SplitIntoIndexBlocks(lines))
        {
            var edition = ParseListBlock(block);
            if (edition is not null)
            {
                editions.Add(edition);
            }
        }

        return editions;
    }

    /// <summary>
    /// Parses a single per-index detail query output (<c>/Index:&lt;n&gt;</c>).
    /// Returns the fully detailed <see cref="WindowsEditionInfo"/> for the index
    /// DISM printed, or <c>null</c> when the output contained no index. The
    /// caller is responsible for setting <see cref="WindowsEditionInfo.DetailStatus"/>.
    /// </summary>
    public static WindowsEditionInfo? ParseImageDetails(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var lines = output.Replace("\r\n", "\n").Split('\n');
        foreach (var block in SplitIntoIndexBlocks(lines))
        {
            var edition = ParseDetailBlock(block);
            if (edition is not null)
            {
                return edition;
            }
        }

        return null;
    }

    private static List<List<string>> SplitIntoIndexBlocks(string[] lines)
    {
        // Blocks must be fully collected before they are returned: a deferred
        // (yield) approach would hand the consumer an empty list and only fill it
        // in afterwards, so every index block would parse as empty.
        var blocks = new List<List<string>>();
        List<string>? current = null;
        foreach (var line in lines)
        {
            if (IndexRegex.IsMatch(line))
            {
                current = new List<string>();
                blocks.Add(current);
            }

            current?.Add(line);
        }

        return blocks;
    }

    private static WindowsEditionInfo? ParseListBlock(List<string> block)
    {
        var edition = new WindowsEditionInfo();
        var captured = false;

        foreach (var raw in block)
        {
            var km = KeyRegex.Match(raw);
            if (!km.Success)
            {
                continue;
            }

            var key = km.Groups[1].Value.Trim();
            var value = km.Groups[2].Value.Trim();

            switch (key.ToLowerInvariant())
            {
                case "index":
                    if (int.TryParse(value, out var idx))
                    {
                        edition.Index = idx;
                        captured = true;
                    }

                    break;
                case "name":
                    if (string.IsNullOrEmpty(edition.Name))
                    {
                        edition.Name = value;
                    }

                    break;
                case "description":
                    edition.Description = value;
                    break;
                // Enumeration output may also contain Size (and, on some DISM
                // builds, a few extra lines), but those are not the reliable
                // detail fields — they are intentionally ignored here and read
                // from the per-index detail query instead.
                default:
                    break;
            }
        }

        // An index block without a parseable Index is not a valid edition.
        return captured ? edition : null;
    }

    private static WindowsEditionInfo? ParseDetailBlock(List<string> block)
    {
        var edition = new WindowsEditionInfo();
        var inLanguages = false;
        var captured = false;

        foreach (var raw in block)
        {
            var line = raw;

            if (inLanguages)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue; // tolerate blank lines inside the languages block
                }

                if (IsKeyLine(line))
                {
                    // A new "Key : Value" line started — leave the language
                    // section and parse it normally below.
                    inLanguages = false;
                }
                else
                {
                    var tag = TryNormalizeLanguageTag(line.Trim());
                    if (tag is not null)
                    {
                        edition.Languages.Add(tag);
                    }
                    else
                    {
                        // Non-language prose (e.g. the DISM footer
                        // "The operation completed successfully."). Terminate the
                        // language section and ignore this and any following
                        // non-language lines; do NOT treat them as languages.
                        inLanguages = false;
                    }

                    continue;
                }
            }

            var km = KeyRegex.Match(line);
            if (!km.Success)
            {
                continue;
            }

            var key = km.Groups[1].Value.Trim();
            var value = km.Groups[2].Value.Trim();

            switch (key.ToLowerInvariant())
            {
                case "index":
                    if (int.TryParse(value, out var idx))
                    {
                        edition.Index = idx;
                        captured = true;
                    }

                    break;
                case "name":
                    if (string.IsNullOrEmpty(edition.Name))
                    {
                        edition.Name = value;
                    }

                    break;
                case "description":
                    edition.Description = value;
                    break;
                case "architecture":
                    edition.Architecture = value;
                    break;
                case "version":
                    edition.Version = value;
                    edition.Build = ExtractBuild(value);
                    break;
                case "edition":
                    // Short edition name (e.g. "Home"). Used only as a fallback
                    // when the descriptive Name is absent.
                    if (string.IsNullOrEmpty(edition.Name))
                    {
                        edition.Name = value;
                    }

                    break;
                case "edition id":
                    edition.EditionId = string.IsNullOrEmpty(value) ? null : value;
                    break;
                case "installation":
                    edition.InstallationType = value;
                    break;
                case "default language":
                    edition.DefaultLanguage = string.IsNullOrEmpty(value) ? null : value;
                    break;
                case "languages":
                    if (string.IsNullOrEmpty(value))
                    {
                        inLanguages = true;
                    }
                    else
                    {
                        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var tag = TryNormalizeLanguageTag(token.Trim());
                            if (tag is not null)
                            {
                                edition.Languages.Add(tag);
                            }
                        }
                    }

                    break;
                default:
                    // Unknown / future DISM fields are ignored on purpose.
                    break;
            }
        }

        return captured ? edition : null;
    }

    private static bool IsKeyLine(string line)
        => KeyRegex.IsMatch(line);

    /// <summary>
    /// Returns a syntactically plausible Windows / BCP-47-like language tag, or
    /// <c>null</c> when the token is not one. Real DISM language entries are
    /// <c>en-US</c>, <c>zh-CN</c>, <c>fr-CA</c>, <c>pt-BR</c>, <c>sr-Latn-RS</c>,
    /// optionally carrying a trailing annotation such as <c>(Default)</c> which
    /// is stripped before validation. Arbitrary free text (footer prose like
    /// "The operation completed successfully.", "Deployment", "Image", …) is
    /// rejected — the parser never takes a blind first word.
    /// </summary>
    private static string? TryNormalizeLanguageTag(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        // DISM may append an annotation such as "(Default)" on the default
        // language line; take the first whitespace-delimited token
        // ("en-US (Default)" -> "en-US") before validating.
        var candidate = token.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        return LanguageTagRegex.IsMatch(candidate) ? candidate : null;
    }

    private static string? ExtractBuild(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        // DISM reports "10.0.26100.1742" (or, on some images, "10.0.26100").
        // The Windows build number is the third dot segment and is genuinely
        // present; we never fabricate a servicing/UBR segment (e.g. "26100.xxxx").
        var parts = version.Split('.');
        return parts.Length >= 3 ? parts[2] : null;
    }
}
