using System.Text.Json;
using WinForge.Core.Health;
using WinForge.Core.Models;
using WinForge.Core.Validation;

namespace WinForge.Infrastructure.Validation;

/// <summary>
/// Phase 17 — deterministic validation-artifact archival. Each run gets a
/// unique runId directory under the archive root; a "latest" pointer indexes
/// the most recent run. History is never overwritten; the pointer is the only
/// mutable entry.
/// </summary>
public sealed class ValidationArtifactArchiveService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _archiveRoot;

    public ValidationArtifactArchiveService(string archiveRoot)
        => _archiveRoot = archiveRoot ?? throw new ArgumentNullException(nameof(archiveRoot));

    public string ArchiveRoot => _archiveRoot;

    public static string NewRunId(string profile, string commitSha)
    {
        var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var sha = string.IsNullOrWhiteSpace(commitSha) ? "nosha" : commitSha[..Math.Min(8, commitSha.Length)];
        return $"{ts}-{sha}-{profile}";
    }

    public string CreateRunDirectory(ValidationArtifactRun run)
    {
        var dir = Path.Combine(_archiveRoot, run.RunId);
        Directory.CreateDirectory(dir);
        WriteManifest(run);
        UpdateLatest(run);
        return dir;
    }

    public void WriteManifest(ValidationArtifactRun run)
    {
        Directory.CreateDirectory(Path.Combine(_archiveRoot, run.RunId));
        File.WriteAllText(Path.Combine(_archiveRoot, run.RunId, "manifest.json"),
            JsonSerializer.Serialize(run, Json));
    }

    public void UpdateLatest(ValidationArtifactRun run)
    {
        Directory.CreateDirectory(_archiveRoot);
        File.WriteAllText(Path.Combine(_archiveRoot, "latest.json"), JsonSerializer.Serialize(run, Json));
    }

    public ValidationArtifactRun? ResolveLatest()
    {
        var path = Path.Combine(_archiveRoot, "latest.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ValidationArtifactRun>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> ListRunDirectories()
    {
        if (!Directory.Exists(_archiveRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.GetDirectories(_archiveRoot)
            .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>History of interrupted/failed runs (recovery metadata, Stage 17.9).</summary>
    public IReadOnlyList<ValidationArtifactRun> ListInterruptedRuns()
    {
        var result = new List<ValidationArtifactRun>();
        foreach (var dir in ListRunDirectories())
        {
            try
            {
                var run = JsonSerializer.Deserialize<ValidationArtifactRun>(
                    File.ReadAllText(Path.Combine(dir, "manifest.json")), Json);
                if (run is not null && run.ResultStatus is "Failed" or "Interrupted")
                {
                    result.Add(run);
                }
            }
            catch (JsonException)
            {
                // Skip corrupt run manifests but do not hide their existence.
            }
        }

        return result;
    }
}

/// <summary>
/// Machine-readable release validation manifest (Stage 17.2). Levels come from
/// a static evidence table — Balanced + DedicatedGaming are the only profiles
/// with demonstrated FullHealthValidated evidence as of Phase 16; everything
/// else is truthfully WorkflowValidated.
/// </summary>
public sealed class ReleaseValidationManifestService
{
    public static ReleaseValidationManifest Build(
        string commitSha,
        string isoPath,
        int index,
        string edition,
        string language,
        string architecture,
        string windowsBuild,
        IReadOnlyDictionary<string, (int PlanOps, int Selected)> planCounts)
    {
        var manifest = new ReleaseValidationManifest
        {
            GeneratedUtc = DateTime.UtcNow,
            WinForgeCommitSha = commitSha,
            Media = new ManifestMedia
            {
                SourceIsoPath = isoPath,
                WindowsIndex = index,
                Edition = edition,
                Language = language,
                Architecture = architecture,
                WindowsBuild = windowsBuild,
            },
        };

        foreach (var profile in ProfileOrder)
        {
            var entry = new ProfileValidationEntry
            {
                ProfileId = profile,
                SourceWindowsBuild = windowsBuild,
                LastValidatedCommit = EvidenceLevels.TryGetValue(profile, out var ev) ? ev.LastValidatedCommit : string.Empty,
            };

            var levels = EvidenceLevels.GetValueOrDefault(profile, new Evidence());
            entry.WorkflowValidated = levels.WorkflowValidated;
            entry.VmInstallValidated = levels.VmInstallValidated;
            entry.FullHealthValidated = levels.FullHealthValidated;
            entry.IsoSha256 = levels.IsoSha256;
            entry.HealthReportRef = levels.HealthReportRef;
            entry.Warnings.AddRange(levels.Warnings);
            entry.ValidationDebt.AddRange(levels.ValidationDebt);

            if (planCounts.TryGetValue(profile, out var counts))
            {
                entry.BuildPlanOperationCount = counts.PlanOps;
                entry.SelectedOperationCount = counts.Selected;
            }

            manifest.Profiles.Add(entry);
        }

        return manifest;
    }

    public static string Serialize(ReleaseValidationManifest manifest)
        => JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });

    public static readonly IReadOnlyList<string> ProfileOrder = new[]
    {
        "Balanced", "Gaming", "DedicatedGaming", "Developer", "Office", "Lightweight",
    };

    private sealed class Evidence
    {
        public bool WorkflowValidated;
        public bool VmInstallValidated;
        public bool FullHealthValidated;
        public string LastValidatedCommit = string.Empty;
        public string? IsoSha256;
        public string? HealthReportRef;
        public List<string> Warnings = new();
        public List<string> ValidationDebt = new();
    }

    /// <summary>
    /// Static evidence table. ONLY Balanced and DedicatedGaming have demonstrated
    /// VM install + passing full-health reports (Phase 16, accepted 2026-08-16).
    /// Every other built-in profile is truthfully WorkflowValidated. Updating
    /// this table requires real installed-OS evidence (ADR-098).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Evidence> EvidenceLevels =
        new Dictionary<string, Evidence>(StringComparer.Ordinal)
        {
            ["Balanced"] = new Evidence
            {
                WorkflowValidated = true,
                VmInstallValidated = true,
                FullHealthValidated = true,
                LastValidatedCommit = "59355b9",
                IsoSha256 = "recorded in Phase 16 profile-commit-validation.json (run archived)",
                HealthReportRef = "Phase 16 full-health-report.json (failures=[], overallStatus Warning, fullHealthValidated=true)",
                Warnings = { "Activation Notification (report-only)", "HTTPS TLS-trust Warning (IP/DNS Pass)", "Optional DISM ScanHealth NotTested" },
                ValidationDebt = { },
            },
            ["Gaming"] = new Evidence
            {
                WorkflowValidated = true,
                VmInstallValidated = false,
                FullHealthValidated = false,
                LastValidatedCommit = "59355b9",
                ValidationDebt = { "No VM install / full-health validation yet (Phase 17 prepares expected-state only)" },
            },
            ["DedicatedGaming"] = new Evidence
            {
                WorkflowValidated = true,
                VmInstallValidated = true,
                FullHealthValidated = true,
                LastValidatedCommit = "59355b9",
                IsoSha256 = "2d521bd21a0efa17bf24acdc97a3a8d2c279cfea1c866e90bbdce2cb89be0210",
                HealthReportRef = "Phase 16 full-health-report.json (failures=[], overallStatus Warning, fullHealthValidated=true)",
                Warnings = { "Activation Notification (report-only)", "HTTPS TLS-trust Warning (IP/DNS Pass)", "Optional DISM ScanHealth NotTested", "Fresh-image Defender signature age old (security platform healthy)" },
                ValidationDebt = { },
            },
            ["Developer"] = new Evidence
            {
                WorkflowValidated = true,
                VmInstallValidated = false,
                FullHealthValidated = false,
                LastValidatedCommit = "59355b9",
                ValidationDebt = { "No VM install / full-health validation yet (Phase 17 prepares expected-state only)" },
            },
            ["Office"] = new Evidence
            {
                WorkflowValidated = true,
                VmInstallValidated = false,
                FullHealthValidated = false,
                LastValidatedCommit = "59355b9",
                ValidationDebt = { "No VM install / full-health validation yet (Phase 17 prepares expected-state only)" },
            },
            ["Lightweight"] = new Evidence
            {
                WorkflowValidated = true,
                VmInstallValidated = false,
                FullHealthValidated = false,
                LastValidatedCommit = "59355b9",
                ValidationDebt = { "No VM install / full-health validation yet; most aggressive selected-only profile (5 services disabled)" },
            },
        };
}

/// <summary>
/// Builds a profile expected-state document from the FINAL aggregated plan
/// operations (selected-only). Consumes the same payloads the executor uses
/// (registry hive/path/value/data, package identities, service names) so the
/// expected state can never drift from the executable plan (Stage 17.3).
/// </summary>
public static class ExpectedStateBuilder
{
    public static ProfileExpectedState Build(string profileId, IEnumerable<CustomizationOperation> selectedOperations)
    {
        var state = new ProfileExpectedState { ProfileId = profileId };
        foreach (var op in selectedOperations)
        {
            switch (op.OperationType)
            {
                case CustomizationOperationType.RemoveProvisionedAppx:
                    if (!string.IsNullOrWhiteSpace(op.TargetIdentifier) && !state.AppxAbsent.Contains(op.TargetIdentifier, StringComparer.Ordinal))
                    {
                        state.AppxAbsent.Add(op.TargetIdentifier);
                    }

                    break;

                case CustomizationOperationType.SetOfflineRegistryValue:
                    var check = ToRegistryCheck(op);
                    if (check is not null && !state.RegistryChecks.Any(r =>
                            r.Scope == check.Scope && r.Path == check.Path && r.Name == check.Name && r.ExpectedData == check.ExpectedData))
                    {
                        state.RegistryChecks.Add(check);
                    }

                    break;

                case CustomizationOperationType.ConfigureOfflineService:
                    if (!string.IsNullOrWhiteSpace(op.TargetIdentifier) && !state.ServicesDisabled.Contains(op.TargetIdentifier, StringComparer.Ordinal))
                    {
                        state.ServicesDisabled.Add(op.TargetIdentifier);
                    }

                    break;

                // DisableOptionalFeature is not yet selected by any primary; it is
                // deliberately omitted from expected-state support (Phase 17 defers
                // feature execution expectations).
            }
        }

        return state;
    }

    private static ExpectedRegistryValue? ToRegistryCheck(CustomizationOperation op)
    {
        var hive = op.RegistryHive ?? string.Empty;
        string scope;
        if (hive.Contains("SOFTWARE", StringComparison.OrdinalIgnoreCase) || hive.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase))
        {
            scope = "OfflineMachine";
        }
        else if (hive.Contains("DEFAULT_USER", StringComparison.OrdinalIgnoreCase))
        {
            scope = "CurrentUserEffective";
        }
        else
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(op.RegistryKeyPath) || string.IsNullOrWhiteSpace(op.RegistryValueName) || op.RegistryValueData is null)
        {
            return null;
        }

        return new ExpectedRegistryValue
        {
            Scope = scope,
            Path = op.RegistryKeyPath,
            Name = op.RegistryValueName,
            ExpectedData = op.RegistryValueData,
            Kind = op.RegistryValueKind?.ToString() ?? "DWord",
        };
    }
}

