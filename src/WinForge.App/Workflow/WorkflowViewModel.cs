using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using WinForge.App.Mvvm;
using WinForge.App.ViewModels;
using WinForge.Core.Models;
using WinForge.Core.Services;
using WinForge.Core.WorkspaceLifecycle;

namespace WinForge.App.Workflow;

/// <summary>
/// The sequential workflow coordinator. It owns no DISM or servicing logic; it
/// only derives each step's availability from shared <see cref="IAppState"/> and
/// moves the active step. Back/Next navigation and direct-step jumps are guarded
/// so a step can never be entered before its prerequisites are met, and a source
/// change while not executing invalidates any assembled plan + discovery (they
/// target the previous image).
/// </summary>
public sealed class WorkflowViewModel : ViewModelBase, IWorkflowNavigator
{
    private readonly IAppState _appState;
    private readonly BuildStepViewModel _build;
    private readonly INavigationService? _navigation;
    private readonly IWorkspaceLifecycleManager? _lifecycle;
    private readonly List<WorkflowStepViewModel> _steps = new();
    private int _currentIndex;

    public WorkflowViewModel(
        IAppState appState,
        ImageViewModel image,
        CustomizeStepViewModel customize,
        PlanReviewViewModel plan,
        BuildStepViewModel build,
        INavigationService? navigation = null,
        IWorkspaceLifecycleManager? lifecycle = null)
    {
        _appState = appState;
        _build = build;
        _navigation = navigation;
        _lifecycle = lifecycle;

        _steps.Add(new WorkflowStepViewModel(WorkflowStep.Source, "Step.Source.Title", "Step.Source.Description", image, 0));
        _steps.Add(new WorkflowStepViewModel(WorkflowStep.Prepare, "Step.Prepare.Title", "Step.Prepare.Description", image, 1));
        _steps.Add(new WorkflowStepViewModel(WorkflowStep.Customize, "Step.Customize.Title", "Step.Customize.Description", customize, 2));
        _steps.Add(new WorkflowStepViewModel(WorkflowStep.Review, "Step.Review.Title", "Step.Review.Description", plan, 3));
        _steps.Add(new WorkflowStepViewModel(WorkflowStep.Apply, "Step.Apply.Title", "Step.Apply.Description", plan, 4));
        _steps.Add(new WorkflowStepViewModel(WorkflowStep.Build, "Step.Build.Title", "Step.Build.Description", build, 5));

        Steps = _steps.AsReadOnly();

        NextCommand = new RelayCommand(_ => GoNext(), _ => CanGoNext);
        BackCommand = new RelayCommand(_ => GoBack(), _ => CanGoBack);
        SelectStepCommand = new RelayCommand(p => GoToStep((WorkflowStep)p!), p => p is WorkflowStep s && CanGoToStep(s));
        FinishCommand = new AsyncRelayCommand(_ => FinishAsync(), _ => CanFinish);

        _appState.PropertyChanged += OnAppStateChanged;
        _build.PropertyChanged += OnBuildChanged;
        RecomputeStates();
    }

    public IReadOnlyList<WorkflowStepViewModel> Steps { get; }

    public WorkflowStepViewModel? CurrentStep =>
        _currentIndex >= 0 && _currentIndex < _steps.Count ? _steps[_currentIndex] : null;

    public event EventHandler? CurrentStepChanged;

    public ICommand NextCommand { get; }

    public ICommand BackCommand { get; }

    public ICommand SelectStepCommand { get; }

    /// <summary>
    /// Ends the wizard cleanly. Only meaningful on the final (Build) step once the
    /// build has actually completed — it navigates to the Home / summary page and
    /// never deletes the produced ISO or any build artifact.
    /// </summary>
    public ICommand FinishCommand { get; }

    /// <summary>True when the active step is the last step (Build).</summary>
    public bool IsFinalStep => _currentIndex == _steps.Count - 1;

    /// <summary>
    /// True only on the final step AND after the build reached
    /// <see cref="BuildState.Completed"/>. A Failed or Cancelled build must not
    /// present a successful "Finish".
    /// </summary>
    public bool CanFinish => IsFinalStep && _build.CurrentStage == BuildState.Completed;

    public bool CanGoNext =>
        CurrentStep is not null
        && _currentIndex < _steps.Count - 1
        && _steps[_currentIndex + 1].State is not WorkflowStepState.NotAvailable;

