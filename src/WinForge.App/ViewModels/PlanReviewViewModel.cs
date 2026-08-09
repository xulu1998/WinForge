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
/// Plan review page (Step 3.3 sections K, L, P). Aggregates the shared
/// <see cref="CustomizationPlan"/> into human-readable totals (apps / packages /
/// registry / services), surfaces validation warnings, and exposes "Validate
/// plan" and "Apply to mounted image". Execution never begins from a checkbox
/// change alone — only from the explicit Apply command — and the engine leaves
/// the image mounted afterward.
/// </summary>
public sealed class PlanReviewViewModel : ViewModelBase
{
    private readonly IAppState _appState;
    private readonly ILoggerService _logger;
    private readonly ICustomizationExecutionService _execution;

    private ObservableCollection<string> _warnings = new();
    private string _progressText = string.Empty;
    private string _resultSummary = string.Empty;

    public PlanReviewViewModel(
        IAppState appState,
        ILoggerService logger,
        ICustomizationExecutionService execution)
    {
        _appState = appState;
        _logger = logger;
        _execution = execution;

        ValidateCommand = new RelayCommand(_ => ValidatePlan(), _ => CanValidate);
        ApplyCommand = new AsyncRelayCommand(_ => ApplyAsync(), _ => CanApply);
        _appState.PropertyChanged += OnAppStateChanged;
        Refresh();
    }

    public ICommand ValidateCommand { get; }
    public ICommand ApplyCommand { get; }

    public CustomizationPlan? Plan => _appState.CurrentCustomizationPlan;

    public bool IsMounted =>
        _appState.CurrentServicingWorkspace?.State == ServicingWorkspaceState.Mounted;

    public int TotalSelected => Plan?.SelectedOperations.Count ?? 0;
    public int TotalApps => CountType(CustomizationOperationType.RemoveProvisionedAppx);
    public int TotalPackages => CountType(CustomizationOperationType.RemovePackage);
    public int TotalRegistry => CountType(CustomizationOperationType.SetOfflineRegistryValue)
                                + CountType(CustomizationOperationType.DeleteOfflineRegistryValue);
    public int TotalServices => CountType(CustomizationOperationType.ConfigureOfflineService);

    public ObservableCollection<string> Warnings
    {
        get => _warnings;
        private set => SetField(ref _warnings, value);
    }

    public bool HasWarnings => _warnings.Count > 0;

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
        OnPropertyChanged(nameof(ExecutionState));
        OnPropertyChanged(nameof(CanValidate));
        OnPropertyChanged(nameof(CanApply));
        if (ValidateCommand is RelayCommand v) v.RaiseCanExecuteChanged();
        if (ApplyCommand is AsyncRelayCommand a) a.RaiseCanExecuteChanged();
    }

    public void ValidatePlan()
    {
        if (!CanValidate || Plan is null)
        {
            return;
        }

        var issues = Plan.RecomputeValidation();
        var validateIssues = Plan.Validate();
        Warnings = new ObservableCollection<string>(validateIssues);
        _appState.CustomizationExecutionState = CustomizationExecutionState.Ready;
        _logger.Info(Plan.Status == CustomizationPlanStatus.Validated
            ? "Plan: validated successfully."
            : $"Plan: validation failed with {validateIssues.Count} issue(s).");
        Refresh();
    }

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
