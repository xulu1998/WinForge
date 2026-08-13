using System.Collections.Generic;
using WinForge.Core.Models;

namespace WinForge.Core.Compatibility;

/// <summary>
/// Evaluates an inspected ISO into a compatibility profile (Phase 13). The
/// implementation is platform-agnostic (Core): it consumes the read-only ISO
/// inspection result + WIM/ESD metadata and applies the Stage 13.1/13.10 rules.
/// </summary>
public interface IImageCompatibilityService
{
    /// <summary>Evaluate a completed (or failed) ISO inspection.</summary>
    ImageCompatibilityProfile Evaluate(IsoInspectionResult inspection);
}

/// <summary>
/// Core rule engine behind <see cref="IImageCompatibilityService"/> — pure logic,
/// unit-testable without media (Stage 13.22 fixtures feed it directly).
/// </summary>
public sealed class CompatibilityRuleEngine
{
    /// <summary>Build the profile + findings for an inspection result.</summary>
    public ImageCompatibilityProfile Evaluate(IsoInspectionResult inspection)
    {
        if (inspection is null || inspection.Status != IsoInspectionStatus.Completed)
        {
            return ImageCompatibilityProfile.UnknownResult();
        }

        var meta = inspection.ImageMetadata;
        var editions = meta?.Editions ?? new List<WindowsEditionInfo>();
        var first = editions.Count > 0 ? editions[0] : null;
        var agreedBuild = ParseBuild(meta?.Version);
        // Editions expose the raw build NUMBER (e.g. "26100"), not a full version.
        var allBuilds = editions
            .Select(e => ParseEditionBuild(e.Build))
            .Where(b => b is not null)
            .ToList();
        var displayVersion = editions.Select(e => e.DisplayVersion).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var profile = new ImageCompatibilityProfile
        {
            IsoPath = inspection.IsoPath,
            Architecture = meta?.Architecture ?? first?.Architecture,
            ProductName = editions.Select(e => e.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
            EditionId = editions.Select(e => e.EditionId).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)),
            InstallationType = first?.InstallationType,
            Version = meta?.Version,
            Build = allBuilds.Count > 0 ? allBuilds.Max() : agreedBuild,
            Ubr = ParseUbr(meta?.Version),
            DisplayVersion = displayVersion,
            ReleaseId = displayVersion,
            DefaultLanguage = first?.DefaultLanguage,
            AvailableLanguages = meta?.Languages ?? new List<string>(),
            ImageFormat = inspection.HasSplitSwm ? ImageFormatKind.Swm
                : inspection.InstallImageType switch
                {
                    InstallImageType.Wim => ImageFormatKind.Wim,
                    InstallImageType.Esd => ImageFormatKind.Esd,
                    _ => ImageFormatKind.Unknown,
                },
            ImageCount = editions.Count,
            SelectedIndex = inspection.SelectedIndex,
            MediaType = ClassifyMedia(inspection),
            HasBootWim = inspection.HasBootWim,
            HasInstallImage = inspection.HasInstallWim || inspection.HasInstallEsd || inspection.HasSplitSwm,
            HasRecoveryEnvironment = inspection.HasRecoveryEnvironment,
            HasSplitSwm = inspection.HasSplitSwm,
            SwmPartCount = inspection.SwmPartCount,
            Release = WindowsReleaseClassifier.Classify(
                allBuilds.Count > 0 ? allBuilds.Max() : agreedBuild, displayVersion),
            SourceFingerprint = BuildFingerprint(inspection, meta),
        };