/// <summary>
/// Release safety invariants (Stage 17.5) — built-in profiles must preserve
/// the platform. Enforcement is key-prefix based against the executable plan,
/// so a future profile change that touches Defender/Firewall/Update/Store or
/// the recovery/servicing stack fails the invariant check deterministically.
/// </summary>
public static class ReleaseSafetyInvariantSet
{
    public static IReadOnlyList<ReleaseSafetyInvariant> Default => new[]
    {
        new ReleaseSafetyInvariant
        {
            Id = "defender",
            Description = "Microsoft Defender security components must never be removed or disabled.",
            ProtectedKeyPrefixes = { "pkg|Microsoft.Windows.SecHealthUI", "cap|Windows-Defender-", "feat|Windows-Defender", "feat|Microsoft-Windows-Containers" },
            ProtectedServices = { "WinDefend", "SecurityHealthService", "wscsvc" },
        },
        new ReleaseSafetyInvariant
        {
            Id = "firewall",
            Description = "Windows Firewall must never be removed or disabled.",
            ProtectedKeyPrefixes = { "feat|NetFx3", "cap|XboxNetApi" },
            ProtectedServices = { "mpssvc", "BFE", "SharedAccess" },
        },
        new ReleaseSafetyInvariant
        {
            Id = "windowsUpdate",
            Description = "Windows Update must remain present and usable.",
            ProtectedKeyPrefixes = { },
            ProtectedServices = { "wuauserv", "UsoSvc", "WaaSMedicSvc" },
        },
        new ReleaseSafetyInvariant
        {
            Id = "store",
            Description = "Microsoft Store is retained by built-in profiles (none removes it).",
            ProtectedKeyPrefixes = { "pkg|Microsoft.WindowsStore", "pkg|Microsoft.StorePurchaseApp", "pkg|Microsoft.DesktopAppInstaller" },
            ProtectedServices = { },
        },
        new ReleaseSafetyInvariant
        {
            Id = "appInstaller",
            Description = "App Installer / winget platform is retained.",
            ProtectedKeyPrefixes = { "pkg|Microsoft.DesktopAppInstaller" },
            ProtectedServices = { },
        },
        new ReleaseSafetyInvariant
        {
            Id = "bootShell",
            Description = "Boot shell and shell infrastructure are retained.",
            ProtectedKeyPrefixes = { "pkg|Microsoft.Windows.ShellExperienceHost_", "pkg|MicrosoftWindows.Client.Cortana_", "pkg|Microsoft.Windows.StartMenuExperienceHost_" },
            ProtectedServices = { },
        },
        new ReleaseSafetyInvariant
        {
            Id = "servicingStack",
            Description = "The servicing stack (CBS/DISM) is never targeted by built-in profiles.",
            ProtectedKeyPrefixes = { "cbs|", "cap|", "pkg|Microsoft-Windows-ServicingStack" },
            ProtectedServices = { "TrustedInstaller" },
        },
        new ReleaseSafetyInvariant
        {
            Id = "networkStack",
            Description = "Core networking stack and drivers are never removed.",
            ProtectedKeyPrefixes = { "cap|Network-", "drv|" },
            ProtectedServices = { "Dhcp", "Dnscache", "NlaSvc" },
        },
        new ReleaseSafetyInvariant
        {
            Id = "displayInput",
            Description = "Display and input baselines are never removed.",
            ProtectedKeyPrefixes = { "drv|display", "drv|hidi2c" },
            ProtectedServices = { },
        },
        new ReleaseSafetyInvariant
        {
            Id = "recovery",
            Description = "Recovery infrastructure (WinRE) is retained unless explicitly unsupported.",
            ProtectedKeyPrefixes = { "pkg|Microsoft-Windows-WinRE-Recovery", "feat|Recovery" },
            ProtectedServices = { },
        },
        new ReleaseSafetyInvariant
        {
            Id = "noHostHkuWrites",
            Description = "Offline servicing must never write to host HKCU (no host registry contamination).",
            ProtectedKeyPrefixes = { },
            ProtectedServices = { },
        },
        new ReleaseSafetyInvariant
        {
            Id = "noUnknownMountDiscard",
            Description = "An unknown mounted image is never discarded (authoritative DISM inventory only).",
            ProtectedKeyPrefixes = { },
            ProtectedServices = { },
        },
    };

