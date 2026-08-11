using System;
using System.Collections.Generic;
using System.Linq;

namespace WinForge.Core.Models;

/// <summary>
/// Lifecycle of a knowledge entry. Candidates are NEVER trusted Curated entries —
/// they must pass review (and an explicit promotion) before reaching the catalog.
/// </summary>
public enum ReviewStatus
{
    Unknown = 0,

    /// <summary>Raw identity discovered in an image; not yet analyzed.</summary>
    DiscoveredRaw,

    /// <summary>A proposed definition produced by an adapter (incl. community).</summary>
    Candidate,

    /// <summary>Human-reviewed and accepted, but not yet in the shipped catalog.</summary>
    Reviewed,

    /// <summary>Shipped in the trusted curated catalog.</summary>
    Curated,

    /// <summary>Was curated but no longer recommended; excluded from current data.</summary>
    Deprecated,

    /// <summary>Present but WinForge does not service it.</summary>
    Unsupported
}

/// <summary>
/// A proposed (not yet trusted) component knowledge entry produced by the import
/// pipeline. It deliberately separates the WinForge <see cref="EffectiveRecommendation"/>
/// (set only by trusted sources) from a community's opinion
/// (<see cref="CommunityProposal"/>), so a community "remove this" list can never
/// silently become a WinForge "RecommendedRemove".
/// </summary>
public sealed class CandidateComponentDefinition
{
    public string Id { get; init; } = string.Empty;

    public ComponentCategory Category { get; init; } = ComponentCategory.Unknown;

    public string DisplayName { get; init; } = string.Empty;

    public string? ProposedPurpose { get; init; }

    public RecommendationLevel EffectiveRecommendation { get; init; } = RecommendationLevel.Unknown;

    public RiskLevel ProposedRisk { get; init; } = RiskLevel.Unknown;

    public RemovalSupport ProposedRemoval { get; init; } = RemovalSupport.Unknown;

    public RestoreSupport ProposedRestore { get; init; } = RestoreSupport.Unknown;

    public IReadOnlyList<string> ProposedKeepIf { get; init; } = new List<string>();

    public IReadOnlyList<string> ProposedRemoveIf { get; init; } = new List<string>();

    public IReadOnlyList<string> ProposedImpact { get; init; } = new List<string>();

    /// <summary>Raw Windows identities this candidate would match.</summary>
    public IReadOnlyList<TechnicalTarget> TechnicalTargets { get; init; } = new List<TechnicalTarget>();

    /// <summary>All provenance sources that contributed to this candidate.</summary>
    public IReadOnlyList<KnowledgeSource> Sources { get; init; } = new List<KnowledgeSource>();

    public ConfidenceLevel Confidence { get; init; } = ConfidenceLevel.Unknown;

    public ReviewStatus Status { get; init; } = ReviewStatus.Candidate;

    /// <summary>
    /// A community project's removal opinion, recorded for review only. It is
    /// informational and is NEVER promoted to <see cref="EffectiveRecommendation"/>.
    /// </summary>
    public RecommendationLevel CommunityProposal { get; init; } = RecommendationLevel.Unknown;

    /// <summary>True when the only contributor was a community adapter.</summary>
    public bool IsCommunityOnly =>
        Sources.Count > 0 && Sources.All(s => s.SourceType == KnowledgeSourceType.CommunityProject);

    /// <summary>True when a trusted (non-community) source asserts a recommendation.</summary>
    public bool HasTrustedRecommendation =>
        Sources.Any(s => s.SourceType != KnowledgeSourceType.CommunityProject) &&
        EffectiveRecommendation != RecommendationLevel.Unknown;

    public CandidateComponentDefinition()
    {
    }

    private CandidateComponentDefinition(Builder b)
    {
        Id = b.Id;
        Category = b.Category;
        DisplayName = b.DisplayName;
        ProposedPurpose = b.ProposedPurpose;
        EffectiveRecommendation = b.EffectiveRecommendation;
        ProposedRisk = b.ProposedRisk;
        ProposedRemoval = b.ProposedRemoval;
        ProposedRestore = b.ProposedRestore;
        ProposedKeepIf = b.ProposedKeepIf;
        ProposedRemoveIf = b.ProposedRemoveIf;
        ProposedImpact = b.ProposedImpact;
        TechnicalTargets = b.TechnicalTargets;
        Sources = b.Sources;
        Confidence = b.Confidence;
        Status = b.Status;
        CommunityProposal = b.CommunityProposal;
    }

