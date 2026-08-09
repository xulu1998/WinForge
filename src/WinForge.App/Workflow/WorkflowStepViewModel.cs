using WinForge.App.Mvvm;

namespace WinForge.App.Workflow;

/// <summary>
/// View-model for a single step in the Stepper. Carries the localized title /
/// description resource keys (resolved in XAML through the localization service,
/// never here) and the content view model rendered when the step is active.
/// </summary>
public sealed class WorkflowStepViewModel : ViewModelBase
{
    public WorkflowStep Step { get; }

    /// <summary>Resource key for the step title (resolved via the localization service).</summary>
    public string TitleKey { get; }

    /// <summary>Resource key for the step description (resolved via the localization service).</summary>
    public string DescriptionKey { get; }

    /// <summary>The content view model shown when this step is active.</summary>
    public object? Content { get; }

    /// <summary>1-based ordinal used by the Stepper for numbering and ordering.</summary>
    public int Ordinal { get; }

    private WorkflowStepState _state;

    public WorkflowStepState State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    public WorkflowStepViewModel(WorkflowStep step, string titleKey, string descriptionKey, object? content, int ordinal)
    {
        Step = step;
        TitleKey = titleKey;
        DescriptionKey = descriptionKey;
        Content = content;
        Ordinal = ordinal;
    }
}