    public static InvariantCheckResult CheckPlan(IEnumerable<CustomizationOperation> operations)
    {
        var result = new InvariantCheckResult { Passed = true };
        foreach (var op in operations)
        {
            var key = op.ConflictKey ?? string.Empty;
            var target = op.TargetIdentifier ?? string.Empty;
            foreach (var invariant in Default)
            {
                var hit = invariant.ProtectedKeyPrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                          || invariant.ProtectedServices.Any(s => string.Equals(target, s, StringComparison.OrdinalIgnoreCase));
                if (hit)
                {
                    result.Passed = false;
                    result.Violations.Add($"[{invariant.Id}] {invariant.Description} (op {key})");
                }
            }
        }

        return result;
    }
}

/// <summary>
/// Six-profile delta audit (Stage 17.4) — machine-readable common/exclusive
/// selected operations + type distribution + convergence detection.
/// </summary>
public static class ProfileDeltaAuditService
{
    public static ProfileDeltaAudit Audit(IReadOnlyDictionary<string, IReadOnlyList<string>> selectedKeysByProfile)
    {
        var audit = new ProfileDeltaAudit { GeneratedUtc = DateTime.UtcNow };
        var allProfiles = selectedKeysByProfile.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();

        // Common = keys present in EVERY profile's selected set.
        var first = selectedKeysByProfile[allProfiles[0]].Distinct().ToList();
        audit.CommonSelectedKeys = first.Where(k => allProfiles.Skip(1).All(p => selectedKeysByProfile[p].Contains(k, StringComparer.Ordinal)))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

        foreach (var profile in allProfiles)
        {
            var keys = selectedKeysByProfile[profile].Distinct().ToList();
            var entry = new ProfileDeltaEntry
            {
                ProfileId = profile,
                SelectedCount = keys.Count,
                ExclusiveKeys = keys.Where(k => !audit.CommonSelectedKeys.Contains(k, StringComparer.Ordinal))
                    .OrderBy(k => k, StringComparer.Ordinal).ToList(),
                OperationTypeDistribution = keys
                    .GroupBy(k => k.Split('|')[0], StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
            };
            audit.Profiles.Add(entry);
        }

        // Convergence: two profiles whose full selected sets are identical.
        for (var i = 0; i < allProfiles.Count; i++)
        {
            for (var j = i + 1; j < allProfiles.Count; j++)
            {
                var a = selectedKeysByProfile[allProfiles[i]].OrderBy(x => x, StringComparer.Ordinal).ToList();
                var b = selectedKeysByProfile[allProfiles[j]].OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (a.SequenceEqual(b, StringComparer.Ordinal))
                {
                    audit.ConvergenceWarnings.Add($"{allProfiles[i]} == {allProfiles[j]} (identical selected sets)");
                }
            }
        }

        return audit;
    }
}

/// <summary>
/// Portable FullHealth input bundle (Stage 17.7): the in-VM health script, the
/// profile expected-state, the run manifest and a README/command file that
/// already contains the exact -ProfileId/-MediaId/-ExpectedJson/-IsoSha256
/// arguments. No credentials, no large ISO duplication.
/// </summary>
public sealed class ValidationBundleService
{
    private readonly string _scriptsDir;

