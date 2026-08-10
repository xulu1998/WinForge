namespace WinForge.App.Workflow;

/// <summary>
/// Lifecycle state of a single workflow step, surfaced in the Stepper UI. States
/// are communicated by more than color alone (icon + label + outline) so the
/// workflow is usable for color-blind operators and accessible tooling.
/// </summary>
public enum WorkflowStepState
{
    /// <summary>The step's prerequisites are not met; it cannot be entered.</summary>
    NotAvailable,

    /// <summary>The step can be entered but is not yet complete.</summary>
    Available,

    /// <summary>The step is the one currently shown.</summary>
    Current,

    /// <summary>The step's exit criteria are satisfied.</summary>
    Completed,

    /// <summary>The step is reachable but has a condition that needs operator attention.</summary>
    RequiresAttention
}
