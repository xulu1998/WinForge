using System;

namespace WinForge.Core.Services;

/// <summary>
/// Top-level navigation destinations in the WinForge UI.
/// </summary>
public enum PageKey
{
    Home,
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