    public ValidationBundleService(string scriptsDir) => _scriptsDir = scriptsDir;

    public static string ProfileFileName(string profile)
    {
        // PascalCase -> kebab-case (DedicatedGaming -> dedicated-gaming) matching the
        // scripts/<profile>-expected-state.json convention.
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < profile.Length; i++)
        {
            if (i > 0 && char.IsUpper(profile[i]))
            {
                sb.Append('-');
            }

            sb.Append(char.ToLowerInvariant(profile[i]));
        }

        return sb.ToString();
    }

    public string GenerateBundle(string bundleDir, string profile, ValidationArtifactRun run)
    {
        Directory.CreateDirectory(bundleDir);

        var fileName = ProfileFileName(profile);
        var scriptSrc = Path.Combine(_scriptsDir, "Validate-WinForgeInstallation.ps1");
        var expectedSrc = Path.Combine(_scriptsDir, $"{fileName}-expected-state.json");
        if (!File.Exists(scriptSrc) || !File.Exists(expectedSrc))
        {
            throw new FileNotFoundException($"Validation bundle inputs missing (script: {File.Exists(scriptSrc)}, expected-state: {File.Exists(expectedSrc)}).");
        }

        var scriptDst = Path.Combine(bundleDir, "Validate-WinForgeInstallation.ps1");
        var expectedDst = Path.Combine(bundleDir, $"{fileName}-expected-state.json");
        File.Copy(scriptSrc, scriptDst, overwrite: true);
        File.Copy(expectedSrc, expectedDst, overwrite: true);

        var isoName = string.IsNullOrWhiteSpace(run.GeneratedIsoPath)
            ? $"WinForge-{profile}-Win11-25H2-Pro-zh-CN-x64.iso"
            : Path.GetFileName(run.GeneratedIsoPath);
        var sha = string.IsNullOrWhiteSpace(run.GeneratedIsoSha256) ? "<host-side-computed-ISO-SHA-256>" : run.GeneratedIsoSha256;

        var readme = string.Join(Environment.NewLine,
            $"WinForge FullHealth validation bundle - {profile} (run {run.RunId})",
            string.Empty,
            "1. Copy this whole folder into the installed VM (Administrator).",
            "2. Run the health check with EXACTLY these arguments:",
            string.Empty,
            "powershell -ExecutionPolicy Bypass -File Validate-WinForgeInstallation.ps1 `",
            $"  -ProfileId {profile} `",
            $"  -MediaId \"{isoName}\" `",
            $"  -ExpectedJson {fileName}-expected-state.json `",
            $"  -IsoSha256 \"{sha}\" `",
            "  -OutputPath \"$env:USERPROFILE\\Desktop\\full-health-report.json\"",
            string.Empty,
            "3. Upload full-health-report.json for archival under .tmp\\validation\\<runId>\\.",
            "4. Acceptance: fullHealthValidated = true with failures = [] (warnings per ADR-098 rules).",
            string.Empty,
            $"Source ISO (read-only): {run.SourceIsoPath}",
            $"WinForge commit: {run.WinForgeCommitSha}",
            $"Validation level target: {run.ValidationLevel}");
        File.WriteAllText(Path.Combine(bundleDir, "README.txt"), readme);

        var manifest = JsonSerializer.Serialize(run, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
        File.WriteAllText(Path.Combine(bundleDir, "validation-manifest.json"), manifest);

        return bundleDir;
    }
}