    /// <summary>Internal mutable builder used by the pipeline merge step.</summary>
    internal sealed class Builder
    {
        public string Id = string.Empty;
        public ComponentCategory Category = ComponentCategory.Unknown;
        public string DisplayName = string.Empty;
        public string? ProposedPurpose;
        public RecommendationLevel EffectiveRecommendation = RecommendationLevel.Unknown;
        public RiskLevel ProposedRisk = RiskLevel.Unknown;
        public RemovalSupport ProposedRemoval = RemovalSupport.Unknown;
        public RestoreSupport ProposedRestore = RestoreSupport.Unknown;
        public List<string> ProposedKeepIf = new();
        public List<string> ProposedRemoveIf = new();
        public List<string> ProposedImpact = new();
        public List<TechnicalTarget> TechnicalTargets = new();
        public List<KnowledgeSource> Sources = new();
        public ConfidenceLevel Confidence = ConfidenceLevel.Unknown;
        public ReviewStatus Status = ReviewStatus.Candidate;
        public RecommendationLevel CommunityProposal = RecommendationLevel.Unknown;

        public CandidateComponentDefinition Build() => new(this);
    }
}

/// <summary>A source adapter that turns a structured local snapshot into candidate definitions.</summary>
public interface IKnowledgeSourceAdapter
{
    KnowledgeSourceType SourceType { get; }

    IReadOnlyList<CandidateComponentDefinition> Produce();
}

/// <summary>Microsoft official identifiers / servicing documentation.</summary>
public sealed class MicrosoftOfficialAdapter : IKnowledgeSourceAdapter
{
    private readonly IReadOnlyList<(string Id, string DisplayName, ComponentCategory Category, string Fact, string? Ref)> _facts;

    public MicrosoftOfficialAdapter(IReadOnlyList<(string, string, ComponentCategory, string, string?)> facts)
        => _facts = facts;

    public KnowledgeSourceType SourceType => KnowledgeSourceType.MicrosoftOfficial;

    public IReadOnlyList<CandidateComponentDefinition> Produce()
    {
        var src = new KnowledgeSource(KnowledgeSourceType.MicrosoftOfficial, "Microsoft Learn / servicing docs",
            ConfidenceLevel.High);
        var list = new List<CandidateComponentDefinition>();
        foreach (var f in _facts)
        {
            list.Add(new CandidateComponentDefinition
            {
                Id = f.Id,
                DisplayName = f.DisplayName,
                Category = f.Category,
                ProposedPurpose = f.Fact,
                EffectiveRecommendation = RecommendationLevel.Unknown,
                Sources = new[] { src },
                Confidence = ConfidenceLevel.High,
                Status = ReviewStatus.Candidate,
            });
        }

        return list;
    }
}

/// <summary>Real WinForge Windows-image discovery output.</summary>
public sealed class WindowsImageDiscoveryAdapter : IKnowledgeSourceAdapter
{
    private readonly ComponentInventory _inventory;

    public WindowsImageDiscoveryAdapter(ComponentInventory inventory) => _inventory = inventory;

    public KnowledgeSourceType SourceType => KnowledgeSourceType.WindowsImageDiscovery;

    public IReadOnlyList<CandidateComponentDefinition> Produce()
    {
        var src = new KnowledgeSource(KnowledgeSourceType.WindowsImageDiscovery, "Windows image discovery",
            ConfidenceLevel.Medium);
        var byId = new Dictionary<string, CandidateComponentDefinition.Builder>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _inventory.Entries)
        {
            // Only unclassified/protected/unsupported raw items become discovery candidates.
            if (entry.Definition is not null)
            {
                continue;
            }

            foreach (var raw in entry.RawItems)
            {
                var id = "raw:" + raw.RawIdentity;
                if (!byId.TryGetValue(id, out var b))
                {
                    b = new CandidateComponentDefinition.Builder
                    {
                        Id = id,
                        DisplayName = raw.DisplayName ?? raw.RawIdentity,
                        Category = raw.Category,
                        Status = ReviewStatus.DiscoveredRaw,
                        Confidence = ConfidenceLevel.Medium,
                    };
                    b.Sources.Add(src);
                    byId[id] = b;
                }

                b.TechnicalTargets.Add(new TechnicalTarget
                {
                    Category = raw.Category,
                    Match = MatchMethod.Exact,
                    Pattern = raw.RawIdentity
                });
            }
        }

        return byId.Values.Select(b => b.Build()).ToList();
    }
}

/// <summary>High-quality community project lists (e.g. Win11Debloat).</summary>
public sealed class Win11DebloatCommunityAdapter : IKnowledgeSourceAdapter
{
    // (rawIdentity, category, communityRemovalOpinion)
    private readonly IReadOnlyList<(string Identity, ComponentCategory Category, RecommendationLevel Opinion)> _items;