        profile.Findings = BuildFindings(profile, inspection);
        profile.Status = ClassifyStatus(profile);
        profile.Findings = profile.Findings.OrderBySeverity();
        return profile;
    }

    private static List<CompatibilityFinding> BuildFindings(ImageCompatibilityProfile p, IsoInspectionResult inspection)
    {
        var findings = new List<CompatibilityFinding>();

        // --- Image format (13.5 / 13.8) ---
        if (!inspection.HasInstallWim && !inspection.HasInstallEsd && !inspection.HasSplitSwm)
        {
            findings.Add(new CompatibilityFinding
            {
                Key = "Compat.NoInstallImage",
                Severity = CompatibilitySeverity.Blocking,
                Category = CompatibilityCategory.ImageFormat,
                Message = "No usable install image (install.wim/install.esd) found.",
            });
        }
        else if (inspection.HasSplitSwm)
        {
            findings.Add(new CompatibilityFinding
            {
                Key = "Compat.SwmReadOnly",
                Severity = CompatibilitySeverity.Warning,
                Category = CompatibilityCategory.ImageFormat,
                Message = "Split WIM (install.swm) detected — read-only inspection only; offline servicing requires a unified WIM.",
            });
        }
        else if (inspection.InstallImageType == InstallImageType.Esd)
        {
            findings.Add(new CompatibilityFinding
            {
                Key = "Compat.EsdServicing",
                Severity = CompatibilitySeverity.Warning,
                Category = CompatibilityCategory.ImageFormat,
                Message = "ESD image detected — it is inspected read-only; servicing converts the selected index to a working WIM.",
            });
        }

        if (!inspection.HasBootWim)
        {
            findings.Add(new CompatibilityFinding
            {
                Key = "Compat.NoBootWim",
                Severity = CompatibilitySeverity.Blocking,
                Category = CompatibilityCategory.MediaStructure,
                Message = "boot.wim is missing from the media layout.",
            });
        }

        // --- Architecture (13.10 blocking) ---
        var arch = p.Architecture;
        if (string.IsNullOrWhiteSpace(arch))
        {
            findings.Add(new CompatibilityFinding
            {
                Key = "Compat.UnknownArch",
                Severity = CompatibilitySeverity.Warning,
                Category = CompatibilityCategory.Architecture,
                Message = "Architecture could not be determined.",
            });
        }
        else if (!arch.Contains("x64", System.StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new CompatibilityFinding
            {
                Key = "Compat.UnsupportedArch",
                Severity = CompatibilitySeverity.Blocking,
                Category = CompatibilityCategory.Architecture,
                Message = $"Architecture '{arch}' is not supported by the current pipeline (x64 required).",
            });
        }

        // --- Release (13.2 / 13.21) ---
        if (p.Release == WindowsRelease.Unknown)
        {
            findings.Add(new CompatibilityFinding
            {
                Key = "Compat.UnknownRelease",
                Severity = CompatibilitySeverity.Warning,
                Category = CompatibilityCategory.Release,
                Message = "Windows release could not be determined from the image.",
            });
        }
        else if (p.Release == WindowsRelease.OlderWindows)
        {
            findings.Add(new CompatibilityFinding
            {
                Key = "Compat.OlderWindows",
                Severity = CompatibilitySeverity.Warning,
                Category = CompatibilityCategory.Release,
                Message = "This image predates the validated Windows 11 targets; proceed conservatively.",
            });
        }
        else if (p.Release == WindowsRelease.Windows11_UnknownNewer)
        {
            findings.Add(new CompatibilityFinding
            {
                Key = "Compat.FutureBuild",
                Severity = CompatibilitySeverity.Warning,
                Category = CompatibilityCategory.Release,
                Message = "This Windows version is newer than the validated compatibility matrix — proceed with a conservative configuration.",
            });
        }

        // --- Edition (13.3) ---
        if (p.EditionId is not null && !EditionCompatibilityCatalog.IsKnown(p.EditionId))
        {
            findings.Add(new CompatibilityFinding
            {
                Key = "Compat.UnknownEdition",
                Severity = CompatibilitySeverity.Warning,
                Category = CompatibilityCategory.Edition,
                Message = $"Edition '{p.EditionId}' is not yet in the validated matrix.",
            });
        }

        // --- Language (13.4) ---
        var defaultLang = p.DefaultLanguage ?? p.AvailableLanguages.FirstOrDefault();
        if (defaultLang is not null && !LanguageIdentity.IsBaseline(defaultLang))
        {
            findings.Add(new CompatibilityFinding
            {
                Key = "Compat.NonBaselineLanguage",
                Severity = CompatibilitySeverity.Info,
                Category = CompatibilityCategory.Language,
                Message = $"Primary language '{LanguageIdentity.Normalize(defaultLang)}' is outside the baseline set (zh-CN/en-US) — display strings stay neutral; matching uses stable identities.",
            });
        }

        // --- Media classification (13.7) ---
        if (p.MediaType == MediaClassification.ModifiedMedia)
        {
            findings.Add(new CompatibilityFinding
            {
                Key = "Compat.ModifiedMedia",
                Severity = CompatibilitySeverity.Warning,
                Category = CompatibilityCategory.MediaStructure,
                Message = "Media structure deviates from a standard Windows installation layout (possibly modified/repacked).",
            });
        }

        // --- Edition capability gate (13.3): any edition-gated definition is
        // surfaced here only as Info; per-definition badges gate in Customize. ---
        if (p.EditionId is not null)
        {
            var cap = EditionCompatibilityCatalog.For(p.EditionId);
            if (cap is null)
            {
                findings.Add(new CompatibilityFinding
                {
                    Key = "Compat.EditionCapabilityUnknown",
                    Severity = CompatibilitySeverity.Info,
                    Category = CompatibilityCategory.Edition,
                    Message = $"Capability facts for '{p.EditionId}' are unknown; edition-gated recommendations will be suppressed.",
                });
            }
        }

        return findings;
    }

    private static CompatibilityStatus ClassifyStatus(ImageCompatibilityProfile p)
    {
        if (p.HasBlockers)
        {
            return CompatibilityStatus.Unsupported;
        }

        if (p.Release is WindowsRelease.Windows11_24H2 or WindowsRelease.Windows11_25H2
            && p.MediaType == MediaClassification.MicrosoftOfficialLike)
        {
            return p.HasWarnings ? CompatibilityStatus.SupportedWithWarnings : CompatibilityStatus.Supported;
        }

        return p.HasWarnings ? CompatibilityStatus.SupportedWithWarnings : CompatibilityStatus.PartiallySupported;
    }

    private static MediaClassification ClassifyMedia(IsoInspectionResult inspection)
    {
        if (inspection.HasBootDirectory && inspection.HasSourcesDirectory
            && (inspection.HasBootWim || inspection.HasInstallWim || inspection.HasInstallEsd))
        {
            return MediaClassification.MicrosoftOfficialLike;
        }

        if (!inspection.HasBootDirectory || !inspection.HasSourcesDirectory)
        {
            return MediaClassification.ModifiedMedia;
        }

        return MediaClassification.Unknown;
    }

    private static int? ParseBuild(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        // "10.0.26100.1742" → 26100
        var parts = version.Split('.');
        if (parts.Length >= 3 && int.TryParse(parts[2], out var b))
        {
            return b;
        }

        return null;
    }

    /// <summary>Parses a raw build number string ("26100") into an int.</summary>
    private static int? ParseEditionBuild(string? build)
        => int.TryParse(build?.Trim(), out var b) ? b : null;

    private static int? ParseUbr(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var parts = version.Split('.');
        if (parts.Length >= 4 && int.TryParse(parts[3], out var ubr))
        {
            return ubr;
        }

        return null;
    }

    private static string BuildFingerprint(IsoInspectionResult inspection, WindowsImageMetadataResult? meta)
        => $"{inspection.IsoPath}|{meta?.Version}|{meta?.Architecture}|{meta?.Languages?.Count ?? 0}langs|{meta?.Editions?.Count ?? 0}idx";
}

internal static class FindingSortExtensions
{
    public static List<CompatibilityFinding> OrderBySeverity(this List<CompatibilityFinding> findings)
        => findings
            .OrderByDescending(f => f.Severity) // Blocking(3) → Warning(2) → Info(1)
            .ThenBy(f => f.Key)
            .ToList();
}
