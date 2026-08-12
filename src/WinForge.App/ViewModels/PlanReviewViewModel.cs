using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using WinForge.App.Mvvm;
using WinForge.App.Services;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// One row in the Review "selected changes" list (Stage 11.3, ADR-051). It
/// presents the exact ACTION TYPE (移除/禁用/配置/服务/功能), the change name,
/// its offline scope, and the revert contract — so the user understands exactly
/// what will happen before Apply (Part S).
/// </summary>
public sealed class ReviewOperationItem
{
    public string DisplayName { get; init; } = string.Empty;
    public string ActionCaption { get; init; } = string.Empty;
    public string CategoryCaption { get; init; } = string.Empty;
    public string ScopeCaption { get; init; } = string.Empty;
    public string ReversalCaption { get; init; } = string.Empty;
}

/// <summary>
/// Plan review page (Step 3.3 sections K, L, P; Stage 11.3 Part S). Aggregates the
/// shared <see cref="CustomizationPlan"/> into human-readable totals (apps /
/// packages / registry / services + per-action-type counts) and a per-operation
/// list that names the exact action type, scope and revert contract. Surfaces
/// validation warnings, and exposes "Validate plan" and "Apply to mounted image".
/// Execution never begins from a checkbox change alone — only from the explicit
/// Apply command — and the engine leaves the image mounted afterward.
/// </summary>
public sealed class PlanReviewViewModel : ViewModelBase
{
    private readonly IAppState _appState;
    private readonly ILoggerService _logger;
    private readonly ICustomizationExecutionService _execution;
    private readonly ILocalizationService? _loc;
    private readonly Func<CustomizationPlan, IReadOnlyList<string>> _validate;

    private ObservableCollection<string> _warnings = new();
    private string _progressText = string.Empty;
    private string _resultSummary = string.Empty;
    private bool _validationPassed;
    private string _validationMessage = string.Empty;

    public PlanReviewViewModel(
        IAppState appState,
        ILoggerService logger,
        ICustomizationExecutionService execution,
        ILocalizationService? loc = null,
        Func<CustomizationPlan, IReadOnlyList<string>>? validate = null)
    {
        _appState = appState;
        _logger = logger;
        _execution = execution;
        _loc = loc;
        _validate = validate ?? (p => p.Validate());

        Operations = new ObservableCollection<ReviewOperationItem>();
        ValidateCommand = new RelayCommand(_ => ValidatePlan(), _ => CanValidate);
        ApplyCommand = new AsyncRelayCommand(_ => ApplyAsync(), _ => CanApply);
        _appState.PropertyChanged += OnAppStateChanged;
        Refresh();
    }

    public ICommand ValidateCommand { get; }
    public ICommand ApplyCommand { get; }

    public CustomizationPlan? Plan => _appState.CurrentCustomizationPlan;

    /// <summary>The exact per-operation list shown before Apply (Part S).</summary>
    public ObservableCollection<ReviewOperationItem> Operations { get; }

    public bool IsMounted =>
        _appState.CurrentServicingWorkspace?.State == ServicingWorkspaceState.Mounted;

    public int TotalSelected => Plan?.SelectedOperations.Count ?? 0;
    public int TotalApps => CountType(CustomizationOperationType.RemoveProvisionedAppx);
    public int TotalPackages => CountType(CustomizationOperationType.RemovePackage)
                                + CountType(CustomizationOperationType.DisableOptionalFeature)
                                + CountType(CustomizationOperationType.RemoveCapability);
    public int TotalRegistry => CountType(CustomizationOperationType.SetOfflineRegistryValue)
                                + CountType(CustomizationOperationType.DeleteOfflineRegistryValue);
    public int TotalServices => CountType(CustomizationOperationType.ConfigureOfflineService);

    // Stage 11.3 per-action-type totals (Part S) — derived from the operation
    // metadata so Review can say "移除 / 禁用 / 配置 / 服务 / 功能".
    public int TotalRemoves => CountAction(OptimizationAction.Remove);
    public int TotalDisables => CountAction(OptimizationAction.Disable);
    public int TotalConfigures => CountAction(OptimizationAction.Configure);
    public int TotalServiceChanges => CountAction(OptimizationAction.Service);
    public int TotalFeatures => CountAction(OptimizationAction.Feature);

