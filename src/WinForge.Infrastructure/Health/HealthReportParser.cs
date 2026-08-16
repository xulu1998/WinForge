using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WinForge.Core.Health;

namespace WinForge.Infrastructure.Health;

/// <summary>Result of parsing + validating a full-health report JSON.</summary>
public sealed class HealthReportParseResult
{
    public bool SchemaValid { get; init; }
    public FullHealthReport? Report { get; init; }
    public List<string> Errors { get; init; } = new();
}

/// <summary>
/// Parses and validates <c>full-health-report.json</c> (produced by
/// <c>scripts/Validate-WinForgeInstallation.ps1</c> inside the installed VM) and
/// re-derives the aggregated status. The aggregation is authoritative on the
/// host side: the script's own <c>overallStatus</c> is ignored and recomputed so
/// a hand-edited or buggy script can never report a false Pass.
/// </summary>
public static class HealthReportParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Parses the JSON into the typed report and validates the schema.</summary>
    public static HealthReportParseResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new HealthReportParseResult { SchemaValid = false, Errors = { "Report JSON is empty." } };
        }

        // PowerShell 5.1 Set-Content -Encoding UTF8 writes a BOM — tolerate it.
        json = json.TrimStart('\uFEFF');

        Dictionary<string, HealthSectionJson>? sections;
        ReportEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ReportEnvelope>(json, Options);
            sections = envelope?.Sections;
        }
        catch (JsonException ex)
        {
            return new HealthReportParseResult { SchemaValid = false, Errors = { $"Report is not valid JSON: {ex.Message}" } };
        }

        if (sections is null)
        {
            return new HealthReportParseResult { SchemaValid = false, Errors = { "Report has no sections object." } };
        }

        var errors = new List<string>();
        foreach (var required in FullHealthReport.RequiredSections)
        {
            if (!sections.ContainsKey(required))
            {
                errors.Add($"Required section '{required}' is missing.");
            }
        }

        var report = new FullHealthReport { RawJson = json };
        foreach (var (name, section) in sections.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var target = SectionTarget(report, name);
            if (target is null)
            {
                continue; // unknown extra section — ignored, not an error
            }

            if (!Enum.TryParse<HealthStatus>(section.Status, ignoreCase: true, out var status))
            {
                errors.Add($"Section '{name}' has invalid status '{section.Status}' (expected Pass|Warning|Fail|NotTested).");
                continue;
            }

            target.Status = status;
            target.Checks.Clear();
            foreach (var check in section.Checks ?? new List<HealthCheckItemJson>())
            {
                if (!Enum.TryParse<HealthStatus>(check.Status, ignoreCase: true, out var checkStatus))
                {
                    errors.Add($"Section '{name}' check '{check.Name}' has invalid status '{check.Status}'.");
                    continue;
                }

                target.Checks.Add(new HealthCheckItem
                {
                    Name = check.Name ?? string.Empty,
                    Status = checkStatus,
                    Detail = check.Detail ?? string.Empty,
                });
            }
        }

        var schemaValid = errors.Count == 0;
        var aggregated = schemaValid ? Aggregate(report) : report;
        if (envelope?.Warnings is { Count: > 0 })
        {
            aggregated.Warnings = envelope.Warnings;
        }

        if (envelope?.Failures is { Count: > 0 })
        {
            aggregated.Failures = envelope.Failures;
        }

        if (schemaValid)
        {
            aggregated.FullHealthValidated = EvaluateFullHealth(aggregated);
        }
        else
        {
            aggregated.FullHealthValidated = false;
        }

        return new HealthReportParseResult { SchemaValid = schemaValid, Report = aggregated, Errors = errors };
    }

    /// <summary>
    /// Aggregates per-section and overall status with precedence
    /// Fail &gt; Warning &gt; NotTested &gt; Pass, and derives the warnings /
    /// failures lists from the section checks.
    /// </summary>
    public static FullHealthReport Aggregate(FullHealthReport report)
    {
        var sections = AllSections(report);
        foreach (var section in sections)
        {
            var checks = section.Checks.Where(c => c.Status != HealthStatus.Pass).ToList();
            if (checks.Count == 0 && section.Status == HealthStatus.NotTested)
            {
                continue;
            }

            var derived = checks.Count > 0 ? Worst(checks.Select(c => c.Status).Append(section.Status)) : section.Status;
            section.Status = derived;
        }

        report.OverallStatus = Worst(sections.Select(s => s.Status));

        var failures = new List<string>();
        var warnings = new List<string>();
        foreach (var section in sections)
        {
            var name = NameOf(report, section);
            foreach (var check in section.Checks)
            {
                switch (check.Status)
                {
                    case HealthStatus.Fail:
                        failures.Add($"{name}: {check.Name} — {check.Detail}");
                        break;
                    case HealthStatus.Warning:
                        warnings.Add($"{name}: {check.Name} — {check.Detail}");
                        break;
                }
            }

            // Section-level status with no failing check detail still surfaces.
            if (section.Status == HealthStatus.Fail && !section.Checks.Any(c => c.Status == HealthStatus.Fail))
            {
                failures.Add($"{name}: section status is Fail (no individual failing check recorded).");
            }
            else if (section.Status == HealthStatus.Warning && !section.Checks.Any(c => c.Status == HealthStatus.Warning))
            {
                warnings.Add($"{name}: section status is Warning (no individual warning check recorded).");
            }
        }

        report.Failures = failures;
        report.Warnings = warnings;
        return report;
    }

    /// <summary>
    /// ADR-084 FullHealthValidated gate (Stage 16.1 §19): the report must be
    /// schema-valid, no section may Fail, the critical sections (bootAndShell,
    /// servicing, security, network) must be actually Pass (not NotTested), and
    /// the overall status must be Pass. Warnings do NOT block validation.
    /// </summary>
    public static bool EvaluateFullHealth(FullHealthReport report)
    {
        if (report is null)
        {
            return false;
        }

        var sections = AllSections(report);
        if (sections.Any(s => s.Status == HealthStatus.Fail))
        {
            return false;
        }

        foreach (var critical in FullHealthReport.CriticalSections)
        {
            var section = SectionTarget(report, critical);
            if (section is null)
            {
                return false;
            }

            // Critical sections (bootAndShell, servicing, security, network) must
            // be ACTUALLY TESTED — an untested critical section never validates.
            if (section.Status == HealthStatus.NotTested)
            {
                return false;
            }

            // A failing check inside a critical section always blocks.
            if (section.Checks.Any(c => c.Status == HealthStatus.Fail))
            {
                return false;
            }
        }

        // Warnings — including inside a critical section, e.g. the HTTPS trust
        // Warning on a VM whose IP/DNS fundamentals Pass — do NOT block
        // FullHealthValidated (ADR-098: only Fail checks and untested critical
        // sections block; warnings are honest evidence, never a false Pass).
        return true;
    }

    private static HealthStatus Worst(IEnumerable<HealthStatus> statuses)
    {
        var worst = HealthStatus.Pass;
        foreach (var s in statuses)
        {
            if ((int)s > (int)worst)
            {
                worst = s;
            }
        }

        return worst;
    }

    private static List<HealthSection> AllSections(FullHealthReport report) => new()
    {
        report.Media, report.Profile, report.WindowsIdentity, report.BootAndShell, report.Devices,
        report.Network, report.Servicing, report.WindowsUpdate, report.Security,
        report.StoreAndAppPlatform, report.ProfileExpectedChanges,
    };

    private static HealthSection? SectionTarget(FullHealthReport report, string name) => name switch
    {
        "media" => report.Media,
        "profile" => report.Profile,
        "windowsIdentity" => report.WindowsIdentity,
        "bootAndShell" => report.BootAndShell,
        "devices" => report.Devices,
        "network" => report.Network,
        "servicing" => report.Servicing,
        "windowsUpdate" => report.WindowsUpdate,
        "security" => report.Security,
        "storeAndAppPlatform" => report.StoreAndAppPlatform,
        "profileExpectedChanges" => report.ProfileExpectedChanges,
        _ => null,
    };

    private static string NameOf(FullHealthReport report, HealthSection section)
    {
        if (ReferenceEquals(report.Media, section)) return "media";
        if (ReferenceEquals(report.Profile, section)) return "profile";
        if (ReferenceEquals(report.WindowsIdentity, section)) return "windowsIdentity";
        if (ReferenceEquals(report.BootAndShell, section)) return "bootAndShell";
        if (ReferenceEquals(report.Devices, section)) return "devices";
        if (ReferenceEquals(report.Network, section)) return "network";
        if (ReferenceEquals(report.Servicing, section)) return "servicing";
        if (ReferenceEquals(report.WindowsUpdate, section)) return "windowsUpdate";
        if (ReferenceEquals(report.Security, section)) return "security";
        if (ReferenceEquals(report.StoreAndAppPlatform, section)) return "storeAndAppPlatform";
        return "profileExpectedChanges";
    }

    private sealed class ReportEnvelope
    {
        public Dictionary<string, HealthSectionJson>? Sections { get; set; }
        public List<string>? Warnings { get; set; }
        public List<string>? Failures { get; set; }
        public string? OverallStatus { get; set; }
    }

    private sealed class HealthSectionJson
    {
        public string? Status { get; set; }
        public List<HealthCheckItemJson>? Checks { get; set; }
    }

    private sealed class HealthCheckItemJson
    {
        public string? Name { get; set; }
        public string? Status { get; set; }
        public string? Detail { get; set; }
    }
}

/// <summary>Parses a profile expected-state JSON into the typed model (null when malformed).</summary>
public static class ProfileExpectedStateParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static ProfileExpectedState? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        json = json.TrimStart('\uFEFF');
        ProfileExpectedState? state;
        try
        {
            state = JsonSerializer.Deserialize<ProfileExpectedState>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (state is null)
        {
            return null;
        }

        // Scope must be EXPLICIT on every registry check — nothing is silently
        // reinterpreted (Stage 16.1a). A missing or unknown scope rejects the file.
        foreach (var check in state.RegistryChecks)
        {
            if (!Enum.TryParse<RegistryCheckScope>(check.Scope, ignoreCase: true, out _))
            {
                return null;
            }
        }

        return state;
    }
}
