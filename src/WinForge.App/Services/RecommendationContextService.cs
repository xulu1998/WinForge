using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using WinForge.Core.Models;
using WinForge.Core.Profiles;
using WinForge.Core.Services;

namespace WinForge.App.Services;

/// <summary>
/// Workflow-scoped recommendation state for the Stage 11.4 profile engine
/// (ADR-057..060). Holds: the selected profiles, the user overrides (Part K),
/// and the set of logical ids actually present in the mounted image (Part O).
///
/// <para>Persistence scope (Part Q): this is per-workflow state — it resets
/// whenever a NEW image workspace is opened, so a previous aggressive profile is
/// never silently reused on a new ISO. The default for a fresh workflow is NO
/// profile selected (pure manual mode).</para>
/// </summary>
public sealed class RecommendationContextService
{
    private readonly IRecommendationEngine _engine;
    private readonly IAppState _appState;
    private readonly List<ProfileDefinition> _profiles;
    private readonly List<string> _extraProfileIds = new();
    private readonly HashSet<string> _userOverrides = new(StringComparer.Ordinal);
    private readonly HashSet<string> _profileManaged = new(StringComparer.Ordinal);
    private HashSet<string> _presentIds = new(StringComparer.Ordinal);
    private ImageWorkspace? _lastWorkspace;
    private string? _primaryProfileId;

    public event EventHandler? Changed;

    public RecommendationContextService(
        IRecommendationEngine engine,
        IProfileCatalogProvider catalog,
        IAppState appState)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _appState = appState ?? throw new ArgumentNullException(nameof(appState));
        _profiles = (catalog ?? throw new ArgumentNullException(nameof(catalog))).GetProfiles().ToList();
        _appState.PropertyChanged += OnAppStateChanged;
    }

    /// <summary>All reviewed profiles in catalog order.</summary>
    public IReadOnlyList<ProfileDefinition> AllProfiles => _profiles;

    /// <summary>
    /// Profiles that actually drive recommendations: the ONE primary profile
    /// (radio selection, Part 1 — <c>Custom</c> excluded, it means manual mode)
    /// plus any EXTRA scenarios (independent checkboxes, Part 2). The engine
    /// keeps full multi-scenario combination internally.
    /// </summary>
    public IReadOnlyList<ProfileDefinition> SelectedProfiles =>
        _profiles.Where(p =>
            (p.Id == _primaryProfileId && p.Id != "Custom") || _extraProfileIds.Contains(p.Id)).ToList();

    /// <summary>Primary profile id (radio choice; "Custom" or null = manual mode).</summary>
    public string? PrimaryProfileId => _primaryProfileId;

    /// <summary>True when a NON-Custom primary profile is active (Custom = manual mode).</summary>
    public bool HasActiveProfiles => _primaryProfileId is not null && _primaryProfileId != "Custom";

    public bool IsProfileSelected(string profileId) =>
        profileId == _primaryProfileId || _extraProfileIds.Contains(profileId);

    public bool IsExtraSelected(string profileId) => _extraProfileIds.Contains(profileId);

    /// <summary>
    /// Radio semantics for PRIMARY profiles (Part 1): selecting one replaces the
    /// current primary. Custom is just another primary (manual mode).
    /// </summary>
    public void ToggleProfile(string profileId)
    {
        if (_profiles.FirstOrDefault(p => p.Id == profileId) is not { Kind: ProfileKind.Primary } profile)
        {
            return;
        }

        _primaryProfileId = profile.Id;
        RaiseChanged();
    }

    /// <summary>Independent secondary scenario checkbox (Part 2).</summary>
    public void ToggleExtraScenario(string profileId)
    {
        if (_profiles.FirstOrDefault(p => p.Id == profileId) is not { Kind: ProfileKind.ExtraScenario })
        {
            return;
        }

        if (_extraProfileIds.Contains(profileId))
        {
            _extraProfileIds.Remove(profileId);
        }
        else
        {
            _extraProfileIds.Add(profileId);
        }

        RaiseChanged();
    }

    public void ClearProfiles()
    {
        if (_primaryProfileId is null && _extraProfileIds.Count == 0)
        {
            return;
        }

        _primaryProfileId = null;
        _extraProfileIds.Clear();
        RaiseChanged();
    }

    // ---- Part K — user overrides ----

    /// <summary>Marks a logical id as explicitly chosen by the user (manual toggle).</summary>
    public void SetUserOverride(string logicalId)
    {
        if (_userOverrides.Add(logicalId))
        {
            RaiseChanged();
        }
    }

    public bool IsUserOverridden(string logicalId) => _userOverrides.Contains(logicalId);

    /// <summary>True when the user manually changed at least one item this session.</summary>
    public bool IsUserOverriddenAny() => _userOverrides.Count > 0;

    /// <summary>True when at least one item carries an explicit user override (drives 恢复此配置推荐).</summary>
    public bool HasUserOverrides => _userOverrides.Count > 0;

    // ---- Stage 11.4 final flow — profile-managed selections ----

    /// <summary>
    /// Logical ids whose CURRENT selection is managed by the active profile
    /// (auto-applied when the profile is selected; may be reverted/removed when
    /// the profile changes). Explicit user overrides are never in this set.
    /// </summary>
    public IReadOnlyCollection<string> ProfileManagedIds => _profileManaged;

    public bool IsProfileManaged(string logicalId) => _profileManaged.Contains(logicalId);

    /// <summary>Records which ids the active profile currently manages (after a direct-apply pass).</summary>
    public void SetProfileManaged(IEnumerable<string> ids)
    {
        _profileManaged.Clear();
        _profileManaged.UnionWith(ids);
    }

    /// <summary>
    /// Explicit "恢复此配置推荐": clears ALL user overrides so the next apply pass
    /// recalculates Profile-managed selections from the active profile. Raises
    /// <see cref="Changed"/> so the ProfileViewModel re-applies immediately.
    /// </summary>
    public void ClearUserOverrides()
    {
        if (_userOverrides.Count == 0)
        {
            return;
        }

        _userOverrides.Clear();
        RaiseChanged();
    }

    // ---- Part O — real image state ----

    /// <summary>Logical ids present in the mounted image / applicable on it (refreshed after discovery).</summary>
    public IReadOnlyCollection<string> PresentIds => _presentIds;

    public void SetPresentIds(IEnumerable<string> ids) => _presentIds = ids.ToHashSet(StringComparer.Ordinal);

    // ---- Evaluation ----

    public EffectiveRecommendation Evaluate(RecommendationInput input)
        => _engine.Evaluate(input, new RecommendationContext
        {
            SelectedProfiles = SelectedProfiles,
            UserOverrides = _userOverrides,
            PresentIds = _presentIds,
        });

    /// <summary>Part Q — a new image workspace starts a clean recommendation session.</summary>
    public void ResetForNewWorkflow()
    {
        _primaryProfileId = null;
        _extraProfileIds.Clear();
        _userOverrides.Clear();
        _profileManaged.Clear();
        _presentIds.Clear();
        RaiseChanged();
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IAppState.CurrentImageWorkspace))
        {
            var workspace = _appState.CurrentImageWorkspace;
            if (!ReferenceEquals(workspace, _lastWorkspace))
            {
                _lastWorkspace = workspace;
                ResetForNewWorkflow();
            }
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