    public bool CanGoBack => _currentIndex > 0;

    public bool CanGoToStep(WorkflowStep step)
    {
        var target = _steps.FirstOrDefault(s => s.Step == step);
        if (target is null || target.State == WorkflowStepState.NotAvailable)
        {
            return false;
        }

        var targetIndex = _steps.IndexOf(target);
        for (var i = 0; i < targetIndex; i++)
        {
            if (_steps[i].State == WorkflowStepState.NotAvailable)
            {
                return false;
            }
        }

        return true;
    }

    public void GoToStep(WorkflowStep step)
    {
        if (!CanGoToStep(step))
        {
            return;
        }

        var idx = _steps.FindIndex(s => s.Step == step);
        if (idx < 0)
        {
            return;
        }

        _currentIndex = idx;
        RaiseCurrentStepChanged();
        RecomputeStates();
    }

    public void GoNext()
    {
        if (!CanGoNext)
        {
            return;
        }

        _currentIndex++;
        RaiseCurrentStepChanged();
        RecomputeStates();
    }

    public void GoBack()
    {
        if (!CanGoBack)
        {
            return;
        }

        _currentIndex--;
        RaiseCurrentStepChanged();
        RecomputeStates();
    }

    /// <summary>
    /// Completes the workflow (Stage 12.2 Part C/D). Called by
    /// <see cref="FinishCommand"/> only when <see cref="CanFinish"/> is true
    /// (final step + build completed). Runs the authoritative safe cleanup of the
    /// CURRENT completed workspace (final ISO is preserved; recoverable checkpoints
    /// are retained), reports the outcome on the Build step, then navigates Home.
    /// A cleanup failure is a WARNING, never a build failure — the user still
    /// completes the workflow.
    /// </summary>
    private async Task FinishAsync()
    {
        if (!CanFinish)
        {
            return;
        }

        var ws = _appState.CurrentServicingWorkspace;
        if (_lifecycle is not null && ws is not null && !string.IsNullOrWhiteSpace(ws.WorkingDirectory))
        {
            var id = System.IO.Path.GetFileName(ws.WorkingDirectory.TrimEnd('\\', '/')) ?? string.Empty;
            var result = await _lifecycle.CleanupCompletedWorkspaceAsync(id);
            _build.ReportFinishCleanup(result);
        }

        _navigation?.NavigateTo(PageKey.Home);
    }

