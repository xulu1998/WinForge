using System;
using System.Collections.Generic;

namespace WinForge.Core.Compatibility;

/// <summary>
/// Classifies a Windows build number into a normalized release (Stage 13.2).
/// No single build number is the only source of truth — the classifier maps
/// build families to releases and degrades gracefully for unknown FUTURE builds
/// (Windows11_UnknownNewer → SupportedWithWarnings, never a crash).
/// </summary>
public static class WindowsReleaseClassifier
{
    // 24H2 family: 10.0.26100.x (Windows 11 24H2; also the 25H2 base on many
    // servicing tracks). 25H2 builds commonly report 26100.x via enablement or
    // 26200.x once updated. Classification is family-based, not single-build.
    private const int Build24H2Base = 26100;
    private const int Build25H2Marker = 26200;

    /// <summary>Builds at/above this are newer than the validated 24H2/25H2 family.</summary>
    private const int FutureBuildFloor = 27000;

    public static WindowsRelease Classify(int? build)
    {
        if (build is null)
        {
            return WindowsRelease.Unknown;
        }

        if (build >= FutureBuildFloor)
        {
            // Future Windows release beyond the validated matrix — degrade
            // gracefully to a warning, never block blindly (ADR-076).
            return WindowsRelease.Windows11_UnknownNewer;
        }

        if (build >= Build25H2Marker)
        {
            return WindowsRelease.Windows11_25H2;
        }

        if (build >= Build24H2Base)
        {
            // 26100.x is the 24H2/25H2 shared base; treat the 26100 family with
            // 25H2 display versions as 25H2, otherwise 24H2.
            return WindowsRelease.Windows11_24H2;
        }

        return WindowsRelease.OlderWindows;
    }

    /// <summary>Refines 26100-family builds using the DisplayVersion when present.</summary>
    public static WindowsRelease Classify(int? build, string? displayVersion)
    {
        if (build is null)
        {
            return WindowsRelease.Unknown;
        }

        if (build >= FutureBuildFloor)
        {
            return WindowsRelease.Windows11_UnknownNewer;
        }

        if (build >= Build25H2Marker)
        {
            return WindowsRelease.Windows11_25H2;
        }

        if (build == Build24H2Base && displayVersion?.StartsWith("25H2", StringComparison.OrdinalIgnoreCase) == true)
        {
            return WindowsRelease.Windows11_25H2;
        }

        if (build >= Build24H2Base)
        {
            return WindowsRelease.Windows11_24H2;
        }

        return WindowsRelease.OlderWindows;
    }
}

/// <summary>Deterministic, culture-invariant language helpers (Stage 13.4).</summary>
public static class LanguageIdentity
{
    /// <summary>Normalizes a language tag to a stable identity (en-US, zh-CN).</summary>
    public static string Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return string.Empty;
        }

        try
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(tag.Trim());
            return culture.Name;
        }
        catch (System.Globalization.CultureNotFoundException)
        {
            // Fall back to a trimmed, lower-cased stable tag (never localized text).
            return tag.Trim();
        }
    }

    public static bool IsBaseline(string? tag)
        => string.Equals(Normalize(tag), "zh-CN", StringComparison.OrdinalIgnoreCase)
           || string.Equals(Normalize(tag), "en-US", StringComparison.OrdinalIgnoreCase);
}
