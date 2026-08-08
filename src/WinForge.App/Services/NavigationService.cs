using System;
using WinForge.Core.Services;

namespace WinForge.App.Services;

/// <summary>
/// Concrete navigation service. Tracks the current page, raises change
/// notifications, and records navigation in the application log. Lives in the
/// App (UI) project because it depends on the logging service. Core only knows
/// the <see cref="INavigationService"/> contract.
/// </summary>
public sealed class NavigationService : INavigationService
{
    private readonly ILoggerService _logger;
    private PageKey _currentPage = PageKey.Home;

    public NavigationService(ILoggerService logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public PageKey CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (_currentPage == value)
            {
                return;
            }

            _currentPage = value;
            CurrentPageChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<PageKey>? CurrentPageChanged;

    public void NavigateTo(PageKey page)
    {
        if (_currentPage == page)
        {
            return;
        }

        CurrentPage = page;
        _logger.Info($"Navigation changed to {page}");
    }
}
