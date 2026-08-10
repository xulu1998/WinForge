using System.ComponentModel;
using System.Runtime.CompilerServices;
using WinForge.Core.Models;

namespace WinForge.Core.Services;

/// <summary>
/// Default in-memory implementation of <see cref="IAppState"/>.
/// </summary>
public sealed class AppState : IAppState
{
    private string? _sourceImagePath;
    private WindowsEditionInfo? _selectedEdition;
    private ImageWorkspace? _currentImageWorkspace;
    private ImageServicingWorkspace? _currentServicingWorkspace;
    private CustomizationPlan? _currentCustomizationPlan;
    private CustomizationExecutionState _customizationExecutionState = CustomizationExecutionState.Idle;
    private DiscoveryInventory? _discoveredInventory;
    private BuildState _buildStatus = BuildState.NotStarted;
    private readonly BuildPlan _configuration = new();

    public string? SourceImagePath
    {
        get => _sourceImagePath;
        set => SetField(ref _sourceImagePath, value);
    }

    public WindowsEditionInfo? SelectedEdition
    {
        get => _selectedEdition;
        set => SetField(ref _selectedEdition, value);
    }

    public ImageWorkspace? CurrentImageWorkspace
    {
        get => _currentImageWorkspace;
        set => SetField(ref _currentImageWorkspace, value);
    }

    public ImageServicingWorkspace? CurrentServicingWorkspace
    {
        get => _currentServicingWorkspace;
        set
        {
            // The servicing layer mutates ImageServicingWorkspace.State IN PLACE and
            // returns the SAME reference (see ImageServicingService.MountAsync, which
            // sets workspace.State = Mounted and then returns that very instance). We
            // surface that transition through ImageServicingWorkspace.INotifyPropertyChanged:
            // the in-place State mutation raises PropertyChanged, which OnNestedServicingChanged
            // forwards to IAppState listeners as CurrentServicingWorkspace.
            //
            // A same-reference reassignment therefore needs NO extra notification. Raising
            // one here would be a redundant, synthetic event: if any downstream consumer
            // reacted to CurrentServicingWorkspace by reassigning the same workspace back,
            // it would create a notification feedback loop. (That loop was the real-desktop
            // 0xc00000fd crash class when entering Customize.) We only fire when the reference
            // actually changes, and (re)subscribe when it does.
            if (ReferenceEquals(_currentServicingWorkspace, value))
            {
                return;
            }

            if (_currentServicingWorkspace is INotifyPropertyChanged oldNested)
            {
                oldNested.PropertyChanged -= OnNestedServicingChanged;
            }

            _currentServicingWorkspace = value;

            if (value is INotifyPropertyChanged newNested)
            {
                newNested.PropertyChanged += OnNestedServicingChanged;
            }

            OnPropertyChanged(nameof(CurrentServicingWorkspace));
        }
    }

    public CustomizationPlan? CurrentCustomizationPlan
    {
        get => _currentCustomizationPlan;
        set
        {
            // The customization plan is mutated IN PLACE by the tab view models
            // (see PlanSync.Toggle), so a same-reference reassignment needs NO
            // synthetic notification — but a reference change must (re)subscribe so
            // the in-place selection/validation/status edits surface as a
            // CurrentCustomizationPlan change. A synthetic event on same-reference
            // would risk a notification feedback loop (the 0xc00000fd class when
            // entering Customize), so we only fire when the reference actually moves.
            if (ReferenceEquals(_currentCustomizationPlan, value))
            {
                return;
            }

            if (_currentCustomizationPlan is INotifyPropertyChanged oldPlan)
            {
                oldPlan.PropertyChanged -= OnNestedPlanChanged;
            }

            _currentCustomizationPlan = value;

            if (value is INotifyPropertyChanged newPlan)
            {
                newPlan.PropertyChanged += OnNestedPlanChanged;
            }

            OnPropertyChanged(nameof(CurrentCustomizationPlan));
        }
    }

    public CustomizationExecutionState CustomizationExecutionState
    {
        get => _customizationExecutionState;
        set => SetField(ref _customizationExecutionState, value);
    }

    public DiscoveryInventory? DiscoveredInventory
    {
        get => _discoveredInventory;
        set => SetField(ref _discoveredInventory, value);
    }

    public BuildPlan Configuration => _configuration;

    public string ConfigurationLabel => "Default";

    public BuildState BuildStatus
    {
        get => _buildStatus;
        set => SetField(ref _buildStatus, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// Forwards nested <see cref="ImageServicingWorkspace"/> changes (notably
    /// <see cref="ImageServicingWorkspace.State"/>) to <see cref="IAppState"/>
    /// listeners. The workflow coordinator derives step availability from State, so
    /// an in-place State mutation must surface as a CurrentServicingWorkspace change.
    /// </summary>
    private void OnNestedServicingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(ImageServicingWorkspace.State))
        {
            OnPropertyChanged(nameof(CurrentServicingWorkspace));
        }
    }

    /// <summary>
    /// Forwards nested <see cref="CustomizationPlan"/> changes (selection toggles,
    /// validation, status) to <see cref="IAppState"/> listeners as a
    /// CurrentCustomizationPlan change. The workflow coordinator derives Review/Next
    /// availability from the plan's selected-operation count, so an in-place edit
    /// must surface here or the gating would stay frozen. Convergence is guaranteed:
    /// no handler reassigns CurrentCustomizationPlan in response to a selection
    /// notification, so the forward cannot re-enter the setter and loop.
    /// </summary>
    private void OnNestedPlanChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CurrentCustomizationPlan));
    }
}
