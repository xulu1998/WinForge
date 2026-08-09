using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using WinForge.Core.Models;

namespace WinForge.Infrastructure.ImageMetadata;

/// <summary>
/// Parses the English (<c>/English</c>) output of <c>dism.exe /Get-WimInfo</c>
/// into a structured <see cref="WindowsImageMetadataResult"/>.
///
/// Design rules (see AGENTS.md and Step 2.2 spec):
/// - Only the <c>/English</c> output is parsed, never localized text.
/// - Parsing is order-insensitive within each index block and tolerant of
///   unknown / future DISM fields (they are ignored, never fatal).
/// - It never relies on fixed column positions; every value is read by key.
/// - Empty or index-less output is reported as a failed result, not an exception.
///
/// The DISM banner line <c>Version:</c> (the tool version, e.g. 10.0.26100.1) is
/// intentionally ignored — only per-index <c>Version</c> values describe the
/// Windows image.
/// </summary>
public static class DismWimInfoParser
{
    // Matches the start of an index block, e.g. "Index : 1".
    private static readonly Regex IndexRegex = new(@"^Index\s*:\s*(\d+)", RegexOptions.Compiled);

    // Matches a "Key : Value" line. The key may contain spaces ("Edition Id").
    private static readonly Regex KeyRegex = new(@"^([A-Za-z][A-Za-z ]*?)\s*:\s*(.*)$", RegexOptions.Compiled);

    public static WindowsImageMetadataResult Parse(string output, string imagePath, WindowsImageType imageType)
    {
        var result = new WindowsImageMetadataResult
        {
            ImagePath = imagePath,
            ImageType = imageType,
            Editions = new List<WindowsEditionInfo>()
        };

        if (string.IsNullOrWhiteSpace(output))
        {
            result.Status = WindowsImageMetadataStatus.Failed;
            result.ErrorMessage = "The image returned no readable information.";
            return result;
        }

        var lines = output.Replace("\r\n", "\n").Split('\n');
        foreach (var block in SplitIntoIndexBlocks(lines))
        {
            var edition = ParseEditionBlock(block);
            if (edition is not null)
            {
                result.Editions.Add(edition);
            }
        }

        if (result.Editions.Count == 0)
        {
            result.Status = WindowsImageMetadataStatus.Failed;
            result.ErrorMessage = "No image indexes were found in the source.";
            return result;
        }

        ComputeTopLevel(result);
        result.Status = WindowsImageMetadataStatus.Completed;
        return result;
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

    private static WindowsEditionInfo? ParseEditionBlock(List<string> block)
    {
        var edition = new WindowsEditionInfo();
        var inLanguages = false;

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
                    inLanguages = false; // a new key started; parse it below
                }
                else
                {
                    var lang = ExtractLanguage(line.Trim());
                    if (!string.IsNullOrEmpty(lang))
                    {
                        edition.Languages.Add(lang);
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
                case "languages":
                    if (string.IsNullOrEmpty(value))
                    {
                        inLanguages = true;
                    }
                    else
                    {
                        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var lang = ExtractLanguage(token.Trim());
                            if (!string.IsNullOrEmpty(lang))
                            {
                                edition.Languages.Add(lang);
                            }
                        }
                    }

                    break;
                default:
                    // Unknown / future DISM fields are ignored on purpose.
                    break;
            }
        }

        return edition;
    }

    private static bool IsKeyLine(string line)
        => KeyRegex.IsMatch(line);

    private static string? ExtractLanguage(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        // "en-US (Default)" -> "en-US"
        var lang = token.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        return string.IsNullOrEmpty(lang) ? null : lang;
    }

    private static string? ExtractBuild(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var parts = version.Split('.');
        return parts.Length >= 3 ? parts[2] : null;
    }

    private static void ComputeTopLevel(WindowsImageMetadataResult result)
    {
        result.Architecture = Consistent(result.Editions, e => e.Architecture);
        result.Version = Consistent(result.Editions, e => e.Version);
        result.Build = Consistent(result.Editions, e => e.Build);
        result.Languages = ConsistentLanguages(result.Editions);
    }

    private static string? Consistent(List<WindowsEditionInfo> editions, Func<WindowsEditionInfo, string?> selector)
    {
        var distinct = editions
            .Select(selector)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .Distinct()
            .ToList();

        return distinct.Count == 1 ? distinct[0] : null;
    }

    private static List<string>? ConsistentLanguages(List<WindowsEditionInfo> editions)
    {
        var lists = editions
            .Select(e => e.Languages)
            .Where(l => l.Count > 0)
            .ToList();

        // If any edition reports no languages, we cannot assert a consistent set.
        if (lists.Count != editions.Count)
        {
            return null;
        }

        var first = lists[0];
        for (var i = 1; i < lists.Count; i++)
        {
            if (!first.SequenceEqual(lists[i]))
            {
                return null;
            }
        }

        return first.ToList();
    }
}
