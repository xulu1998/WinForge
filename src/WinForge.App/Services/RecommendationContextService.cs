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
    private readonly List<string> _selectedProfileIds = new();
    private readonly HashSet<string> _userOverrides = new(StringComparer.Ordinal);
    private HashSet<string> _presentIds = new(StringComparer.Ordinal);
    private ImageWorkspace? _lastWorkspace;

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
    /// Profiles that actually drive recommendations. <c>Custom</c> is EXCLUDED —
    /// it means "no profile-driven overrides": the engine falls back to catalog
    /// defaults while explicit manual checkbox selections are preserved.
    /// </summary>
    public IReadOnlyList<ProfileDefinition> SelectedProfiles =>
        _profiles.Where(p => _selectedProfileIds.Contains(p.Id) && p.Id != "Custom").ToList();

    public IReadOnlyList<string> SelectedProfileIds => _selectedProfileIds;

    /// <summary>True when at least one NON-Custom profile is active (Custom = manual mode).</summary>
    public bool HasActiveProfiles => _selectedProfileIds.Any(id => id != "Custom");

    public bool IsProfileSelected(string profileId) => _selectedProfileIds.Contains(profileId);

    /// <summary>
    /// Toggles a profile. The <c>Custom</c> profile is exclusive: selecting it
    /// clears every preset; selecting any preset clears Custom (Part B/D).
    /// </summary>
    public void ToggleProfile(string profileId)
    {
        var profile = _profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null)
        {
            return;
        }

        if (profileId == "Custom")
        {
            _selectedProfileIds.Clear();
            _selectedProfileIds.Add("Custom");
        }
        else if (_selectedProfileIds.Contains(profileId))
        {
            _selectedProfileIds.Remove(profileId);
        }
        else
        {
            _selectedProfileIds.Remove("Custom");
            _selectedProfileIds.Add(profileId);
        }

        RaiseChanged();
    }

    public void ClearProfiles()
    {
        if (_selectedProfileIds.Count == 0)
        {
            return;
        }

        _selectedProfileIds.Clear();
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
        _selectedProfileIds.Clear();
        _userOverrides.Clear();
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
