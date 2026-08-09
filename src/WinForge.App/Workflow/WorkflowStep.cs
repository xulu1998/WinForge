namespace WinForge.App.Workflow;

/// <summary>
/// The six top-level steps of the WinForge customization workflow. Navigation is
/// strictly sequential through these; the workflow coordinator derives each
/// step's availability from shared application state.
/// </summary>
public enum WorkflowStep
{
    /// <summary>Step 1 — choose the Windows ISO and edition to customize.</summary>
    Source,

    /// <summary>Step 2 — export an isolated working image and mount it.</summary>
    Prepare,

    /// <summary>Step 3 — trim apps, tune services, set privacy/system options.</summary>
    Customize,

    /// <summary>Step 4 — review the assembled declarative plan.</summary>
    Review,

    /// <summary>Step 5 — apply the validated plan to the mounted image.</summary>
    Apply,

    /// <summary>Step 6 — produce the final image (honest placeholder; not yet implemented).</summary>
    Build
}
