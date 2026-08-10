using WinForge.App.Mvvm;
using WinForge.Core.Models;
using WinForge.Core.Services;

namespace WinForge.App.ViewModels;

/// <summary>
/// Build step — an <b>honest placeholder</b>. It deliberately does NOT implement a
/// fake ISO rebuild or image export. The step exists so the workflow is complete
/// end to end; the real export/commit/rebuild is a later phase. No DISM, no file
/// mutation, no silent success — it only reflects whether Apply has completed.
/// </summary>
public sealed class BuildStepViewModel : ViewModelBase
{
    private readonly IAppState _appState;

    public BuildStepViewModel(IAppState appState)
    {
        _appState = appState;
        _appState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAppState.CustomizationExecutionState))
            {
                OnPropertyChanged(nameof(ApplyCompleted));
            }
        };
    }

    /// <summary>True when the Apply step has finished (successfully or with errors).</summary>
    public bool ApplyCompleted => _appState.CustomizationExecutionState is
        CustomizationExecutionState.Completed or CustomizationExecutionState.CompletedWithErrors;
}