    private void RaiseCurrentStepChanged()
    {
        OnPropertyChanged(nameof(CurrentStep));
        CurrentStepChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Derives every step's <see cref="WorkflowStepState"/> from shared app state.
    /// The step at the active index is always <see cref="WorkflowStepState.Current"/>
    /// (we never sit on a NotAvailable step); all others are Available / Completed
    /// per their exit criteria, or NotAvailable when prerequisites are missing.
    /// </summary>
    private void RecomputeStates()
    {
        var isMounted = _appState.CurrentServicingWorkspace?.State == ServicingWorkspaceState.Mounted;
        var hasImage = _appState.CurrentImageWorkspace is not null;
        var hasServicing = _appState.CurrentServicingWorkspace is not null;
        var plan = _appState.CurrentCustomizationPlan;
        var planSelected = plan is not null && plan.SelectedOperations.Count > 0;
        var planValidated = plan?.Status == CustomizationPlanStatus.Validated;
        var exec = _appState.CustomizationExecutionState;

        // Execution success means the validated plan was actually applied to the
        // mounted image. That — not merely being Validated — is what completes
        // Review and unlocks the Apply (commit) step. A Validated plan whose
        // "Apply to mounted image" was never run has nothing to commit, so Apply
        // and Next must stay unavailable (this is the corrected contract; it also
        // fixes the real-desktop defect where a successful execution left Review
        // incomplete and Apply unreachable, because execution flips the plan from
        // Validated to Completed and the old gate keyed solely on Validated).
        var execSucceeded = exec is CustomizationExecutionState.Completed
            or CustomizationExecutionState.CompletedWithErrors;

        for (var i = 0; i < _steps.Count; i++)
        {
            var step = _steps[i];
            var isCurrent = i == _currentIndex;

            step.State = step.Step switch
            {
                WorkflowStep.Source => isCurrent
                    ? WorkflowStepState.Current
                    : (hasImage || hasServicing) ? WorkflowStepState.Completed : WorkflowStepState.Available,

                WorkflowStep.Prepare => !hasImage
                    ? WorkflowStepState.NotAvailable
                    : isCurrent ? WorkflowStepState.Current
                    : isMounted ? WorkflowStepState.Completed : WorkflowStepState.Available,

                WorkflowStep.Customize => !isMounted
                    ? WorkflowStepState.NotAvailable
                    : isCurrent ? WorkflowStepState.Current
                    : planSelected ? WorkflowStepState.Completed : WorkflowStepState.Available,

                WorkflowStep.Review => !isMounted || !planSelected
                    ? WorkflowStepState.NotAvailable
                    : isCurrent
                        ? (execSucceeded ? WorkflowStepState.Completed : WorkflowStepState.Current)
                        : (planValidated || execSucceeded) ? WorkflowStepState.Completed
                    : WorkflowStepState.Available,

                WorkflowStep.Apply => !isMounted
                    ? WorkflowStepState.NotAvailable
                    : isCurrent ? WorkflowStepState.Current
                    : execSucceeded ? WorkflowStepState.Available
                    : WorkflowStepState.NotAvailable,

                // Build (Phase 10) is reachable only after the customization plan was
                // actually applied (execution succeeded), never as an always-open
                // placeholder. The step's own CanBuild (mounted + ADK present) further
                // gates the actual build command; this gate controls navigation only.
                // Stage 12.5: a CURRENT Build step whose build reached Completed shows
                // Completed (not InProgress) — the wizard must mirror the build VM's
                // terminal state so the top stepper and Finish stay coherent.
                WorkflowStep.Build => !execSucceeded
                    ? WorkflowStepState.NotAvailable
                    : isCurrent
                        ? (_build.CurrentStage == BuildState.Completed
                            ? WorkflowStepState.Completed
                            : WorkflowStepState.Current)
                        : WorkflowStepState.Available,

                _ => WorkflowStepState.Available
            };
        }

        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsFinalStep));
        OnPropertyChanged(nameof(CanFinish));
        if (NextCommand is RelayCommand next) next.RaiseCanExecuteChanged();
        if (BackCommand is RelayCommand back) back.RaiseCanExecuteChanged();
        if (SelectStepCommand is RelayCommand select) select.RaiseCanExecuteChanged();
        // Stage 12.5: FinishCommand is an AsyncRelayCommand — the earlier
        // `is RelayCommand` type check never matched, so FinishCommand.
        // RaiseCanExecuteChanged() was NEVER called and the Finish button stayed
        // disabled even after a successful build.
        if (FinishCommand is AsyncRelayCommand finish) finish.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// The Build step's completion state lives on <see cref="_build"/>, not on
    /// <see cref="IAppState"/>, so the wizard must react to its property changes
    /// to enable "Finish" and to flip the step to Completed the moment a build
    /// completes. Recomputing the whole step graph also covers navigation gating.
    /// </summary>
    private void OnBuildChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BuildStepViewModel.CurrentStage))
        {
            RecomputeStates();
        }
    }

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IAppState.SourceImagePath) or nameof(IAppState.SelectedEdition))
        {
            InvalidatePlanOnSourceChange();
        }

        if (e.PropertyName is nameof(IAppState.SourceImagePath) or nameof(IAppState.SelectedEdition)
            or nameof(IAppState.CurrentImageWorkspace) or nameof(IAppState.CurrentServicingWorkspace)
            or nameof(IAppState.CurrentCustomizationPlan) or nameof(IAppState.CustomizationExecutionState)
            or nameof(IAppState.DiscoveredInventory))
        {
            RecomputeStates();
        }
    }

    /// <summary>
    /// A source ISO or edition change invalidates any assembled plan and discovery
    /// results, which target the previous image. Skipped while an execution is in
    /// flight (the plan is frozen and must not be silently reset).
    /// </summary>
    private void InvalidatePlanOnSourceChange()
    {
        if (_appState.CustomizationExecutionState == CustomizationExecutionState.Executing)
        {
            return;
        }

        if (_appState.CurrentCustomizationPlan is not null || _appState.DiscoveredInventory is not null)
        {
            _appState.CurrentCustomizationPlan = null;
            _appState.DiscoveredInventory = null;
        }
    }
}
