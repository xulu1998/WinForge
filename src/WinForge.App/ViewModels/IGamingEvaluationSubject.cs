using WinForge.Core.ComponentIntelligence;
using WinForge.Core.Models;

namespace WinForge.App.ViewModels;

/// <summary>
/// Phase 14 Stage 14.3 — a recommendation subject that carries the production
/// DEEP component knowledge required by the knowledge-driven gaming pipeline
/// (ADR-088). Implemented by <see cref="ComponentKnowledgeItem"/>; optimization
/// (services/privacy/system/personalization) rows are not deep-classified and do
/// not implement this — the gaming policy targets components only.
/// </summary>
public interface IGamingEvaluationSubject
{
    /// <summary>Stable logical id (curated id or raw identity).</summary>
    string LogicalId { get; }

    /// <summary>Raw Windows identity used for traceability (representative of the row).</summary>
    string RawIdentity { get; }

    /// <summary>Discovery source (AppX / Capability / OptionalFeature / …).</summary>
    ComponentCategory SourceCategory { get; }

    /// <summary>Production deep classification of the row (null when Unknown).</summary>
    DeepComponentKnowledge? DeepKnowledge { get; }

    bool IsPresent { get; }

    /// <summary>True when an ALREADY-SUPPORTED safe action exists (ADR-086).</summary>
    bool IsApplySupported { get; }

    /// <summary>True when the user manually chose this row (manual override, Part K).</summary>
    bool WasOverridden { get; }

    /// <summary>Localized display name (for user-facing summary examples).</summary>
    string DisplayName { get; }
}
