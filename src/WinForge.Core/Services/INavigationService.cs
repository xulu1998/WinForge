using System;

namespace WinForge.Core.Services;

    /// <summary>
    /// Top-level navigation destinations in the WinForge UI.
    /// </summary>
    public enum PageKey
    {
        Home,
        /// <summary>
        /// The sequential wizard/Stepper surface. Represented here (rather than
        /// only as a shell flag) so the navigation coordinator's notion of the
        /// current page stays in sync with the visible surface — without this,
        /// <see cref="WorkflowViewModel.Finish"/> navigating to <see cref="Home"/>
        /// would be a no-op against a stale "Home".
        /// </summary>
        Workflow,
        Image,
    Components,
    Experience,
    Privacy,
    System,
    Plan,
    Build,
    Logs,
    Settings,
    About
}

/// <summary>
/// Contract for navigating between application pages. The interface lives in
/// Core; the concrete implementation lives in the App (UI) project.
/// </summary>
public interface INavigationService
{
    PageKey CurrentPage { get; }

    event EventHandler<PageKey>? CurrentPageChanged;

    void NavigateTo(PageKey page);
}