    public ObservableCollection<string> Warnings
    {
        get => _warnings;
        private set
        {
            if (SetField(ref _warnings, value))
            {
                OnPropertyChanged(nameof(HasWarnings));
            }
        }
    }

    public bool HasWarnings => _warnings.Count > 0;

    // ---- Stage 12.2/12.3 real-desktop blocker: visible validation feedback.
    // The old flow only toggled Warnings (whose HasWarnings was never notified),
    // so a failed validation kept showing "没有校验警告" and a successful one gave
    // no feedback at all — Apply stayed disabled with no explanation. ----

    /// <summary>True after ValidatePlan when the plan is Validated and applyable.</summary>
    public bool ValidationPassed
    {
        get => _validationPassed;
        private set => SetField(ref _validationPassed, value);
    }

    /// <summary>Localized success / blocking-failure / exception message.</summary>
    public string ValidationMessage
    {
        get => _validationMessage;
        private set => SetField(ref _validationMessage, value);
    }

    /// <summary>True when a validation outcome message should be shown.</summary>
    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(_validationMessage);

    /// <summary>True when the last validation failed (blocking issues or exception).</summary>
    public bool HasValidationFailure => !_validationPassed && HasValidationMessage;

    public CustomizationExecutionState ExecutionState => _appState.CustomizationExecutionState;

    public string ProgressText
    {
        get => _progressText;
        private set => SetField(ref _progressText, value);
    }

    public string ResultSummary
    {
        get => _resultSummary;
        private set => SetField(ref _resultSummary, value);
    }

    public bool CanValidate =>
        IsMounted && Plan is not null &&
        Plan.Status is CustomizationPlanStatus.Draft or CustomizationPlanStatus.Validated &&
        TotalSelected > 0;

