using System;
using System.Text;

namespace WinForge.Infrastructure.Health;

/// <summary>Verdict of a non-destructive SFC /verifyonly run.</summary>
public sealed class SfcVerdict
{
    public bool Pass { get; init; }
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// Decoding helpers for NATIVE (non-PowerShell) command output. Native tools such
/// as sfc.exe can emit UTF-16 text, ANSI text in the system code page, or
/// NUL-corrupted captures (a UTF-16 byte stream mis-decoded as the console code
/// page yields an ASCII character followed by a NUL for every real character).
/// The health script captures with <c>[Console]::OutputEncoding = UTF8</c> and
/// this decoder additionally strips NULs and normalizes newlines so a successful
/// run is never reported as a failure because of capture artifacts.
/// </summary>
public static class NativeOutputDecoder
{
    /// <summary>
    /// Best-effort decode of raw captured bytes. UTF-16 BOM wins; otherwise the
    /// candidate with the fewest U+FFFD replacement characters wins among
    /// strict UTF-8, UTF-16LE (a low NUL-density heuristic accepts pure-Chinese
    /// UTF-16 text, which can contain very few NUL bytes), and the system ANSI
    /// code page. Never throws; never returns NUL characters.
    /// </summary>
    public static string DecodeBestEffort(byte[] raw)
    {
        if (raw is null || raw.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
            {
                return Sanitize(Encoding.Unicode.GetString(raw, 2, raw.Length - 2));
            }

            if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
            {
                return Sanitize(Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2));
            }

            var nulCount = 0;
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i] == 0)
                {
                    nulCount++;
                }
            }

            var nulRatio = raw.Length > 0 ? (double)nulCount / raw.Length : 0.0;

            var utf16 = Encoding.Unicode.GetString(raw, 0, raw.Length - (raw.Length % 2));
            string? utf8 = null;
            try
            {
                utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(raw);
            }
            catch (DecoderFallbackException)
            {
                utf8 = null;
            }

            var ansi = Encoding.Default.GetString(raw);

            var score16 = CountReplacementChars(utf16);
            var score8 = utf8 is null ? int.MaxValue : CountReplacementChars(utf8);
            var scoreA = CountReplacementChars(ansi);

            if (score8 == 0)
            {
                return Sanitize(utf8!);
            }

            // Pure-Chinese UTF-16LE text can have an almost-zero NUL ratio, so a
            // low NUL-density signal + a clean UTF-16 decode is accepted.
            if (score16 == 0 || (nulRatio >= 0.15 && score16 <= scoreA))
            {
                return Sanitize(utf16);
            }

            if (scoreA == 0)
            {
                return Sanitize(ansi);
            }

            var best = utf16;
            var bestScore = score16;
            if (score8 < bestScore)
            {
                bestScore = score8;
                best = utf8!;
            }

            if (scoreA < bestScore)
            {
                bestScore = scoreA;
                best = ansi;
            }

            return Sanitize(best);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int CountReplacementChars(string text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (c == '\uFFFD')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Removes NUL characters and normalizes CRLF/CR to LF. Used before matching
    /// or building diagnostic detail so a NUL-corrupted capture can never corrupt
    /// a verdict or the JSON report.
    /// </summary>
    public static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\0')
            {
                continue;
            }

            if (c == '\r')
            {
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Collapses whitespace runs (incl. newlines) to single spaces for a compact diagnostic line.</summary>
    public static string Compact(string text, int maxLength = 300)
    {
        var s = Sanitize(text ?? string.Empty);
        var sb = new StringBuilder(s.Length);
        var lastWasSpace = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            sb.Append(c);
            lastWasSpace = false;
        }

        var result = sb.ToString().Trim();
        return result.Length > maxLength ? result.Substring(0, maxLength) + "…" : result;
    }
}

/// <summary>
/// Deterministic verdict for <c>sfc /verifyonly</c> (non-destructive).
///
/// SFC's exit semantics are locale-independent and authoritative across Windows
/// versions for /verifyonly: exit code 0 means "no integrity violations were
/// found". The output text is locale-dependent (English "did not find any
/// integrity violations" / Chinese "未找到任何完整性冲突" / other languages), so
/// it is used ONLY as corroborating evidence, never as the sole Pass signal, and
/// a NUL-corrupted capture can never fail a successful run.
///
/// Rule: Pass iff exitCode == 0, OR (exitCode is unavailable/ambiguous AND the
/// decoded output contains a known success marker in either locale). Everything
/// else is Fail. The sanitized + compacted output is preserved as diagnostic
/// detail either way.
/// </summary>
public static class SfcVerifyOnlyEvaluator
{
    private const string SuccessMarkerEnglish = "did not find any integrity violations";
    private const string SuccessMarkerChinese = "未找到任何完整性冲突";

    public static SfcVerdict Evaluate(int exitCode, string decodedOutput)
    {
        var clean = NativeOutputDecoder.Compact(decodedOutput);
        var pass = exitCode == 0
                   || (exitCode != 0 && ContainsSuccessMarker(clean));

        return new SfcVerdict
        {
            Pass = pass,
            Detail = pass
                ? $"sfc /verifyonly passed (exit {exitCode}): {clean}"
                : $"sfc /verifyonly FAILED (exit {exitCode}): {clean}",
        };
    }

    private static bool ContainsSuccessMarker(string text)
        => text.IndexOf(SuccessMarkerEnglish, StringComparison.OrdinalIgnoreCase) >= 0
           || text.IndexOf(SuccessMarkerChinese, StringComparison.Ordinal) >= 0;
}