    public Win11DebloatCommunityAdapter(
        IReadOnlyList<(string, ComponentCategory, RecommendationLevel)> items) => _items = items;

    public KnowledgeSourceType SourceType => KnowledgeSourceType.CommunityProject;

    public IReadOnlyList<CandidateComponentDefinition> Produce()
    {
        var src = new KnowledgeSource(KnowledgeSourceType.CommunityProject, "Win11Debloat (community)",
            ConfidenceLevel.Low);
        var list = new List<CandidateComponentDefinition>();
        foreach (var it in _items)
        {
            // CRITICAL: the community's removal opinion is recorded as CommunityProposal
            // ONLY. It is never promoted to EffectiveRecommendation (which stays Unknown)
            // so a community "remove" list cannot manufacture a WinForge "RecommendedRemove".
            list.Add(new CandidateComponentDefinition
            {
                Id = "raw:" + it.Identity,
                DisplayName = it.Identity,
                Category = it.Category,
                CommunityProposal = it.Opinion,
                EffectiveRecommendation = RecommendationLevel.Unknown,
                TechnicalTargets = new[]
                {
                    new TechnicalTarget { Category = it.Category, Match = MatchMethod.Prefix, Pattern = it.Identity }
                },
                Sources = new[] { src },
                Confidence = ConfidenceLevel.Low,
                Status = ReviewStatus.Candidate,
            });
        }

        return list;
    }
}

/// <summary>Manually curated WinForge corrections / shipped catalog.</summary>
public sealed class WinForgeCuratedAdapter : IKnowledgeSourceAdapter
{
    private readonly IReadOnlyList<ComponentDefinition> _definitions;

    public WinForgeCuratedAdapter(IReadOnlyList<ComponentDefinition> definitions) => _definitions = definitions;

    public KnowledgeSourceType SourceType => KnowledgeSourceType.WinForgeCurated;

    public IReadOnlyList<CandidateComponentDefinition> Produce()
    {
        var src = new KnowledgeSource(KnowledgeSourceType.WinForgeCurated, "WinForge curated catalog",
            ConfidenceLevel.Verified);
        var list = new List<CandidateComponentDefinition>();
        foreach (var def in _definitions)
        {
            list.Add(new CandidateComponentDefinition
            {
                Id = def.Id,
                DisplayName = def.DisplayNameKey,
                Category = def.Category,
                ProposedPurpose = def.ShortDescriptionKey,
                EffectiveRecommendation = def.Recommendation,
                ProposedRisk = def.Risk,
                ProposedRemoval = def.Removal,
                ProposedRestore = def.Restore,
                ProposedKeepIf = def.KeepIf,
                ProposedRemoveIf = def.RemoveIf,
                ProposedImpact = def.KnownImpact,
                TechnicalTargets = def.TechnicalTargets,
                Sources = new[] { src },
                Confidence = ConfidenceLevel.Verified,
                Status = ReviewStatus.Curated,
            });
        }

        return list;
    }
}

/// <summary>
/// Developer-facing, offline knowledge import pipeline. It runs a set of source
/// adapters, merges candidates by id, de-duplicates technical targets and provenance
/// sources, and exposes the merged candidates. Candidates NEVER automatically become
/// trusted Curated entries — promotion is an explicit, reviewed action.
/// </summary>
public sealed class KnowledgeImportPipeline
{
    private readonly List<IKnowledgeSourceAdapter> _adapters = new();

    public void AddAdapter(IKnowledgeSourceAdapter adapter) => _adapters.Add(adapter);