    public bool CanApply => IsMounted && Plan?.Status == CustomizationPlanStatus.Validated;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Plan));
        OnPropertyChanged(nameof(IsMounted));
        OnPropertyChanged(nameof(TotalSelected));
        OnPropertyChanged(nameof(TotalApps));
        OnPropertyChanged(nameof(TotalPackages));
        OnPropertyChanged(nameof(TotalRegistry));
        OnPropertyChanged(nameof(TotalServices));
        OnPropertyChanged(nameof(TotalRemoves));
        OnPropertyChanged(nameof(TotalDisables));
        OnPropertyChanged(nameof(TotalConfigures));
        OnPropertyChanged(nameof(TotalServiceChanges));
        OnPropertyChanged(nameof(TotalFeatures));
        OnPropertyChanged(nameof(ExecutionState));
        OnPropertyChanged(nameof(CanValidate));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(ValidationPassed));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(HasValidationMessage));
        OnPropertyChanged(nameof(HasValidationFailure));
        RebuildOperations();
        if (ValidateCommand is RelayCommand v) v.RaiseCanExecuteChanged();
        if (ApplyCommand is AsyncRelayCommand a) a.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Rebuilds the per-operation review list so every selected change is shown
    /// with its exact action type, category, offline scope, and revert contract.
    /// </summary>
    private void RebuildOperations()
    {
        Operations.Clear();
        if (Plan is null)
        {
            return;
        }

        foreach (var op in Plan.SelectedOperations)
        {
            Operations.Add(new ReviewOperationItem
            {
                DisplayName = op.DisplayName,
                ActionCaption = _loc is null ? ResolveAction(op).ToString() : _loc["Opt.Action." + ResolveAction(op)],
                CategoryCaption = _loc is null ? op.Category.ToString() : _loc[CategoryKey(op.Category)],
                ScopeCaption = op.Scope is null
                    ? string.Empty
                    : (_loc is null ? op.Scope.Value.ToString() : _loc["Opt.Scope." + op.Scope.Value]),
                ReversalCaption = string.IsNullOrWhiteSpace(op.ReversalKey)
                    ? (_loc is null ? string.Empty : _loc["Plan.Reversal.Generic"])
                    : _loc![op.ReversalKey!],
            });
        }
    }

    private static string CategoryKey(CustomizationCategory category) => category switch
    {
        CustomizationCategory.App => "Customize.Tab.Apps",
        CustomizationCategory.Package => "Customize.Tab.Components",
        CustomizationCategory.Privacy => "Customize.Tab.Privacy",
        CustomizationCategory.System => "Customize.Tab.System",
        CustomizationCategory.Service => "Customize.Tab.Services",
        CustomizationCategory.Personalization => "Customize.Tab.Personalization",
        _ => "Customize.Tab.System",
    };

    private static OptimizationAction ResolveAction(CustomizationOperation op)
    {
        if (op.ActionKind is { } action && action != OptimizationAction.Unknown)
        {
            return action;
        }

        return op.OperationType switch
        {
            CustomizationOperationType.RemoveProvisionedAppx or CustomizationOperationType.RemovePackage
                or CustomizationOperationType.RemoveOfflineFile or CustomizationOperationType.RemoveCapability
                => OptimizationAction.Remove,
            CustomizationOperationType.DisableOptionalFeature or CustomizationOperationType.DisableOfflineScheduledTask
                => OptimizationAction.Feature,
            CustomizationOperationType.ConfigureOfflineService => OptimizationAction.Service,
            _ => OptimizationAction.Configure,
        };
    }

    private int CountAction(OptimizationAction action)
        => Plan is null ? 0 : Plan.SelectedOperations.Count(o => ResolveAction(o) == action);

    public void ValidatePlan()
    {
        if (!CanValidate || Plan is null)
        {
            return;
        }

        try
        {
            var issues = _validate(Plan);
            Warnings = new ObservableCollection<string>(issues);
            ValidationPassed = issues.Count == 0 && Plan.Status == CustomizationPlanStatus.Validated;
            ValidationMessage = ValidationPassed
                ? Localize("Review.ValidatePassed")
                : string.Format(Localize("Review.ValidateFailed"), issues.Count);
            _appState.CustomizationExecutionState = CustomizationExecutionState.Ready;
            _logger.Info(ValidationPassed
                ? "Plan: validated successfully."
                : $"Plan: validation failed with {issues.Count} issue(s).");
        }
        catch (Exception ex)
        {
            // NO SILENT FAILURES (real-desktop blocker requirement): a throwing
            // validator surfaces the exact error instead of leaving Apply dead
            // with no explanation.
            ValidationPassed = false;
            ValidationMessage = string.Format(Localize("Review.ValidateError"), ex.Message);
            Warnings = new ObservableCollection<string> { ex.Message };
            _logger.Error($"Plan: validation threw: {ex}");
        }

        OnPropertyChanged(nameof(HasValidationMessage));
        OnPropertyChanged(nameof(HasValidationFailure));
        Refresh();
    }

    private string Localize(string key) => _loc is null ? key : _loc[key];

    public async Task ApplyAsync()
    {
        if (!CanApply || Plan is null || _appState.CurrentServicingWorkspace is null)
        {
            return;
        }

        _appState.CustomizationExecutionState = CustomizationExecutionState.Executing;
        ProgressText = "Applying plan to the mounted image…";
        ResultSummary = string.Empty;
        Refresh();

        var progress = new Progress<ExecutionProgress>(p =>
        {
            ProgressText = $"Applying {p.Completed + 1} of {p.Total}: {p.CurrentOperation}";
        });

        try
        {
            var result = await _execution.ExecuteAsync(
                Plan, _appState.CurrentServicingWorkspace, progress, CancellationToken.None);

            _appState.CustomizationExecutionState = result.CriticalFailure
                ? CustomizationExecutionState.Failed
                : (result.Success
                    ? CustomizationExecutionState.Completed
                    : (result.FailedOperations > 0
                        ? CustomizationExecutionState.CompletedWithErrors
                        : CustomizationExecutionState.Completed));

            ResultSummary = result.Summary ?? "Done.";
            _logger.Info($"Plan: applied ({result.Succeeded} succeeded, {result.FailedOperations} failed).");
        }
        catch (System.Exception ex)
        {
            _appState.CustomizationExecutionState = CustomizationExecutionState.Failed;
            ResultSummary = $"Apply failed: {ex.Message}";
            _logger.Error($"Plan: apply failed: {ex.Message}");
        }
        finally
        {
            ProgressText = string.Empty;
            Refresh();
        }
    }

    private int CountType(CustomizationOperationType type)
        => Plan is null ? 0 : Plan.SelectedOperations.Count(o => o.OperationType == type);

    private void OnAppStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IAppState.CurrentCustomizationPlan)
            or nameof(IAppState.CurrentServicingWorkspace)
            or nameof(IAppState.CustomizationExecutionState))
        {
            Refresh();
        }
    }
}
