using System;
using System.Collections.Generic;
using System.Linq;
using WinForge.Core.Models;

namespace WinForge.Core.Profiles;

// =====================================================================
// Phase 15 Stage 15.3 — OPTIMIZATION DEFINITION VALIDATOR (ADR-096 §5)
//
// Reusable, deterministic validation of executable optimization definitions.
// Detects (fail fast, BEFORE the user reaches Apply):
//   MissingTechnicalTarget / MissingRegistryTarget / MissingServiceName /
//   MissingFeatureName / UnsupportedExecution / InvalidValue /
//   DuplicateCanonicalIdentity
//
// The validator is deliberately STRICT — the plan validator stays the final
// authority and is never weakened. These checks simply surface malformed
// definitions during catalog construction / tests / plan generation instead
// of only when a plan fails to validate.
// =====================================================================

public static class OptimizationDefinitionValidator
{
    /// <summary>Validates one definition; returns issue strings (empty = valid).</summary>
    public static IReadOnlyList<string> ValidateDefinition(OptimizationDefinition d)
    {
        var issues = new List<string>();
        if (d is null)
        {
            return new[] { "Null optimization definition." };
        }

        if (string.IsNullOrWhiteSpace(d.Id))
        {
            issues.Add("Missing Id.");
        }

        switch (d.Mechanism)
        {
            case OptimizationMechanism.ServiceStartup:
                if (string.IsNullOrWhiteSpace(d.ServiceName))
                {
                    issues.Add("MissingServiceName: service startup definition has no ServiceName.");
                }

                // A NeverRemove service (informational, e.g. RpcSs) can never become
                // a change — it does not need an allowlisted configurable identity
                // or a proposed start type.
                var changeEligible = d.Recommendation != RecommendationLevel.NeverRemove;
                if (changeEligible && !ServiceConfigPolicy.IsConfigurable(d.ServiceName))
                {
                    issues.Add($"UnsupportedExecution: service '{d.ServiceName}' is not on the trusted allowlist (ADR-030).");
                }

                if (changeEligible && d.ProposedStartType is null)
                {
                    issues.Add($"MissingServiceName: service '{d.Id}' is change-eligible but has no ProposedStartType.");
                }

                break;

            case OptimizationMechanism.DisableOptionalFeature:
                if (string.IsNullOrWhiteSpace(d.TargetIdentifier))
                {
                    issues.Add("MissingFeatureName: optional-feature definition has no TargetIdentifier.");
                }

                break;

            case OptimizationMechanism.RemoveProvisionedAppx:
            case OptimizationMechanism.RemoveCapability:
            case OptimizationMechanism.RemovePackage:
                if (string.IsNullOrWhiteSpace(d.TargetIdentifier))
                {
                    issues.Add("MissingTechnicalTarget: removal definition has no TargetIdentifier.");
                }

                break;

            default:
                // Registry-backed mechanisms (PrivacyPolicy / RegistryPolicy /
                // ExplorerPreference / StartPreference / TaskbarPreference /
                // VisualPreference / SystemPolicy / OfflineRegistry).
                if (d.RegistryTargets.Count == 0)
                {
                    issues.Add($"MissingRegistryTarget: '{d.Id}' ({d.Mechanism}) has no RegistryTarget.");
                }
                else
                {
                    var index = 0;
                    foreach (var t in d.RegistryTargets)
                    {
                        if (string.IsNullOrWhiteSpace(t.Hive))
                        {
                            issues.Add($"InvalidValue: '{d.Id}' registry target #{index} has no Hive.");
                        }

                        if (string.IsNullOrWhiteSpace(t.KeyPath))
                        {
                            issues.Add($"InvalidValue: '{d.Id}' registry target #{index} has no KeyPath.");
                        }

                        if (string.IsNullOrWhiteSpace(t.ValueName))
                        {
                            issues.Add($"InvalidValue: '{d.Id}' registry target #{index} has no ValueName.");
                        }

                        if (string.IsNullOrWhiteSpace(t.RecommendedData))
                        {
                            issues.Add($"InvalidValue: '{d.Id}' registry target #{index} has no RecommendedData.");
                        }
                        else if (!IsDataValidForKind(t.ValueKind, t.RecommendedData))
                        {
                            issues.Add($"InvalidValue: '{d.Id}' registry target #{index} data '{t.RecommendedData}' is not valid for {t.ValueKind}.");
                        }

                        index++;
                    }
                }

                break;
        }

        // Unsupported execution: the definition's concrete operation type must
        // have a supported/conditional path (KNOWN != REMOVABLE, ADR-086).
        var opType = ProfilePlanSubject.OperationTypeForOptimization(d);
        if (!ExecutionSupportMatrix.IsExecutable(opType))
        {
            issues.Add($"UnsupportedExecution: '{d.Id}' maps to {opType} which has no supported execution path.");
        }

        return issues;
    }

    /// <summary>Validates every definition in a catalog; returns all issues.</summary>
    public static IReadOnlyList<string> ValidateCatalog(IReadOnlyList<OptimizationDefinition> definitions)
    {
        var issues = new List<string>();
        // Duplicate detection is scoped to NON-MERGEABLE identities — service
        // names / feature names / package targets, where two definitions colliding
        // is a genuine identity conflict. Two registry definitions MAY legally
        // target the same value (e.g. Privacy SpotlightFeatures + Personalization
        // DisableSpotlight both set CloudContent\DisableWindowsSpotlightFeatures):
        // the plan layer merges identical registry mutations with provenance
        // retained (Phase 12, ADR-096 §12) — that is dedup, not a defect.
        var nonMergeable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in definitions ?? Array.Empty<OptimizationDefinition>())
        {
            issues.AddRange(ValidateDefinition(d).Select(i => $"'{d.Id}': {i}"));

            if (IsNonMergeableIdentity(d, out var key) && !nonMergeable.Add(key))
            {
                issues.Add($"DuplicateCanonicalIdentity: '{d.Id}' collides on canonical key '{key}'.");
            }
        }

        return issues;
    }

    private static bool IsNonMergeableIdentity(OptimizationDefinition d, out string key)
    {
        key = string.Empty;
        if (d.Mechanism == OptimizationMechanism.ServiceStartup && !string.IsNullOrWhiteSpace(d.ServiceName))
        {
            key = "svc|" + d.ServiceName;
            return true;
        }

        if (d.Tab == OptimizationTab.WindowsComponents && !string.IsNullOrWhiteSpace(d.TargetIdentifier))
        {
            key = "feat|" + d.TargetIdentifier;
            return true;
        }

        if (d.Tab == OptimizationTab.Apps && !string.IsNullOrWhiteSpace(d.TargetIdentifier))
        {
            key = "appx|" + d.TargetIdentifier;
            return true;
        }

        return false;
    }

    /// <summary>True when the recommended data is parseable for the value kind.</summary>
    public static bool IsDataValidForKind(OfflineRegistryValueKind kind, string data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return false;
        }

        var raw = data.Trim();
        return kind switch
        {
            OfflineRegistryValueKind.DWord or OfflineRegistryValueKind.QWord =>
                long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out _)
                || long.TryParse(raw.AsSpan().StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? raw[2..] : raw,
                    System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out _),
            OfflineRegistryValueKind.String or OfflineRegistryValueKind.ExpandString
                or OfflineRegistryValueKind.MultiString => true,
            _ => false,
        };
    }
}
