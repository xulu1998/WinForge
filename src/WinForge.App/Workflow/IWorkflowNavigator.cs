namespace WinForge.App.Workflow;

/// <summary>
/// Navigation contract for the sequential workflow. The coordinator owns no DISM
/// or servicing logic — it only derives step availability from shared application
/// state and moves the active step. Guards prevent entering a step whose
/// prerequisites are unmet and protect a mounted / dirty / in-flight session.
/// </summary>
public interface IWorkflowNavigator
{
    /// <summary>The ordered workflow steps (Source … Build).</summary>
    System.Collections.Generic.IReadOnlyList<WorkflowStepViewModel> Steps { get; }

    /// <summary>The currently active step, or null before initialization.</summary>
    WorkflowStepViewModel? CurrentStep { get; }

    /// <summary>Raised after the active step changes.</summary>
    event System.EventHandler? CurrentStepChanged;

    /// <summary>True when the Next control may advance to the following step.</summary>
    bool CanGoNext { get; }

    /// <summary>True when the Back control may return to the previous step.</summary>
    bool CanGoBack { get; }

    /// <summary>True when the given step may be entered directly (guards satisfied).</summary>
    bool CanGoToStep(WorkflowStep step);

    /// <summary>Jump to a specific step (no-op when <see cref="CanGoToStep"/> is false).</summary>
    void GoToStep(WorkflowStep step);

    /// <summary>Advance one step (no-op when <see cref="CanGoNext"/> is false).</summary>
    void GoNext();

    /// <summary>Return one step (no-op when <see cref="CanGoBack"/> is false).</summary>
    void GoBack();
}