    /// <summary>
    /// Runs all adapters and merges their candidates by <see cref="CandidateComponentDefinition.Id"/>.
    /// Merging combines technical targets and provenance sources WITHOUT duplication, and raises the
    /// review status to the most trusted contributor (Curated &gt; Reviewed &gt; Candidate &gt; DiscoveredRaw).
    /// A community-only candidate never receives a trusted recommendation.
    /// </summary>
    public IReadOnlyList<CandidateComponentDefinition> Run()
    {
        var byId = new Dictionary<string, CandidateComponentDefinition.Builder>(StringComparer.OrdinalIgnoreCase);

        foreach (var adapter in _adapters)
        {
            foreach (var c in adapter.Produce())
            {
                if (!byId.TryGetValue(c.Id, out var b))
                {
                    b = new CandidateComponentDefinition.Builder
                    {
                        Id = c.Id,
                        DisplayName = c.DisplayName,
                        Category = c.Category,
                        ProposedPurpose = c.ProposedPurpose,
                        EffectiveRecommendation = c.EffectiveRecommendation,
                        ProposedRisk = c.ProposedRisk,
                        ProposedRemoval = c.ProposedRemoval,
                        ProposedRestore = c.ProposedRestore,
                        CommunityProposal = c.CommunityProposal,
                        Status = c.Status,
                        Confidence = c.Confidence,
                    };
                    byId[c.Id] = b;
                }
                else
                {
                    // Merge scalar fields: prefer a trusted (non-community) value when present.
                    if (c.EffectiveRecommendation != RecommendationLevel.Unknown)
                    {
                        b.EffectiveRecommendation = c.EffectiveRecommendation;
                    }

                    if (c.CommunityProposal != RecommendationLevel.Unknown)
                    {
                        b.CommunityProposal = c.CommunityProposal;
                    }

                    if (c.ProposedPurpose is not null)
                    {
                        b.ProposedPurpose ??= c.ProposedPurpose;
                    }

                    if (c.ProposedRisk != RiskLevel.Unknown)
                    {
                        b.ProposedRisk = c.ProposedRisk;
                    }

                    if (c.ProposedRemoval != RemovalSupport.Unknown)
                    {
                        b.ProposedRemoval = c.ProposedRemoval;
                    }

                    if (c.ProposedRestore != RestoreSupport.Unknown)
                    {
                        b.ProposedRestore = c.ProposedRestore;
                    }

                    // Raise review status to the most trusted contributor.
                    b.Status = RaiseStatus(b.Status, c.Status);
                    b.Confidence = RaiseConfidence(b.Confidence, c.Confidence);
                }

                // Merge technical targets + sources WITHOUT duplication.
                foreach (var t in c.TechnicalTargets)
                {
                    if (!b.TechnicalTargets.Any(x => x.Category == t.Category &&
                        string.Equals(x.Pattern, t.Pattern, StringComparison.OrdinalIgnoreCase)))
                    {
                        b.TechnicalTargets.Add(t);
                    }
                }

                foreach (var s in c.Sources)
                {
                    if (!b.Sources.Any(x => x.SourceType == s.SourceType &&
                        string.Equals(x.SourceName, s.SourceName, StringComparison.OrdinalIgnoreCase)))
                    {
                        b.Sources.Add(s);
                    }
                }
            }
        }

        return byId.Values.Select(b => b.Build()).ToList();
    }

    /// <summary>Current (shippable) candidates — excludes Deprecated ones.</summary>
    public static IReadOnlyList<CandidateComponentDefinition> GetCurrent(
        IReadOnlyList<CandidateComponentDefinition> candidates) =>
        candidates.Where(c => c.Status != ReviewStatus.Deprecated).ToList();

    /// <summary>
    /// Explicit, reviewed promotion of a candidate to a trusted <see cref="ComponentDefinition"/>.
    /// Returns null unless the candidate has actually been reviewed (Reviewed/Curated) AND a
    /// trusted recommendation is established — community-only candidates are refused.
    /// </summary>
    public static ComponentDefinition? PromoteToCurated(CandidateComponentDefinition candidate)
    {
        if (candidate.Status != ReviewStatus.Curated && candidate.Status != ReviewStatus.Reviewed)
        {
            return null;
        }

        if (candidate.IsCommunityOnly || !candidate.HasTrustedRecommendation)
        {
            return null;
        }

        return new ComponentDefinition
        {
            Id = candidate.Id,
            Category = candidate.Category,
            DisplayNameKey = candidate.DisplayName,
            ShortDescriptionKey = candidate.ProposedPurpose ?? string.Empty,
            Recommendation = candidate.EffectiveRecommendation,
            Risk = candidate.ProposedRisk,
            Removal = candidate.ProposedRemoval,
            Restore = candidate.ProposedRestore,
            KeepIf = candidate.ProposedKeepIf,
            RemoveIf = candidate.ProposedRemoveIf,
            KnownImpact = candidate.ProposedImpact,
            TechnicalTargets = candidate.TechnicalTargets,
        };
    }

    private static ReviewStatus RaiseStatus(ReviewStatus a, ReviewStatus b)
    {
        // Trust order: Curated > Reviewed > Candidate > DiscoveredRaw > Unsupported > Deprecated.
        int Rank(ReviewStatus s) => s switch
        {
            ReviewStatus.Curated => 5,
            ReviewStatus.Reviewed => 4,
            ReviewStatus.Candidate => 3,
            ReviewStatus.DiscoveredRaw => 2,
            ReviewStatus.Unsupported => 1,
            _ => 0,
        };
        return Rank(b) > Rank(a) ? b : a;
    }

    private static ConfidenceLevel RaiseConfidence(ConfidenceLevel a, ConfidenceLevel b)
    {
        int Rank(ConfidenceLevel c) => (int)c;
        return Rank(b) > Rank(a) ? b : a;
    }
}
